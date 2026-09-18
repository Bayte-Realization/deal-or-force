# Interface and event sounds

*Русская версия: [INTERFACE_AUDIO_RU.md](INTERFACE_AUDIO_RU.md)*

Fourteen original synthesised cues, with no borrowed samples. The generator is
`Tools/generate_interface_audio.py` (Python 3, standard library only). Running it again
overwrites only the WAVs it lists, in `game/sound/audio/interface/`.

## Where they play

- Picking a country in the network lobby, connecting and disconnecting, confirming and
  withdrawing readiness, and a refused action.
- The first entry into a networked campaign; a re-sync does not repeat the opening cue.
- Pausing and resuming time.
- Queueing construction and completing a building of your own country. Projects already
  under way when a save loads, and cancelled construction, give no completion cue.
- Changing your own country's ideology or form of government, and proclaiming a formable
  nation.
- A successful manual save; autosave stays silent.

## Modding

The declarations live in `game/sound/02_interface_events.json`. A mod can override a cue of
the same name from its own `sound/` folder: `file`, `channel`, `volume_db`, `cooldown_ms`,
`yields_to_anything_playing`. Use the existing event names — inventing a new name does not
by itself create a game event to attach it to.

The WAV files can be replaced with your own. `file` paths are relative to the `sound/`
folder. For new assets in the editor, wait for Godot's import to finish; after changing the
catalogue, restart the game. Do not hand-edit the generated `.import` files.

Short actions use the independent quiet `interface` channel; ordinary notifications use
`notification`; forming a nation uses the priority `strategic` channel. Construction
completions yield to other notifications and are rate-limited to one every 3 seconds. Every
effect respects the product of master volume and effects volume, including zero; an
individual cue's volume survives a change of settings.
