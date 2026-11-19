# The soundtrack

The 15 October soft peace alternative is `quiet_current.ogg`; it replaces the two
earlier universal calm recordings in automatic selection without deleting them.
Both originals remain in the listening library. See `docs/QUIET_SOUNDTRACK_RU.md`
from the project root for provenance, technical checks and the listening caveat.

The earlier ReimAlpha adaptive score introduced seven recordings: two experimental universal pieces,
four region-tagged Asian companions, and a processed world-emergency loop.
Scheduling, region/mood tags and crossfade policy live in `../10_adaptive_score.json`
and `../11_world_emergency.json`. See [provenance and modding notes](../../../docs/ADAPTIVE_SOUNDTRACK_RU.md)
and `Tools/AudioCompiler` for the reproducible rising-loop layer. These are generated
recordings, not a claim of human performance. Final musical acceptance requires listening.

## Preserved legacy score

Four tracks, generated for Deal or Force with Creative Claw and encoded to Ogg Vorbis
(44.1 kHz stereo). They are the game's own and can be reused under the author's
[asset permissions](../../../README.md#licence), like the interface sounds made the same way.

| Track | Plays | Length |
| --- | --- | ---: |
| `menu_theme` | in the menus | 2:03 |
| `campaign_ambient` | during a campaign, most often | 2:28 |
| `war_theme` | during a campaign | 2:10 |
| `victory_theme` | during a campaign, least often | 1:53 |

The original declarations are preserved in `../00_tracks.json`; the new catalog removes
these four keys from active selection without deleting the recordings. A mod replaces any of them by
naming the same key in its own `music/` folder, or adds its own under a new key.

## Modern companion score

`architects_of_detente.ogg` ("Architects of détente") was generated with Creative Claw /
Lyria 3.5 (instrumental, 1:57, felt piano, muted cello, contrabass pizzicato, 76 BPM,
D Dorian) and encoded to Ogg Vorbis q5 after `loudnorm=I=-20:TP=-2:LRA=9` (measured
-20.4 LUFS integrated). Declared in `../17_architects_of_detente.json` for calm and uneasy
campaign moods. Source asset:
https://cdn.creativeclaw.co/u/c8554121/audio/ec4b47d2-5c68-40e8-8126-bd8ef57c37ab.mp3
(job `a5bd6d96-5b56-4386-9dde-cf8632fa5017`). A generated recording, not a claim of human
performance; final musical acceptance requires listening.
