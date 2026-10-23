#!/usr/bin/env node
// Deal or Force - project knowledge as MCP tools.
//
// The repository carries its own memory: a master plan whose rows carry the status
// vocabulary, dated development passes, an ideas backlog, a define-driven simulation whose
// every tunable constant lives in Scripts/Simulation/Defines.cs, and the game data under
// game/common. Answering "what is the state of X" or "what does define Y do" today means
// grepping and re-parsing those by hand. This server exposes them as tools, with the same
// deliberately dependency-free stdio transport as Tools/game-mcp/server.mjs - newline
// delimited JSON-RPC, no SDK, nothing to install.
//
// Smoke-test it by hand:
//   node Tools/knowledge-mcp/server.mjs
//   {"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
//   {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
//   {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"plan_status","arguments":{"filter":"UI-09"}}}

import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const project = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

// The game's data files carry `//` comments, so a plain JSON.parse is not enough.
function readJson(path) {
  return JSON.parse(readFileSync(path, 'utf8').replace(/^\s*\/\/.*$/gm, ''));
}

const DOC_EXT = new Set(['.md', '.json', '.yaml', '.yml', '.gd', '.cs', '.gdshader', '.cfg', '.txt']);
const SKIP_DIRS = new Set(['.git', '.godot', '.fennara', 'build', 'node_modules', '.zcode', '.devin', '.agents', '.cursor', '.mimosa']);

function walk(root, limit, acc = []) {
  if (!existsSync(root) || acc.length >= limit) return acc;
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    if (acc.length >= limit) break;
    if (entry.name.startsWith('.') || SKIP_DIRS.has(entry.name)) continue;
    const full = join(root, entry.name);
    if (entry.isDirectory()) walk(full, limit, acc);
    else if (DOC_EXT.has(entry.name.slice(entry.name.lastIndexOf('.')).toLowerCase()) && statSync(full).size < 2_000_000) acc.push(full);
  }
  return acc;
}

const STATUSES = ['проверено', 'частично', 'в работе', 'очередь', 'внешняя ошибка'];

const tools = [
  {
    name: 'search_docs',
    description: 'Search the project text (docs, game data, Tools) for a substring or regex and report file:line hits. The way to find which plan, pass or data file already mentions something.',
    inputSchema: {
      type: 'object',
      properties: {
        query: { type: 'string', description: 'Text to look for (case-insensitive), or a regex when regex is true.' },
        regex: { type: 'boolean', description: 'Treat query as a regular expression. Default false.' },
        root: { type: 'string', description: 'Subfolder to search: docs (default), game, Tools, Scripts, or "all".' },
        limit: { type: 'number', description: 'Max hits. Default 25.' }
      },
      required: ['query']
    }
  },
  {
    name: 'plan_status',
    description: 'Read docs/MASTER_DEVELOPMENT_PLAN_RU.md and report its item rows: id, section, statuses and text. The current control list - what is проверено, частично, в работе, очередь, внешняя ошибка.',
    inputSchema: {
      type: 'object',
      properties: {
        filter: { type: 'string', description: 'Only rows whose text contains this substring, e.g. "UI-" or "фронт".' },
        status: { type: 'string', description: 'Only rows carrying this status: проверено | частично | в работе | очередь | внешняя ошибка.' },
        counts_only: { type: 'boolean', description: 'Return just the per-status row counts.' }
      }
    }
  },
  {
    name: 'list_defines',
    description: 'List simulation tunables from Scripts/Simulation/Defines.cs: name, type, default value and the first line of the doc comment that explains why it exists. Balance questions start here.',
    inputSchema: {
      type: 'object',
      properties: { pattern: { type: 'string', description: 'Case-insensitive regex against the property name, e.g. "front|order".' } }
    }
  },
  {
    name: 'backlog_ideas',
    description: 'List the recorded early ideas (docs/IDEAS_BACKLOG_RU.md) with their one-line gist, so a new proposal can build on them instead of rediscovering them.',
    inputSchema: { type: 'object', properties: {} }
  },
  {
    name: 'recent_work',
    description: 'List dated development documents (passes and topical reports) newest first, with the title from their first heading.',
    inputSchema: {
      type: 'object',
      properties: { limit: { type: 'number', description: 'How many. Default 12.' } }
    }
  },
  {
    name: 'data_stats',
    description: 'Count what the game data actually contains, per game/common category: files and top-level entries. With a category, per-file counts.',
    inputSchema: {
      type: 'object',
      properties: { category: { type: 'string', description: 'One category, e.g. "technologies", "buildings", "decisions".' } }
    }
  }
];

