# Conflict integrity audit

Read-only save inspection and bounded headless replay of the RUS/UKR example.
It uses the production save, map, diplomacy and simulation code; it never writes a save,
changes human seats or forces a desired winner. Use a disposable copy of the input.

```
dotnet run -c Release --project Tools/ConflictAudit -- --selftest
dotnet run -c Release --project Tools/ConflictAudit -- "<copied save folder>" WatafakDonbassy 180
dotnet run -c Release --project Tools/ConflictAudit -- --seed 932446730 365
```

The regular `--land-only` FormableTests suite also runs these regressions.
`--probe-republic` as the fourth argument explicitly exercises proclamation on the in-memory
copy, then prints ownership/control before and after enforcement. It does not save the result.
This is a mutation probe, not an observation of historical events.

The report separates legal ownership, military control, armistice arrangements and historical
peace summaries. Daily snapshots can miss changes reversed inside the same day; the focused
regression tests additionally check the responsible production actions directly.

Tests here are not a complete balance battery, a network test, a native Godot playtest or a
claim that every peace outcome should be identical across seeds.
