---
trigger: always_on
description: Simulation boundary, localization, and how new Deal or Force features attach to existing systems
---

# Deal or Force — core

Ported from `.cursor/rules/deal-or-force-core.mdc`. Keep both in sync.

- Simulation code under `Scripts/Simulation/` stays free of Godot scene types. Presentation reads `Campaign`; it does not store a second world. State changes go through `ICampaignCommand`.
- Player-visible strings ship in English and Russian. UI colors use `Ui.Colour` tokens, not raw literals.
- Extend an existing system. A new panel, good, or crisis does not invent a parallel economy, war, or save format.
- New state is serialized and mixed into `CampaignFingerprint`. Reading a screen does not change the fingerprint.
- Terms follow `CONTEXT.md` (Казна ≠ Госдолг ≠ Просрочка ≠ Дефолт).
- Branch is ReimAlpha. Do not publish binary builds to GitHub.
- `Tools/BtaBridge/` and `tmp_playtest/` are local experiments. Do not treat their untracked size as game source, and do not delete them as cleanup.
- Status in plan docs is the author's report, not proof. "Done" means a named check passed on today's tree.
