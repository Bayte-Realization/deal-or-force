---
name: dof-plan-sync
description: >-
  Find where Deal or Force development stands and record a finished pass in the
  project's plan documents. Use at the start of a session ("what next", "continue
  the game", "where is the plan") and at the end of a substantial change, to write
  the docs/DEVELOPMENT_PASS_<date>_RU.md note and update status lines with evidence.
---

# Plan sync

Many agents (Claude, Cursor, Codex, Devin, Cline, Kilo, Antigravity) have worked on this repo. The plan is spread across documents; read them in this order and stop when the question is answered.

## Start of a session

1. `docs/MASTER_DEVELOPMENT_PLAN_RU.md` — «Ближайшие законченные срезы» (top) and the status tables.
2. Newest `docs/DEVELOPMENT_PASS_*_RU.md` and `docs/REIMALPHA_*_RU.md` — what the last pass actually did.
3. `ПЛАН_CLAUDE.md` (root; the name shows mangled in Windows PowerShell 5 — read it with `view_file`) — the ordered queue.
4. `docs/IDEAS_BACKLOG_RU.md` — ideas, explicitly not features.
5. `git log --oneline -n 10` and `git status --short | Measure-Object` — uncommitted work from another agent is theirs; do not overwrite or "clean" it.

Answer with: the next open slice, what evidence exists, what is unverified.

## End of a pass

Write `docs/DEVELOPMENT_PASS_<YYYY_MM_DD>_RU.md` (append a section if today's file exists) in the house style: plain Russian, short paragraphs, no marketing.

- **Что сделано** — behaviour, not file lists.
- **Чем проверено** — exact numbers (`2414 PASS`, fingerprint before/after, `EXPORT_VERIFY failures=0`, SimBench seeds and `FLIPPED=`).
- **Что не проверено / ограничения** — honestly.
- Update the item's status in `MASTER_DEVELOPMENT_PLAN_RU.md` (проверено / частично / в работе / очередь) with a link to the evidence.

Never mark a line «проверено» from a compile, a file's existence, or a generated image.
