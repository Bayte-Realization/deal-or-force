# Python / py4godot — experimental, not approved for the game

Source review pin: b41ddb82af5f8e59ce4e1a9254bec9cf82a138dd (MIT).
The separately downloaded 4.7-alpha21 release archive has its own SHA256
in prepare-release.ps1; this is not a claim that release binaries were
built from that later source-review commit.

prepare-release.ps1 extracts the official hashed Windows x64 runtime into
a fresh owned project, retaining addons/py4godot. The included CPython is
3.14.4, independent of system Python. Source build.ps1 uses a private venv;
no global pip installation is performed. Source compilation is not certified.

The real Python fixture passed small arithmetic, but **4294967297 returned 1**.
It also leaked registered PyLanguage/PyScriptExtension/loader/saver classes
and a resource on exit. The gate correctly rejects this release on Beta2.
Do not automatically enable it in the main game.

probe-runtime.gd calls an attached Python script; probe.ps1 requires a real
success marker and clean teardown. A GDScript-only startup is not proof.
The alternative touilleMan godot4-meson track is in ../python-meson,
with a tested API parser patch but no native-runtime certificate.

See [current evidence](../../reports/beta2-runtime-2026-10-29.md).
