#!/usr/bin/env node
// Deal or Force - inventory every MCP server and agent skill on this machine, per harness.
//
// MCP is configured in eleven different places on this box and they drift: a server added to
// OpenCode is invisible to Qwen, a skill written for Antigravity never reaches Devin. Nobody
// can see that from inside one harness. This tool reads all of them read-only and prints the
// matrix, so "port it" stops meaning "remember which file that was".
//
// Nothing here writes. --emit <harness> prints copy-pasteable JSON for the servers that
// harness is missing; --apply is deliberately absent, because a config outside the project
// belongs to whoever owns it.
//
// Usage:
//   node Tools/mcp-survey.mjs                     matrix of servers and skills
//   node Tools/mcp-survey.mjs --emit qwen         JSON blocks qwen lacks
//   node Tools/mcp-survey.mjs --plugins           every skill inside an installed plugin cache
//   node Tools/mcp-survey.mjs --plugins game      ...filtered, and which matches no harness has
//   node Tools/mcp-survey.mjs --json              machine-readable matrix

import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const project = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const home = homedir();
const asJson = process.argv.includes('--json');
const emitIdx = process.argv.indexOf('--emit');
const emitFor = emitIdx === -1 ? null : process.argv[emitIdx + 1];
const pluginsIdx = process.argv.indexOf('--plugins');
const pluginsFor = pluginsIdx === -1 ? null : (process.argv[pluginsIdx + 1] ?? '');

// ---------------------------------------------------------------- harness map

// Each harness reads MCP from a different filename and a different key. `kind` says how to
// get servers out of it; `scope` records whether the file is machine-wide or per-project.
const harnesses = [
  { id: 'qwen', label: 'Qwen Code (user)', kind: 'json', scope: 'user', file: join(home, '.qwen', 'settings.json'), key: ['mcpServers'] },
  { id: 'qwen', label: 'Qwen Code (project)', kind: 'json', scope: 'project', file: join(project, '.mcp.json'), key: ['mcpServers'] },
  { id: 'claude-code', label: 'Claude Code (user)', kind: 'json', scope: 'user', file: join(home, '.claude.json'), key: ['mcpServers'], alsoProjects: true },
  { id: 'claude-desktop', label: 'Claude Desktop', kind: 'json', scope: 'user', file: join(home, 'AppData', 'Roaming', 'Claude', 'claude_desktop_config.json'), key: ['mcpServers'] },
  { id: 'cursor', label: 'Cursor (user)', kind: 'json', scope: 'user', file: join(home, '.cursor', 'mcp.json'), key: ['mcpServers'] },
  { id: 'windsurf', label: 'Windsurf', kind: 'json', scope: 'user', file: join(home, '.codeium', 'windsurf', 'mcp_config.json'), key: ['mcpServers'] },
  { id: 'gemini', label: 'Gemini CLI / Antigravity', kind: 'json', scope: 'user', file: join(home, '.gemini', 'settings.json'), key: ['mcpServers'] },
  { id: 'codex', label: 'Codex CLI', kind: 'toml', scope: 'user', file: join(home, '.codex', 'config.toml') },
  { id: 'opencode', label: 'OpenCode', kind: 'json', scope: 'user', file: join(home, '.config', 'opencode', 'opencode.json'), key: ['mcp'] },
  { id: 'kiro', label: 'Kiro', kind: 'json', scope: 'user', file: join(home, '.kiro', 'settings', 'mcp.json'), key: ['mcpServers'] },
  { id: 'cline', label: 'Cline / ZCode (project)', kind: 'json', scope: 'project', file: join(project, '.mcp.json'), key: ['mcpServers'] },
  { id: 'devin', label: 'Devin (project)', kind: 'json', scope: 'project', file: join(project, '.devin', 'mcp_config.local.json'), key: ['mcpServers'] },
];

// Skills live in per-harness folders. `.agents` is the source the project's own
// harness-sync.mjs copies from; the rest are either copies or independent sets.
const skillRoots = [
  { id: 'agents', label: '.agents (project, source)', dir: join(project, '.agents', 'skills') },
  { id: 'cursor', label: '.cursor (project)', dir: join(project, '.cursor', 'skills') },
  { id: 'devin', label: '.devin (project)', dir: join(project, '.devin', 'skills') },
  { id: 'qwen-user', label: '~/.qwen (user)', dir: join(home, '.qwen', 'skills') },
  { id: 'claude-user', label: '~/.claude (user)', dir: join(home, '.claude', 'skills') },
  { id: 'gemini-user', label: '~/.gemini (user)', dir: join(home, '.gemini', 'skills') },
  { id: 'opencode-user', label: '~/.config/opencode (user)', dir: join(home, '.config', 'opencode', 'skills') },
];

