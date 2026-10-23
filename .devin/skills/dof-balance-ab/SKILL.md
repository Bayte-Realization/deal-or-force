---
name: dof-balance-ab
description: >-
  Judge a Deal or Force balance or simulation change on several seeds with the
  headless SimBench, comparing a baseline against --define overrides. Use when
  the user asks to tune numbers in game/common/defines, check determinism,
  compare "before/after" of a simulation change, or asks whether a change broke
  the economy, wars, or fronts.
---

# Balance A/B on SimBench

SimBench plays a campaign without the engine and prints ~100 invariants as `[ ok ]` / `[FAIL]`.
Arguments: `<days> <seed> [--define Key=Value]...` (`Tools/SimBench/Program.cs`). An unknown key prints `--define X: no such define` — treat that as a failed experiment, not a pass.

## Steps

1. Name the hypothesis in one line ("lower X should shorten border wars without starving fronts").
2. Try the numbers as defines first, without touching files:
   `powershell -ExecutionPolicy Bypass -File .agents\skills\dof-balance-ab\scripts\simbench-ab.ps1 -Days 365 -Seeds 1,555,730 -Define 'Key=Value' -Label <short-name>`
3. Read `FLIPPED=` and the per-seed table. A check that flips on one seed only is noise until a fourth seed agrees.
4. For a code change instead of a define: run the script with no `-Define` on the old tree into one label, then on the new tree into another, and compare logs (`-CompareOnly` reuses existing logs).
5. Only after the A/B looks right, write the value into `game/common/defines/*.json` and run 730 days once on the default seed.

## What to report

- seeds, days, defines; FAIL counts per side; every flipped check by name.
- The narrative lines that moved (world balance, tightest good, war counts) — quote them from the logs in `.fennara/simbench/<label>/`.
- Do not call a balance change "done" from a single seed or a compile.

## Known baseline

On 2026-10 trees the 730-day run on the default seed had **2 FAIL** of 106 (see `simbench_730_final.log` in the root). Compare to that, do not assume zero.
