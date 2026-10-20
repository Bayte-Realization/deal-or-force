extends SceneTree

func _initialize() -> void:
	var kernel = ClassDB.instantiate("DofOdinKernel")
	if kernel == null:
		push_error("ODIN_PROBE class missing")
		quit(1)
		return
	var result: int = kernel.weighted_sum3(3, -2, 7)
	if result != 20 or kernel.weighted_sum3(0, 0, 0) != 0:
		push_error("ODIN_PROBE wrong result: %s" % result)
		quit(1)
		return
	print("ODIN_PROBE PASS weighted_sum3=20 zero=0")
	quit(0)
