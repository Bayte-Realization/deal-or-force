# The soundtrack

ReimAlpha's active score is now seven recordings: two experimental universal pieces,
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
