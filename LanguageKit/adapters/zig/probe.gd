extends SceneTree

func _initialize() -> void:
	if not ClassDB.class_exists("DofZigKernel"):
		push_error("ZIG_PROBE missing native class")
		quit(1)
		return
	var kernel = ClassDB.instantiate("DofZigKernel")
	for values in [[3, -2, 7, 20], [0, 0, 0, 0], [-3, 2, -7, -20], [4294967297, 0, 0, 4294967297]]:
		if kernel.weighted_sum(values[0], values[1], values[2]) != values[3]:
			push_error("ZIG_PROBE wrong result")
			quit(1)
			return
	kernel = null
	print("ZIG_PROBE PASS signed=20 zero=0 negative=-20 int64=4294967297")
	quit(0)
