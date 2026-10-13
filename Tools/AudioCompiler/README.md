# World-emergency loop compiler

The generated source is a 60-second, 128-BPM instrumental bed requested through
CreativeClaw / ElevenLabs Music v2.5. This tool adds a deterministic continuous
Shepard/Risset layer and blends one bar of the source's end into its beginning.
It is not a general beat detector: a different source requires its own bar alignment.

Requirements: .NET 8+, FFmpeg on PATH. Preserve the source recording.

```powershell
./Tools/AudioCompiler/build-world-loop.ps1 -Source C:/Audio/last_lap_source.mp3 -Output C:/Audio/last_lap_world.ogg
```

Existing output requires explicit `-Replace`. The generator itself uses `CreateNew`;
it checks amplitude and boundary slope before writing the WAV. A unique temporary
directory retains the intermediate stem for inspection; no source is deleted.

The decoded loop is 2,790,000 stereo frames at 48 kHz (58.125 s). Vorbis container
duration can include encoder padding (the delivered file reports 58.2 s). In the
game, native Ogg looping is enabled on a duplicated stream, not on the shared import.
Listen across the boundary before accepting a new source: numerical continuity of
the synthetic layer does not prove musical continuity of any arbitrary recording.

Six ordinary companion tracks use stereo/44.1 kHz Vorbis q5 after
`loudnorm=I=-20:TP=-2:LRA=9`. The world-loop recipe normalizes to the same target.
Provider provenance and playlist rules: `docs/ADAPTIVE_SOUNDTRACK_RU.md`.
