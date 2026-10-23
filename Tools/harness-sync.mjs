#!/usr/bin/env node
// Deal or Force - keep the per-harness skill copies in one place.
//
// The same agent skills live in three harness directories: .agents/skills (Antigravity),
// .cursor/skills (Cursor/Devin) and .devin/skills (Devin). When a new skill is written it
// lands in one of them and the others silently fall behind - that is how .devin ended up
// four skills short. This tool reports and, with --apply, repairs that.
//
// Rules, on purpose:
//   - A skill MISSING from a target is copied there whole (scripts and resources included).
//   - A skill that EXISTS in both but differs is reported, never overwritten: .agents
//     deliberately carries harness-specific adaptations (fennara-godot is Antigravity's
//     lazy-tool version), so a blind copy would erase real work.
//   - .agents/rules -> .cursor/rules follows the same rules, with the .md -> .mdc rename
//     Cursor expects.
//
// Usage:
//   node Tools/harness-sync.mjs            report only
//   node Tools/harness-sync.mjs --apply    copy missing skills/rules

import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const apply = process.argv.includes('--apply');
const project = resolve(dirname(fileURLToPath(import.meta.url)), '..');

const sources = { skills: join(project, '.agents', 'skills'), rules: join(project, '.agents', 'rules') };
if (!Object.values(sources).some(existsSync)) {
  console.log('harness-sync: optional local .agents instructions are not installed; nothing to synchronise. Building and playing do not require them.');
  process.exit(0);
}
const targets = {
  skills: [join(project, '.cursor', 'skills'), join(project, '.devin', 'skills')],
  rules: [{ dir: join(project, '.cursor', 'rules'), rename: (n) => n.replace(/\.md$/, '.mdc') }],
};

function filesUnder(dir, prefix = '') {
  if (!existsSync(dir)) return [];
  const out = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const rel = prefix ? `${prefix}/${entry.name}` : entry.name;
    if (entry.isDirectory()) out.push(...filesUnder(join(dir, entry.name), rel));
    else out.push(rel);
  }
  return out.sort();
}

const report = [];
let copied = 0;

for (const kind of Object.keys(sources)) {
  const srcDir = sources[kind];
  if (!existsSync(srcDir)) continue;
  for (const name of readdirSync(srcDir)) {
    if (!statSync(join(srcDir, name)).isDirectory()) continue;
    const relFiles = filesUnder(join(srcDir, name), name);
    for (const target of targets[kind]) {
      const tDir = typeof target === 'string' ? target : target.dir;
      const rename = typeof target === 'string' ? (n) => n : (target.rename ?? ((n) => n));
      const dst = join(tDir, name);
      if (!existsSync(dst)) {
        if (apply) {
          mkdirSync(tDir, { recursive: true });
          cpSync(join(srcDir, name), dst, { recursive: true });
        }
        copied++;
        report.push(`${apply ? 'COPIED' : 'MISSING'} ${kind}/${name} -> ${relativize(tDir)} (${relFiles.length} files)`);
        continue;
      }
      for (const rel of relFiles) {
        const srcFile = join(srcDir, rel);
        const dstFile = join(tDir, rename(rel));
        if (!existsSync(dstFile)) {
          report.push(`PARTIAL ${kind}/${name} in ${relativize(tDir)} lacks ${rename(rel)}`);
          continue;
        }
        if (!readFileSync(srcFile).equals(readFileSync(dstFile))) {
          report.push(`DIFFERS ${kind}/${rel} vs ${relativize(tDir)}/${rename(rel)} (adapted copy - left alone)`);
        }
      }
    }
  }
}

function relativize(p) {
  return p.slice(project.length + 1);
}

if (!report.length) console.log('harness-sync: everything is in place');
else console.log(report.join('\n'));
if (!apply && copied) console.log(`\n${copied} item(s) missing - rerun with --apply to copy them.`);
if (apply) console.log(`harness-sync --apply: copied ${copied} item(s). Differing copies were left untouched.`);
