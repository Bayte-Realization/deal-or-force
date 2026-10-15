#!/usr/bin/env node
// Deal or Force - a small MCP server for the development loop.
//
// The project is checked by four things: a build, the headless test stand, the Godot asset
// import and the in-game verification probe. Every one of them is a command line somebody has
// to remember. This exposes them as MCP tools so an assistant can run the same checks the
// project actually trusts, and read the same numbers a person would.
//
// It is deliberately dependency-free: the MCP stdio transport is newline-delimited JSON-RPC,
// and that is the whole of what this needs. No SDK, nothing to install, nothing to keep
// patched - which matters for a tool that is supposed to still work in a year.
//
// Smoke-test it by hand:
//   node Tools/game-mcp/server.mjs
//   {"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
//   {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}

import { spawn } from 'node:child_process';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const project = resolve(here, '..', '..');

function godot() {
  if (process.env.GODOT_EXECUTABLE) return process.env.GODOT_EXECUTABLE;
  const config = join(project, '.mcp.json');
  if (existsSync(config)) {
    try {
      const path = JSON.parse(readFileSync(config, 'utf8'))?.mcpServers?.['godot-coding-solo']?.env?.GODOT_PATH;
      if (path && existsSync(path)) return path;
    } catch { /* a malformed config is not a reason to fail the tool */ }
  }
  return 'C:\\GodotEngineNet 4.8-dev\\Godot_v4.8-dev7_mono_win64_console.exe';
}

function run(command, args) {
  return new Promise((done) => {
    const child = spawn(command, args, { cwd: project, shell: false, windowsHide: true });
    let out = '';
    let err = '';
    child.stdout.on('data', (chunk) => { out += chunk; });
    child.stderr.on('data', (chunk) => { err += chunk; });
    child.on('error', (error) => done({ code: -1, out, err: String(error) }));
    child.on('close', (code) => done({ code, out, err }));
  });
}

// The game's data files carry `//` comments, so a plain JSON.parse is not enough.
function readJson(path) {
  return JSON.parse(readFileSync(path, 'utf8').replace(/^\s*\/\/.*$/gm, ''));
}

function summarise(result, label) {
  const pass = (result.out.match(/^PASS /gm) ?? []).length;
  const failed = (result.out.match(/^(FAIL|ERROR) .*/gm) ?? []).slice(0, 8);
  const lines = [`${label}: exit ${result.code}, ${pass} PASS`];
  if (failed.length) lines.push(...failed);
  else if (result.code !== 0) lines.push((result.err || result.out).trim().split('\n').slice(-8).join('\n'));
  return lines.join('\n');
}

const tools = [
  {
    name: 'build_game',
    description: 'Build the game solution and the headless test stand. Reports the first compile errors rather than a bare exit code.',
    inputSchema: { type: 'object', properties: {} }
  },
  {
    name: 'run_tests',
    description: 'Run the headless test stand. Pass a single suite flag (for example "--identity-only") to run just that one.',
    inputSchema: {
      type: 'object',
      properties: { args: { type: 'string', description: 'Optional FormableTests arguments, e.g. "--media-only".' } }
    }
  },
  {
    name: 'game_verify',
    description: 'Play the scripted campaign with the in-game verification probe and report EXPORT_VERIFY failures. Headless by default.',
    inputSchema: {
      type: 'object',
      properties: { capture_directory: { type: 'string', description: 'Where the run log and any screenshots go. Defaults to the system temp folder.' } }
    }
  },
  {
    name: 'capture_screenshots',
    description: 'Play the scripted campaign in a real window and write export-*.png screenshots, so the interface can be looked at rather than imagined.',
    inputSchema: {
      type: 'object',
      properties: { capture_directory: { type: 'string', description: 'Where the screenshots go. Defaults to the system temp folder.' } }
    }
  },
  {
    name: 'list_technologies',
    description: 'List the research catalogue read from game/common/technologies: id, name, category, cost and prerequisites.',
    inputSchema: {
      type: 'object',
      properties: { category: { type: 'string', description: 'Optional category filter, e.g. "industry".' } }
    }
  },
  {
    name: 'localization_audit',
    description: 'Report keys present in the English tables but missing from the Russian ones, in the same file.',
    inputSchema: { type: 'object', properties: {} }
  }
];

async function call(name, args) {
  const capture = args.capture_directory ?? join(process.env.TEMP ?? '/tmp', 'dof-shots');

  if (name === 'build_game') {
    const game = await run('dotnet', ['build', join(project, 'DealOrForce.sln'), '-v', 'q', '-nologo']);
    if (game.code !== 0) return summarise(game, 'game build');
    const tests = await run('dotnet', ['build', join(project, 'Tools', 'FormableTests', 'FormableTests.csproj'), '-v', 'q', '-nologo']);
    return summarise(tests, 'test stand build');
  }

  if (name === 'run_tests') {
    const extra = (args.args ?? '').split(' ').filter(Boolean);
    const result = await run('dotnet', ['run', '--project', join(project, 'Tools', 'FormableTests'), '--no-build', '--', ...extra]);
    return summarise(result, 'test stand');
  }

  if (name === 'game_verify' || name === 'capture_screenshots') {
    const headless = name === 'game_verify' ? ['--headless'] : [];
    const result = await run(godot(), [...headless, '--path', project, '--', '--verify-build', '--verify-build-output', capture]);
    const verdict = /EXPORT_VERIFY failures=(\d+)/.exec(result.out);
    if (!verdict) return `no verification result (exit ${result.code}).\n${(result.err || result.out).split('\n').slice(-12).join('\n')}`;
    const shots = existsSync(capture) ? readdirSync(capture).filter((f) => f.endsWith('.png')).length : 0;
    return `EXPORT_VERIFY failures=${verdict[1]}\nscreenshots: ${shots} in ${capture}`;
  }

  if (name === 'list_technologies') {
    const folder = join(project, 'game', 'common', 'technologies');
    const rows = [];
    for (const file of readdirSync(folder).filter((f) => f.endsWith('.json')).sort()) {
      let parsed;
      try { parsed = readJson(join(folder, file)); } catch { continue; }
      for (const [id, tech] of Object.entries(parsed)) {
        if (args.category && tech.category !== args.category) continue;
        rows.push(`${id} | ${tech.name} | ${tech.category} | ${tech.cost} | ${(tech.requires ?? []).join(', ')}`);
      }
    }
    return `${rows.length} technologies\n${rows.join('\n')}`;
  }

  if (name === 'localization_audit') {
    const english = join(project, 'game', 'localization', 'english');
    const russian = join(project, 'game', 'localization', 'russian');
    // The two folders do not use the same file names, so the comparison is by key: every
    // English key that has no Russian text anywhere is a string a Russian player reads in
    // English.
    const keys = (folder) => {
      const set = new Set();
      for (const file of readdirSync(folder).filter((f) => f.endsWith('.json'))) {
        try { for (const key of Object.keys(readJson(join(folder, file)))) set.add(key); } catch { /* unreadable table */ }
      }
      return set;
    };
    const en = keys(english);
    const ru = keys(russian);
    const gaps = [...en].filter((key) => !ru.has(key)).sort();
    return gaps.length
      ? `${gaps.length} of ${en.size} English keys have no Russian text\n${gaps.slice(0, 60).join('\n')}`
      : `Russian tables cover all ${en.size} English keys`;
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
      serverInfo: { name: 'deal-or-force-dev', version: '1.0.0' }
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

