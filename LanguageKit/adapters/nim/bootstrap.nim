import gdext
import gdext/classes/gdRefCounted

type DofNimKernel* {.gdsync.} = ptr object of RefCounted

proc weighted_sum(self: DofNimKernel; values: PackedInt32Array): int64 {.gdsync.} =
  for i, value in values:
    result += int64(i + 1) * int64(value)

proc regional_pressure_sum(self: DofNimKernel; populations: PackedInt32Array; unrest_permille: PackedInt32Array): int64 {.gdsync.} =
  let n = min(populations.len, unrest_permille.len)
  for i in 0 ..< n:
    let pop = max(0'i64, int64(populations[i]))
    let u = clamp(int64(unrest_permille[i]), 0'i64, 1000'i64)
    result += (pop * u) div 1000'i64

proc language_name(self: DofNimKernel): string {.gdsync.} = "Nim"

proc register_classes {.execon: initialize_scene.} =
  register DofNimKernel

GDExtensionEntryPoint
