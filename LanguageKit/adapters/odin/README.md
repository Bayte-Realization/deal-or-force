# Odin / odin-godot adapter

Upstream: https://github.com/dresswithpockets/odin-godot

Reviewed pin: `93395901a76f62a9f2adff76c2eb38a185d91513`, Apache-2.0 (`upstream-LICENSE.txt`). No upstream modifications.

Requirements: Windows x86_64, Odin (tested `dev-2026-10-nightly:84bc3fc`), native Windows linker/SDK. This adapter uses the upstream `gdext` C-interface package directly. It does not depend on generated full Godot bindings, Temple, Godin, or submodules.

```powershell
./build.ps1 -GodotExecutable C:/Godot/Godot_console.exe -SourceDirectory C:/sources/odin-godot -OutputDirectory C:/build/odin -OdinExecutable C:/Odin/odin.exe
./probe.ps1 -GodotExecutable C:/Godot/Godot_console.exe -OutputDirectory C:/build/odin
```

Build emits `project/addons/dof_odin` and prints `ADDON_ROOT`. Copy it into a project's `addons/dof_odin`. `src/kernel.odin` shows explicit class registration, instance allocation/free, Variant-call and ptrcall dispatch, owned StringName/String lifetime, and unregister cleanup. It exposes `DofOdinKernel : RefCounted` and `weighted_sum3(a, b, c)`.

The upstream README calls the complete toolkit WIP and documents Godot 4.4, with Godin explicitly incomplete. Its pinned Temple generator does not compile with Odin 2026-10 (`filepath.walk`, `os.Errno` and other removed APIs). Therefore this kit does **not** advertise full generated 4.8 wrappers, attachable `.odin` scene scripts, Godin, editor completion, or reload support. The smaller direct ABI path is independently verified on Godot 4.8 Beta1.

Verified: editor import, class construction, negative-input method call returning 20, zero input returning 0, exit 0 and no Godot errors, including teardown. No full upstream suite, export-template, performance or cross-platform claim.

## Godot 4.8-beta2 status (14 November 2026)

Status: **runs, cannot be rebuilt here**. The DLL that runs is the Beta1 build of 16 October 2026
(`projects/AllSix/addons/dof_odin/dof_odin.dll`, 123 904 bytes,
SHA-256 `A241BCEEB3FAD50E848DC5DD06DA090408A9600C32A6C756A60A38B8230FECB3`).

Verified on `Godot_v4.8-beta2_mono_win64_console.exe` (`v4.8.beta2.mono.official.cafc17a19`), headless,
in a scratch project with its own copy of the DLL (`tmp_logs/languages/odin-beta2/probe` in the game
repository), never in the game project:

- `--import` exit 0, then `--headless --verbose --script res://probe_beta2.gd` exit 0, printing
  `ODIN_PROBE_BETA2 PASS vector=20,0,-20,4294967297,6*2^40 call=ok callv=ok instances=10000`:
  `weighted_sum3` gives 20, 0 and -20, carries a full int64 (4294967297 and 6·2^40) without truncation,
  answers through both the direct method path and the Variant `call`/`callv` path, and survives
  10 000 instance create/free cycles. The verbose log has no error, leak or orphan line through teardown.
- The extension's interface needs against the Beta2 header (`--dump-gdextension-interface`, 179
  functions): of the interface names the DLL requests from `get_proc_address`, 170 exist on Beta2.
  All 13 functions `src/kernel.odin` actually calls are present. Four of them are deprecated but still
  served: `classdb_construct_object` (4.4, use `classdb_construct_object3`), `mem_alloc`/`mem_free`
  (4.6, use `mem_alloc2`/`mem_free2`) and `classdb_register_extension_class2` (4.3, use
  `classdb_register_extension_class6`). The upstream `gdext` loader also asks for `array_ref`, which
  Beta2 marks "removed from interface"; the kernel never calls it.

Not done on Beta2, and why: there is no Odin compiler on this machine and `toolchains.windows.lock.json`
names none (it locks only `go-1.27.3`, `zig-0.17.0` and `rust`), and the pinned odin-godot source
(`9339590`) is not in `.cache/sources-beta2`. So the adapter has not been recompiled, no packed-array
method exists to probe, and neither `kernels/raster_overlay/odin` nor `kernels/ownership/odin` has a
DLL: the game's `FormableTests --raster-kernel-only <dir>` and `--ownership-native-only` with
`DOF_EXPERIMENTAL_KERNEL=odin` are wired and waiting for one. The next step needs the owner's approval
to add an `odin-dev-*` asset (official odin-lang release zip, its SHA-256, `odin.exe`) to the lock file;
after that: rebuild with `build.ps1`, move the four deprecated calls to their current replacements,
add a `PackedInt64Array` method to the probe, and build both C-ABI kernels for the parity and
alternating A/B timing runs.

Reproduce (paths relative to the game repository):

```powershell
$g = 'C:\GodotEngineNet 4.8-Beta2\Godot_v4.8-beta2_mono_win64_console.exe'
& $g --headless --path tmp_logs/languages/odin-beta2/probe --import
& $g --headless --verbose --path tmp_logs/languages/odin-beta2/probe --script res://probe_beta2.gd
python -I tmp_logs/languages/odin-beta2/check_interface.py tmp_logs/languages/odin-beta2/probe/addons/dof_odin/dof_odin.dll tmp_logs/languages/odin-beta2/api/gdextension_interface.h
```

## Godot 4.8-beta3 status (16 November 2026)

Status: **rebuilt and verified**. The Odin compiler is now in `toolchains.windows.lock.json`
(`odin-dev-2026-10`, official release zip, SHA-256 `B6B2E426…C2011`, `odin version` →
`dev-2026-10-nightly:84bc3fc`). With the odin-godot pin `9339590` fetched into
`.cache/sources-beta3/odin`, `build.ps1` produced a new `dof_odin.dll` (123 904 bytes,
SHA-256 `96177065EBD291F16A9F9162B5B66B6BDBB6A2C0A401DCB6A3E40874184C3FC0`). On
`4.8.beta3.mono.official.c51ff06e8`:

- `probe.ps1`: `ODIN_PROBE PASS weighted_sum3=20 zero=0`, `ODIN_RUNTIME_EXIT=0`.
- The stricter Beta2 probe on the new DLL: `ODIN_PROBE_BETA2 PASS vector=20,0,-20,4294967297,6*2^40 call=ok callv=ok instances=10000`,
  verbose log without error, leak or orphan lines.
- The GDExtension interface header is byte-identical between Beta2 and Beta3, so the
  interface analysis above (four deprecated calls, all still served) holds unchanged.

Still open: moving the four deprecated calls to their replacements, a `PackedInt64Array`
probe method, and the C-ABI kernels in `kernels/`. See `reports/beta3-2026-11-16.md`.