// Plugin caches hold the skills that installed plugins ship, at
// <cache>/<publisher>/<plugin>/<version>/skills/<skill>. Nothing lists them, so several hundred
// skills were invisible to every harness's own picker - including two project skills that had
// been written into one cache and never reached the repository. Read-only, and depth-capped:
// the same caches hold whole checkouts, and walking all of them is slow for no benefit.
const pluginCaches = [join(home, '.codex', 'plugins', 'cache'), join(home, '.claude', 'plugins', 'cache')];

function findSkillDirs(dir, depth, out) {
  if (depth > 4) return out;
  let entries;
  try { entries = readdirSync(dir, { withFileTypes: true }); } catch { return out; }
  for (const entry of entries) {
    if (!entry.isDirectory()) continue;
    const full = join(dir, entry.name);
    // Stop at a `skills` folder: its children are the skills themselves, and descending into
    // those walks each skill's own assets and scripts for nothing.
    if (entry.name === 'skills') { out.push(full); continue; }
    findSkillDirs(full, depth + 1, out);
  }
  return out;
}

function pluginSkills() {
  const byPlugin = new Map();
  for (const cache of pluginCaches) {
    if (!existsSync(cache)) continue;
    for (const dir of findSkillDirs(cache, 0, [])) {
      const parts = relative(cache, dir).split(/[\\/]+/);
      const plugin = parts.length >= 3 ? `${parts[0]}/${parts[1]}` : parts.join('/');
      let names;
      try {
        names = readdirSync(dir, { withFileTypes: true }).filter((e) => e.isDirectory()).map((e) => e.name);
      } catch { continue; }
      const known = byPlugin.get(plugin) ?? new Set();
      for (const name of names) known.add(name);
      byPlugin.set(plugin, known);
    }
  }
  return byPlugin;
}

// ------------------------------------------------------------------- parsing

function readText(file) {
  try { return readFileSync(file, 'utf8'); } catch { return null; }
}

function parseJson(file) {
  const text = readText(file);
  if (text === null) return { error: 'missing' };
  try { return { data: JSON.parse(text) }; }
  catch (e) { return { error: `unparseable: ${e.message}` }; }
}

// Codex and friends use TOML. Only the [mcp_servers.name] tables matter here, so a line
// scanner is enough - pulling in a TOML parser would be a dependency for one table shape.
// A deeper table such as [mcp_servers.blender.env] belongs to its parent server, and
// treating it as a server of its own is how phantom rows like "blender.env" appear.
function parseTomlMcp(file) {
  const text = readText(file);
  if (text === null) return { error: 'missing' };
  const out = {};
  let current = null;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    const table = line.match(/^\[([^\]]+)\]$/);
    if (table) {
      // Codex quotes names that contain a dash: [mcp_servers."ziva-godot"]. Keeping the
      // quotes would invent a second server beside the unquoted spelling of the same one.
      const parts = table[1].split('.').map((p) => p.trim().replace(/^"(.*)"$/, '$1'));
      current = null;
      if (parts[0] === 'mcp_servers' && parts.length >= 2) {
        current = out[parts[1]] ??= {};
        // Walk into the sub-table so `env` keys land under their server, not beside it.
        for (const sub of parts.slice(2)) current = current[sub] ??= {};
      }
      continue;
    }
    if (!current) continue;
    const kv = line.match(/^([A-Za-z0-9_-]+)\s*=\s*(.+)$/);
    if (!kv) continue;
    let value = kv[2].trim();
    if (value.startsWith('"') && value.endsWith('"')) value = value.slice(1, -1);
    else if (value.startsWith('[')) {
      try { value = JSON.parse(value.replace(/'/g, '"')); } catch { value = value.slice(1, -1).split(',').map((s) => s.trim().replace(/^["']|["']$/g, '')).filter(Boolean); }
    }
    current[kv[1]] = value;
  }
  return { data: { mcpServers: out } };
}

