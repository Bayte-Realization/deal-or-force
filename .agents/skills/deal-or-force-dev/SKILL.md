---
name: deal-or-force-dev
description: >-
  Architecture, verification, critical play, and the living plan for Deal or
  Force, a Godot 4.8 .NET grand strategy game. Use when writing, reviewing, or
  testing simulation systems, campaign UI, localization, saves, campaign
  fingerprints, or when asked to continue the game, play it, or find the plan.
---

# Deal or Force — development

## Boundary

- `Scripts/Simulation/*.cs` does not reference `Godot.Node` or scene frames. A seed rebuilds the same world. State changes go through `ICampaignCommand`.
- `Scripts/DealOrForceGame.cs`, `Scripts/ui/*.cs`, and `Scripts/gfx/*.cs` project `Campaign` onto the globe and panels. They do not keep a second copy of game state.
- UI colors come from `Ui.Colour("token.name")` / `InterfaceSkin`. No raw color literals in panels.
- Every player-visible string exists in both `game/localization/english/` and `game/localization/russian/`.
- New simulation fields join `CampaignFingerprint` and `SaveSystem`. A new screen reads an existing system. It does not start a parallel economy, war, or crisis model.
- Branch is ReimAlpha. Do not publish binary builds. Do not delete `Tools/BtaBridge/` or `tmp_playtest/`.

## Where the plan actually is

Read these before inventing a new direction. Status lines in them are the author's report, not proof.

- `docs/MASTER_DEVELOPMENT_PLAN_RU.md` — the merged checklist.
- `docs/CLAUDE_PLAN_SYNC_2026_10_14_RU.md` — what was accepted from the other plan, and the order to continue.
- `ПЛАН_CLAUDE.md` — the ordered queue. Civil wars, movements, and the interface are still open after optimization and intelligence.
- `docs/CURSOR_NOTES_2026_10_14_RU.md` — working notes. They are not a claim that a campaign was watched.

Continue an existing system. A new panel does not invent a second economy, war, or save format.

## Campaign options

Toggles in `game/common/settings` with `"scope": "campaign"` are stored as JSON booleans. Read them with `SettingsCatalog.Flag`. `Chosen` only reads strings, so a menu that shows On can leave the campaign on the default. Apply the flag in `SettingsApply` after the campaign exists. A campaign keeps the value it was given; the main-menu row is where the next campaign starts.

`peace_by_provinces` sets `Defines.PeaceWholeRegions` to the opposite of the flag. Both `PeaceConference.Tidy` and `PeaceShape.Write` / `SettleProvinces` must honor it. Rounding in only one of them makes the toggle look dead.

`civil_wars_players` sets `Defines.CivilWarStrikesPlayers`. The weekly roll still needs low stability, people, regions, and the recovery cooldown. It is a chance, not a countdown.

## Comfort that was already rejected

- Do not put a full-width world headline under the top bar. World lines live in the right rail. Clicking one may move the camera. It does not play a cue: several of these happen every day.
- Do not recolor capital names for war or low stability. A separate mark can be designed later.
- The province card is a cursor card. Wheel events are not clicks. Wrapping labels with no width stretch the card to the screen; use `Fixed` on the head and the facts.
- Player-facing sound belongs to the player's own war, battle, deal, or new critical situation. Not to the world wire.

## Critical play

Watch a campaign before calling a surface finished. Poland is the first country: Singleplayer, choose on the map, `POL`. The campaign starts paused. One `fennara_status`, then `runtime_session`. If that call times out twice, stop and say so. Do not start a second Godot.

Judge what a player can misunderstand in one minute: a number with no cause, a button that does nothing, a menu taller than the screen, a sound on every foreign headline, a toggle that the menu shows and the treaty ignores. Say that plainly. Do not praise a compile.

## Check before calling work done

1. `dotnet build DealOrForce.csproj`
2. The narrow test for the system (`Tools/FormableTests`, `Tools/ArmyInteractionTests`, or its flag such as `--political-only`).
3. For balance or determinism: `dotnet run -c Release --project Tools/SimBench/SimBench.csproj -- 365`
4. Opening a panel must not change the campaign fingerprint.

Godot work reads `addons/fennara/ai/guidelines.md` first. Do not hand-edit `.tscn`, `.tres`, or `.res`.
