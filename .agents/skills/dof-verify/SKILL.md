---
name: dof-verify
description: >-
  Pick and run the smallest set of checks that proves a Deal or Force change:
  build, the matching FormableTests suite, SimBench, the in-game export probe,
  and interface screenshots. Use before saying any change is done, when the user
  asks "does it work", or after editing C#, game data, localization, or UI.
---

# Verification ladder

Run from the repo root. Stop at the first red rung and fix it. Prefer the `deal-or-force-dev` MCP tools; the shell form is in brackets.

| Rung | When | Command |
| --- | --- | --- |
| 1. Build | any C# edit | `build_game` [`dotnet build DealOrForce.csproj`] |
| 2. Narrow suite | the system you touched | `run_tests` with `args: "--<suite>-only"` [`Tools\dev.ps1 -Test -TestArgs '--<suite>-only'`] |
| 3. Localization | any new player string | `localization_audit` — must list nothing new |
| 4. Simulation | sim/balance/save edits | `dotnet run -c Release --project Tools/SimBench/SimBench.csproj -- 365` (multi-seed: skill `dof-balance-ab`) |
| 5. Full stand | before a commit that spans systems | `run_tests` with no args [`Tools\dev.ps1 -Test`] |
| 6. In-game probe | UI, boot, packaging | `game_verify` — read `EXPORT_VERIFY failures=N` |
| 7. Eyes | anything visual | `capture_screenshots`, then actually open the PNGs; RU and EN, 1920×1080 and 1280×720 |

## Suite map (FormableTests `--X-only`)

- Economy: `finance`, `households`, `production`, `construction-funding`, `ai-fiscal`, `brain-drain`
- Diplomacy: `deals`, `negotiations`, `alliances`, `organizations`, `ultimatum`, `player-ultimatum`, `peace-compact`, `foreign-assistance`, `kindred`, `claims`, `enclave-swap`, `border-tidy`
- War/AI: `ai-doctrine`, `delegated-front`, `civil-war-budget`, `assault-presentation`, `corridor-report`, `host-patience`. ArmyInteractionTests has its own flags: `--advance-rotation-only` (stale covered pins give way to open sectors), `--mobilisation-only` (wartime raise batch, peace stand-down margin, queue cap), `--combat-attrition-only`, `--coalition-combat-only`, `--ai-front-recovery-only`, `--assault-readiness-only`
- Politics/society: `political`, `promises`, `crises`, `civic`, `social`, `identity`, `methods`, `movement-voice`, `movement-power-state`, `federal-outcome`, `restoration`, `conciliation`, `policies`, `courses`
- Fingerprint/save/net: `integrity`, `living-world-fingerprint`, `movement-power-fingerprint`, `staged-boundary`, `snapshot-delivery`, `network-cadence`, `reload`, `release`
- Presentation: `situation`, `feed`, `events`, `responsive-hud`, `ui-icons`, `icons`, `flags`, `media`, `music`, `music-transition`, `cue-queue`, `weather`, `presentation-diplomacy`, `assistance-presentation`, `army-localization`, `fleet-localization`, `regional-content`
- Modding: `yaml`, `worker`, `mod-frontend`, `campaign-toggles`

Refresh the list with: `Get-ChildItem Tools\FormableTests -Filter *.cs -Recurse | Select-String '"--[a-z-]+-only"' -AllMatches | % { $_.Matches.Value } | Sort -Unique`.

## Report

Name each rung run with its number (`2414 PASS`, `EXPORT_VERIFY failures=0`, `FLIPPED=0`). Rungs not run are listed as not run. Known noise: ~258 ObjectDB warnings at Godot exit — mention, do not hide, do not count as new.
