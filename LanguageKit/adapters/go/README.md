# Go / graphics.gd

Pinned source: af2e5393cdf04d36e1cba40cb4d6437624916d40 (MIT).
Go 1.27+ and a C compiler are required. System Go 1.21.4 is too old.

The adapter builds a real c-shared GDExtension containing DofGoKernel,
not just the gd build command. Pass -GoExecutable and -CCompiler, or use
workbench/build-local.ps1 from the kit root with -Languages go.
-BuildTool additionally produces gd.exe. No global go install is run.

The generated project is independently owned. Source templates are not
silently overwritten if edited. Dependency versions and sums are retained.
The probe instantiates the Go Node, calls weighted_sum with signed/zero,
negative and 4294967297 inputs, explicitly frees it and checks shutdown.

Actual Beta2 build/import/runtime passed on Windows x64 with portable
Go 1.27.3 and Zig 0.17.0 cc. This does not certify every engine API,
export, hot reload, sandboxing or a game-performance improvement.

See [current evidence](../../reports/beta2-runtime-2026-10-29.md).