// One fingerprint per server so "same server, different harness" is a comparison and not
// a guess. Harnesses disagree on shape - OpenCode puts the whole argv in `command` and
// spells env `environment`, Claude Code splits `command`/`args` - so normalise first and
// compare what actually runs, not how the file happens to be written.
function fingerprint(spec) {
  if (!spec || typeof spec !== 'object') return 'invalid';
  const argv = Array.isArray(spec.command)
    ? spec.command
    : [spec.command, ...(Array.isArray(spec.args) ? spec.args : spec.args ? [spec.args] : [])].filter(Boolean);
  const parts = [];
  if (argv.length) parts.push(argv.join(' '));
  if (spec.url) parts.push(spec.url);
  if (spec.type === 'http' || spec.type === 'sse' || spec.type === 'remote') parts.push('transport:remote');
  if (spec.disabled === true || spec.enabled === false) parts.push('DISABLED');
  return parts.join(' | ').replace(/\s+/g, ' ').trim() || 'empty';
}

function isOff(spec) {
  return Boolean(spec) && (spec.disabled === true || spec.enabled === false);
}

function loadHarness(h) {
  const parsed = h.kind === 'toml' ? parseTomlMcp(h.file) : parseJson(h.file);
  if (parsed.error) return { ...h, servers: {}, error: parsed.error };
  const servers = {};
  for (const path of h.key ?? ['mcpServers']) {
    const bag = path.split('.').reduce((o, k) => (o ? o[k] : undefined), parsed.data);
    if (!bag || typeof bag !== 'object') continue;
    for (const [name, spec] of Object.entries(bag)) {
      // OpenCode's file carries a nested `servers` object beside its flat entries. Read as
      // written, that is one bogus server called "servers" hiding ten real ones - which is
      // exactly how a config ends up silently loading nothing.
      if (name === 'servers' && spec && typeof spec === 'object' && !spec.command && !spec.url) {
        Object.assign(servers, spec);
        continue;
      }
      servers[name] = spec;
    }
  }
  // Claude Code also stores per-project servers under projects.<path>.mcpServers.
  if (h.alsoProjects && parsed.data.projects) {
    for (const [path, conf] of Object.entries(parsed.data.projects)) {
      if (!conf || typeof conf !== 'object' || !conf.mcpServers) continue;
      const relevant = path.toLowerCase() === project.toLowerCase();
      for (const [name, spec] of Object.entries(conf.mcpServers)) {
        servers[relevant ? name : `${name} @${shorten(path)}`] = spec;
      }
    }
  }
  return { ...h, servers, error: null };
}

function shorten(p) {
  const tail = p.split(/[\\/]/).filter(Boolean).slice(-2).join('/');
  return tail || p;
}

function listSkills(dir) {
  if (!existsSync(dir)) return null;
  return readdirSync(dir).filter((n) => statSync(join(dir, n)).isDirectory()).sort();
}

// --------------------------------------------------------------------- report

const loaded = harnesses.map(loadHarness);
const byId = new Map();
for (const h of loaded) {
  const prev = byId.get(h.id) ?? { id: h.id, label: h.label, servers: {}, errors: [] };
  if (h.error) prev.errors.push(`${h.scope}: ${h.error}`);
  for (const [name, spec] of Object.entries(h.servers)) {
    if (name.endsWith(` @${shorten(project)}`) || !name.includes(' @')) prev.servers[name] = spec;
    else prev.servers[name] = prev.servers[name] ?? spec;
  }
  prev.label = prev.label.includes('(') ? prev.label : h.label;
  byId.set(h.id, prev);
}
const harnessIds = [...byId.keys()];
const serverNames = [...new Set(harnessIds.flatMap((id) => Object.keys(byId.get(id).servers)))].sort();

const skillSets = skillRoots.map((r) => ({ ...r, skills: listSkills(r.dir) }));
const skillNames = [...new Set(skillSets.flatMap((s) => s.skills ?? []))].sort();

