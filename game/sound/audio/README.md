# Strategic audio mix

The modern cue set is deliberately short and uses separate interface, strategic and battle channels.

- Interface cues are quiet and immediate.
- Strategic alerts are queued, deduplicated and never interrupt each other.
- Battle reports use their own lower-volume channel and are globally throttled so a batch of resolved battles produces one report instead of a wall of sound.

The untrimmed source recordings are in `original/` beside this file. What sits here is the
trimmed and level-matched set the game actually plays, so a mod can replace either without
touching any code.

All of it lives under `game/sound/` rather than beside the source, and the paths in
`01_cues.json` are relative to `sound/`. It used to live in an `Assets/Audio` folder next
to the C#, which made the sound of this game the one kind of content a mod could not
replace without knowing where the executable had been built from.

| Cue | Duration | Mix role |
| --- | ---: | --- |
| `ui_select` | 0.38 s | quiet immediate feedback |
| `event_alert` | 1.65 s | strategic queue |
| `battle_won`, `battle_lost` | 1.85 s | throttled battle report |
| `war_declared`, `peace_signed` | 2.65 s | priority strategic queue |

Source-file peaks are kept below -6 dBFS for UI and below -4 dBFS targets for alerts before the per-channel Godot gain is applied. This leaves headroom for music and ambience.

The three `interface/support_*.wav` cues were generated for this game with Creative Claw / ElevenLabs Sound Effects v2 (44.1 kHz stereo PCM) and are mixed separately through `02_interface_events.json`:

| Cue | Purpose | Source asset |
| --- | --- | --- |
| `support_order` | Queue a long-range formation | https://cdn.creativeclaw.co/u/c8554121/audio/6d8b6115-eb5e-43c9-b733-4c6f9eafa234.pcm |
| `support_mission` | Change its mission | https://cdn.creativeclaw.co/u/c8554121/audio/fc077033-0f3f-4ceb-87d7-3623801f8b07.pcm |
| `support_ready` | Training finishes | https://cdn.creativeclaw.co/u/c8554121/audio/1a7b3d30-c07d-416d-b443-08691ddd4ff8.pcm |