async function call(name, args) {
  if (name === 'search_docs') {
    const query = String(args.query ?? '');
    if (!query) throw new Error('query is required');
    const matcher = args.regex
      ? new RegExp(query, 'i')
      : new RegExp(query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i');
    const root = { docs: 'docs', game: 'game', Tools: 'Tools', Scripts: 'Scripts' }[args.root] ?? 'docs';
    const limit = Math.min(Number(args.limit) || 25, 100);
    const files = walk(join(project, root), 3000);
    const hits = [];
    for (const file of files) {
      let lines;
      try { lines = readFileSync(file, 'utf8').split('\n'); } catch { continue; }
      for (let i = 0; i < lines.length; i++) {
        if (!matcher.test(lines[i])) continue;
        const rel = relative(project, file).replaceAll('\\', '/');
        hits.push(`${rel}:${i + 1}: ${lines[i].trim().slice(0, 220)}`);
        if (hits.length >= limit) break;
      }
      if (hits.length >= limit) break;
    }
    return hits.length ? `${hits.length} hit(s) in ${root}\n${hits.join('\n')}` : `no hits for "${query}" under ${root}`;
  }

  if (name === 'plan_status') {
    const text = readFileSync(join(project, 'docs', 'MASTER_DEVELOPMENT_PLAN_RU.md'), 'utf8');
    const rows = [];
    let section = '';
    for (const line of text.split('\n')) {
      if (line.startsWith('## ')) { section = line.slice(3).trim(); continue; }
      const id = /^\|\s*([A-Z]{2,6}-\d{1,3})\s*\|/.exec(line);
      if (!id) continue;
      const found = STATUSES.filter((s) => line.toLowerCase().includes(s));
      rows.push({ id: id[1], section, statuses: found, text: line.replace(/\s*\|/g, ' |').trim().slice(0, 300) });
    }
    if (args.counts_only) {
      const counts = Object.fromEntries(STATUSES.map((s) => [s, rows.filter((r) => r.statuses.includes(s)).length]));
      return `${rows.length} plan rows\n${JSON.stringify(counts, null, 2)}`;
    }
    let out = rows;
    if (args.status) out = out.filter((r) => r.statuses.includes(String(args.status).toLowerCase()));
    if (args.filter) out = out.filter((r) => r.text.toLowerCase().includes(String(args.filter).toLowerCase()) || r.id.toLowerCase().includes(String(args.filter).toLowerCase()));
    const body = out.slice(0, 60).map((r) => `${r.id} [${r.statuses.join('/') || 'без статуса'}] (${r.section})\n  ${r.text}`).join('\n');
    return `${out.length} of ${rows.length} plan rows\n${body}`;
  }

  if (name === 'list_defines') {
    const dir = join(project, 'Scripts', 'Simulation');
    const files = readdirSync(dir).filter((f) => /^Defines.*\.cs$/.test(f)).sort();
    const pattern = args.pattern ? new RegExp(String(args.pattern), 'i') : null;
    const out = [];
    let summary = '';
    for (const file of files) {
      let pending = '';
      for (const line of readFileSync(join(dir, file), 'utf8').split('\n')) {
        const doc = /^\/\/\/?\s*(?:<summary>)?\s*(.+?)\s*(?:<\/summary>)?$/.exec(line.trim());
        if (doc && doc[1] && !doc[1].startsWith('<')) { pending ||= doc[1]; continue; }
        const prop = /public\s+(?:[\w<>?,\[\]\s.]+?)\s+(\w+)\s*\{\s*get;\s*set;\s*\}\s*=\s*([^;]+);/.exec(line);
        if (prop) {
          if (!pattern || pattern.test(prop[1])) {
            out.push(`${prop[1]}: ${prop[2].trim()}${pending ? `  // ${pending}` : ''}`);
            if (out.length >= 80) break;
          }
          pending = '';
        } else if (line.trim() && !line.trim().startsWith('/') && !line.trim().startsWith('*')) pending = '';
      }
      if (out.length >= 80) break;
    }
    return out.length ? `${out.length} define(s)\n${out.join('\n')}` : 'no defines matched';
  }

  if (name === 'backlog_ideas') {
    const text = readFileSync(join(project, 'docs', 'IDEAS_BACKLOG_RU.md'), 'utf8');
    const out = [];
    let heading = '';
    for (const line of text.split('\n')) {
      if (/^#{2,3}\s/.test(line)) { heading = line.replace(/^#+\s*/, '').trim(); continue; }
      // Only numbered entries are ideas; service sections like "Общие ограничения" are not.
      if (heading && /^\d+\./.test(heading) && line.trim() && !out.some((o) => o.startsWith(heading))) out.push(`${heading}\n  ${line.trim().slice(0, 220)}`);
    }
    return out.length ? `${out.length} idea(s)\n${out.join('\n')}` : 'no idea headings found';
  }

  if (name === 'recent_work') {
    const limit = Math.min(Number(args.limit) || 12, 40);
    const rows = [];
    for (const file of readdirSync(join(project, 'docs'))) {
      const date = /(\d{4})_(\d{2})_(\d{2})/.exec(file);
      if (!date || !file.endsWith('.md')) continue;
      let title = '';
      try {
        title = /^#\s+(.+)$/m.exec(readFileSync(join(project, 'docs', file), 'utf8'))?.[1]?.trim() ?? '';
      } catch { /* unreadable doc is not a reason to fail the listing */ }
      rows.push({ key: `${date[1]}-${date[2]}-${date[3]}`, file, title });
    }
    rows.sort((a, b) => b.key.localeCompare(a.key));
    return rows.slice(0, limit).map((r) => `${r.key}  ${r.file}${r.title ? `  — ${r.title}` : ''}`).join('\n');
  }

  if (name === 'data_stats') {
    const root = join(project, 'game', 'common');
    if (args.category) {
      const dir = join(root, String(args.category));
      if (!existsSync(dir)) throw new Error(`no such category: ${args.category} (see data_stats without arguments for the list)`);
      const out = [];
      let total = 0;
      for (const file of readdirSync(dir).filter((f) => f.endsWith('.json')).sort()) {
        try {
          const keys = Object.keys(readJson(join(dir, file))).length;
          total += keys;
          out.push(`${file}: ${keys}`);
        } catch { out.push(`${file}: unparsable`); }
      }
      return `${args.category}: ${total} entries\n${out.join('\n')}`;
    }
    const out = [];
    for (const category of readdirSync(root).sort()) {
      const dir = join(root, category);
      if (!statSync(dir).isDirectory()) continue;
      let files = 0, keys = 0;
      for (const file of readdirSync(dir).filter((f) => f.endsWith('.json'))) {
        files++;
        try { keys += Object.keys(readJson(join(dir, file))).length; } catch { /* counted as files only */ }
      }
      out.push(`${category}: ${files} file(s), ${keys} entries`);
    }
    return `${out.length} categories\n${out.join('\n')}`;
  }

  throw new Error(`unknown tool: ${name}`);
}

// ---------------------------------------------------------------- MCP stdio transport
function send(payload) {
  process.stdout.write(JSON.stringify(payload) + '\n');
}

async function handle(message) {
  const { id, method, params } = message;
  if (method === 'initialize') {
    send({ jsonrpc: '2.0', id, result: {
      protocolVersion: params?.protocolVersion ?? '2025-06-18',
      capabilities: { tools: {} },
      serverInfo: { name: 'deal-or-force-knowledge', version: '1.0.0' }
    } });
    return;
  }
  if (method === 'notifications/initialized' || method === 'notifications/cancelled') return;
  if (method === 'ping') { send({ jsonrpc: '2.0', id, result: {} }); return; }
  if (method === 'tools/list') { send({ jsonrpc: '2.0', id, result: { tools } }); return; }
  if (method === 'tools/call') {
    try {
      const text = await call(params?.name, params?.arguments ?? {});
      send({ jsonrpc: '2.0', id, result: { content: [{ type: 'text', text }] } });
    } catch (error) {
      send({ jsonrpc: '2.0', id, result: { content: [{ type: 'text', text: String(error?.message ?? error) }], isError: true } });
    }
    return;
  }
  if (id !== undefined) send({ jsonrpc: '2.0', id, error: { code: -32601, message: `unknown method: ${method}` } });
}

let buffer = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', async (chunk) => {
  buffer += chunk;
  let newline;
  while ((newline = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (!line) continue;
    let message;
    try { message = JSON.parse(line); } catch { continue; }
    await handle(message);
  }
});
