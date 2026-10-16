extends SceneTree

func _initialize() -> void:
	var kernel = ClassDB.instantiate("DofNimKernel")
	if kernel == null:
		push_error("NIM_PROBE class missing")
		quit(1)
		return
	var result: int = kernel.weighted_sum(PackedInt32Array([3, -2, 7]))
	if result != 20 or kernel.language_name() != "Nim" or kernel.weighted_sum(PackedInt32Array()) != 0:
		push_error("NIM_PROBE wrong result: %s" % result)
		quit(1)
		return
	print("NIM_PROBE PASS weighted_sum=20 empty=", kernel.weighted_sum(PackedInt32Array()))
	quit(0)
