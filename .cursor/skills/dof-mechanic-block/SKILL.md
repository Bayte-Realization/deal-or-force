---
name: dof-mechanic-block
description: >-
  Design and land a new Deal or Force game mechanic (intelligence, crises,
  reconstruction, strategic goals, a new deal type, etc.) through the project's
  acceptance pipeline: data and localization, existing loop, price/refusal in UI,
  saved state, fingerprint, tests, toggle/mod overlay, and campaign observation.
  Use when the user asks to add or extend a mechanic or system.
---

# New mechanic block

The project's own acceptance rule (`docs/MASTER_DEVELOPMENT_PLAN_RU.md`, «Общая приёмка»), turned into steps. Copy [checklist.md](resources/checklist.md) into the pass notes and tick it honestly.

## 0. Find the host system

`grep` `Scripts/Simulation/` for the nouns of the idea before writing anything. A mechanic **extends** an existing system (deals, crises, movements, finance, organizations). If two plans mention it, read `docs/MASTER_DEVELOPMENT_PLAN_RU.md` and `docs/IDEAS_BACKLOG_RU.md` first.

## 1. Cause → facts → consequence → action

Write the four lines before code. Every new event must be explainable in a card: why it happened (a modeled cause), what the player can observe, what will likely follow, and what can be done. No effect may be attributed by coincidence of timing.

## 2. Pipeline

1. **Data** in `game/common/...` (JSON, overlayable by mods) + strings in `game/localization/english` **and** `russian`.
2. **Loop**: hook into the existing daily/weekly tick; state changes only through `ICampaignCommand`.
3. **Price / refusal / response** visible in UI, read from the same definitions the execution uses.
4. **Bounded saved state** in `SaveSystem`; old saves load (default for missing fields).
5. **split/merge + fingerprint**: field mixed into `CampaignFingerprint`; opening its panel does not change the fingerprint; multiplayer authority respected.
6. **Tests**: positive and negative in `Tools/FormableTests` with a new `--<name>-only` flag.
7. **Toggle / mod overlay**: a campaign setting or define to turn it off; a mod can change its numbers.
8. **Observation**: SimBench several seeds (`dof-balance-ab`) and one watched campaign (`fennara-godot`).

## 3. Close

Run `dof-verify`. Update the plan line with the evidence via `dof-plan-sync`. Status words: проверено / частично / в работе / очередь — never "готово" from a compile.
