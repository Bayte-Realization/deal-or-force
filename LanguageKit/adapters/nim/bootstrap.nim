import gdext
import gdext/classes/gdRefCounted

type DofNimKernel* {.gdsync.} = ptr object of RefCounted

proc weighted_sum(self: DofNimKernel; values: PackedInt32Array): int64 {.gdsync.} =
  for i, value in values:
    result += int64(i + 1) * int64(value)

proc language_name(self: DofNimKernel): string {.gdsync.} = "Nim"

proc register_classes {.execon: initialize_scene.} =
  register DofNimKernel

GDExtensionEntryPoint
