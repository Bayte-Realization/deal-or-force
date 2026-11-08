# Doctrine choices and loose audio / Доктрины и обычные аудиофайлы

## Doctrine choices

Put a numbered JSON/JSONC/YAML document in your mod's `common/tactical_doctrines/`,
using the same category as the base game. See the complete working example
`game/common/tactical_doctrines/50_operational_choices.json`.

Nodes may declare `"excludes": ["alternative_node_id"]`. The exclusion is reciprocal:
writing it on one side is sufficient. It affects availability, AI study, the visible
choice caption and the ordinary saved Known list. It does not erase existing knowledge.
IDs must exist in the same book; self-exclusion, ancestor exclusions and cycles in
requirements reject the candidate book, preserving the prior valid definition.

Both books and nodes may declare `"ai_profiles": { "defensive": 3, "garrison_state": 1 }`.
These are relative weights for AI character IDs, not country tags or probabilities.
Weights must be finite and non-negative. Custom character IDs are permitted. An absent
profile falls back to the previous selection rules; the primary strategic book keeps
its priority. No script is required. Localize book/node names and descriptions in
your package's normal localization files.

По-русски: `excludes` задаёт взаимоисключающие статьи одной книги; связь двусторонняя.
`ai_profiles` — относительные веса характера ИИ, а не страны. Пример выше позволяет
выбрать мобильный контрудар вместо позиционного сопротивления с разными плюсами
и минусами. Старые статьи и сохранения не нужно переименовывать. Некорректная книга
не должна вытеснять прежнее рабочее определение.

## Audio without importing in Godot

Copy an MP3/Ogg/WAV into your mod and declare a cue in `sound/` or a track in `music/`.
The existing asset-root rule resolves relative paths within that package. Use
`game/sound/08_comfort_selection.json` and `game/music/16_atlas_lamplight.json` as
complete examples: sound paths are relative to the package root, music's `audio/...`
paths to its music category. The game loads loose audio at runtime; a mod author
does not need to import it or produce a PCK. Imported packaged audio remains supported.
Replacing a file takes effect when the catalogue/scene reloads, not as a live decoder swap.

Keep `volume_db` at zero or below and use a cooldown for repeated UI cues. Do not route
quiet selection feedback through critical alerts or remove warnings to make the game calm.
Optional listening-library metadata belongs in `presentation/`; its waveform is measured
amplitude, not invented decoration. Generate measurements without modifying the source:

```powershell
./Tools/MediaLibrary/MeasureWaveforms.ps1 -ProjectPath <mod-or-project-root> -Inventory game/presentation/36_comfort_audio.json -Points 64
```

That helper expects the base-style `game/` layout; for a standalone mod, measure from
a development staging layout with its files under `game/`, or use FFmpeg directly.
It writes JSON to stdout, not to your asset or inventory. Add measured `duration_seconds`
and `peaks` to the appropriate inventory record. Changing only a caption preserves
measurements; changing its file clears stale waveform/duration/trim metadata.

По-русски: аудиофайл достаточно положить в пакет и указать в обычной декларации.
Отдельный импорт в редакторе Godot не нужен. Звук и музыка остаются настраиваемыми
данными, включая громкость, повторы и контекст; новый мягкий звук не прошит в C#.
Проверяйте звучание слухом: отсутствие технического clipping не гарантирует приятный тембр.