if (asJson) {
  console.log(JSON.stringify({
    servers: serverNames.map((name) => ({
      name,
      present: harnessIds.filter((id) => name in byId.get(id).servers),
      fingerprints: [...new Set(harnessIds.filter((id) => name in byId.get(id).servers).map((id) => fingerprint(byId.get(id).servers[name])))],
    })),
    skills: skillNames.map((name) => ({
      name,
      present: skillSets.filter((s) => (s.skills ?? []).includes(name)).map((s) => s.id),
    })),
  }, null, 2));
} else if (emitFor) {
  const target = byId.get(emitFor);
  if (!target) { console.error(`unknown harness "${emitFor}". known: ${harnessIds.join(', ')}`); process.exit(2); }
  const missing = serverNames.filter((n) => !(n in target.servers) && !n.includes(' @'));
  if (!missing.length) console.log(`${emitFor}: nothing missing - all ${serverNames.length} known servers are configured.`);
  else {
    // Prefer the richest spec available for each server: the one with the most detail wins,
    // so an emitted block carries env and args rather than a bare command.
    const out = {};
    for (const name of missing) {
      let best = null;
      for (const id of harnessIds) {
        const spec = byId.get(id).servers[name];
        if (!spec) continue;
        if (!best || JSON.stringify(spec).length > JSON.stringify(best).length) best = spec;
      }
      out[name] = best;
    }
    console.log(JSON.stringify({ mcpServers: out }, null, 2));
    console.error(`\n${missing.length} server(s) ${emitFor} does not have: ${missing.join(', ')}`);
    console.error('Review each before pasting - a command that exists on one harness may need');
    console.error('a different path or an API key on another.');
  }
} else if (pluginsFor !== null) {
  const grouped = [...pluginSkills().entries()]
    .map(([plugin, set]) => [plugin, [...set].sort()])
    .sort((left, right) => left[0].localeCompare(right[0]));
  const total = grouped.reduce((sum, [, names]) => sum + names.length, 0);
  const term = pluginsFor.trim().toLowerCase();
  console.log(`Plugin-cached skills - ${total} across ${grouped.length} plugins` +
    (term ? `, showing those matching "${pluginsFor}"` : '') + '\n');
  const matched = [];
  for (const [plugin, names] of grouped) {
    const hits = term ? names.filter((name) => name.toLowerCase().includes(term)) : names;
    if (!hits.length) continue;
    console.log(`${plugin} (${hits.length})`);
    console.log(`  ${hits.join(', ')}`);
    matched.push(...hits);
  }
  if (term) {
    // A skill already living in some harness folder needs copying, not porting; one that exists
    // only in a cache is the kind that goes unnoticed - which is how two project skills were
    // written into a cache and never reached the repository.
    const known = new Set(skillNames);
    const orphan = [...new Set(matched)].filter((name) => !known.has(name));
    console.log(`\n${matched.length} match(es), of which ${orphan.length} are in no harness skill folder:`);
    if (orphan.length) console.log(`  ${orphan.join(', ')}`);
    console.log('\nRead-only by design. A plugin cache is installed content: copying from it into');
    console.log('.agents/skills is the port, and Tools/harness-sync.mjs --apply spreads it onward.');
  }
} else {
  const tick = (id, name) => {
    const spec = byId.get(id).servers[name];
    if (!spec) return ' · ';
    return isOff(spec) ? 'off' : ' ✓ ';
  };
  console.log(`MCP servers on this machine - ${serverNames.length} distinct, ${harnessIds.length} harness views\n`);
  const width = Math.max(...serverNames.map((n) => n.length), 'server'.length) + 2;
  console.log('server'.padEnd(width) + harnessIds.map((id) => id.slice(0, 9).padStart(9)).join(' '));
  for (const name of serverNames) {
    console.log(name.padEnd(width) + harnessIds.map((id) => tick(id, name).padStart(9)).join(' '));
  }
  for (const id of harnessIds) {
    const h = byId.get(id);
    if (h.errors.length) console.log(`\n  ${id}: ${h.errors.join('; ')}`);
  }

  // A server configured differently in two places is the drift that actually bites.
  const drifted = serverNames.filter((n) => {
    const fps = new Set(harnessIds.filter((id) => n in byId.get(id).servers).map((id) => fingerprint(byId.get(id).servers[n])));
    return fps.size > 1;
  });
  console.log(`\ndiffering configurations: ${drifted.length ? drifted.join(', ') : 'none'}`);

  console.log(`\nAgent skills - ${skillNames.length} distinct\n`);
  const sw = Math.max(...skillNames.map((n) => n.length), 'skill'.length) + 2;
  console.log('skill'.padEnd(sw) + skillSets.map((s) => s.id.slice(0, 11).padStart(11)).join(' '));
  for (const name of skillNames) {
    console.log(name.padEnd(sw) + skillSets.map((s) => ((s.skills ?? []).includes(name) ? ' ✓ ' : ' · ').padStart(11)).join(' '));
  }
}
