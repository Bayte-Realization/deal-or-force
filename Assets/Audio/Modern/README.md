# Strategic audio mix

The modern cue set is deliberately short and uses separate interface, strategic and battle channels.

- Interface cues are quiet and immediate.
- Strategic alerts are queued, deduplicated and never interrupt each other.
- Battle reports use their own lower-volume channel and are globally throttled so a batch of resolved battles produces one report instead of a wall of sound.

The legacy source recordings remain in `Assets/Audio`. These files are trimmed and level-matched derivatives so mods can replace either set without changing code.

| Cue | Duration | Mix role |
| --- | ---: | --- |
| `ui_select` | 0.38 s | quiet immediate feedback |
| `event_alert` | 1.65 s | strategic queue |
| `battle_won`, `battle_lost` | 1.85 s | throttled battle report |
| `war_declared`, `peace_signed` | 2.65 s | priority strategic queue |

Source-file peaks are kept below -6 dBFS for UI and below -4 dBFS targets for alerts before the per-channel Godot gain is applied. This leaves headroom for music and ambience.
