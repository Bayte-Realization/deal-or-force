# Deliberately defective native test fixtures

This is a separate, dependency-free crate for the C# loader/fallback tests. The
production crate does not reference it; ordinary production builds and `cargo
test` do not build it. A build without exactly one fixture feature fails. These
libraries must never be installed or activated for gameplay.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/GodotLanguages/kernels/ownership/tests/fixtures/build.ps1
```

The recipe uses the same existing private Cargo/MSVC toolchain as the production
recipe, with process-only environment changes. Each feature builds in a separate
scratch directory under `.fennara/languages/ownership-kernel-fixtures`; it prints
the three absolute directories to pass explicitly to test loader instances.
Use `-Case abi2`, `-Case status3`, or `-Case malformed` to build only one.

| Feature | Deliberate behavior | Host must prove |
| --- | --- | --- |
| abi2 | ABI export returns 2 | Refuse library, free handle, managed result |
| status3 | Write partial invalid scratch, then return 3 | Discard entire result, sticky disable, managed result |
| malformed | Write partial invalid scratch, return success 0 | Structural rejection, sticky disable, managed result |

The status3 fixture simulates the native panic status; it does not unwind or test
OS fault handling. These are trusted test binaries that only write inside the
declared output buffers. They test ordinary failure containment, not hostile or
crashing native code. Tests should compare the managed batch byte-for-byte after
fallback and verify a second computation skips the disabled native backend.

GPL-3.0-only; see [../../../../LICENSE](../../../../LICENSE).
