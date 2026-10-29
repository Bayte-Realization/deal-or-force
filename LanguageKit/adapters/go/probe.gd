extends SceneTree

func _initialize() -> void:
	call_deferred("_check")

func _check() -> void:
	if not ClassDB.class_exists("DofGoKernel"):
		push_error("GO_PROBE missing native class")
		quit(1)
		return
	var kernel = ClassDB.instantiate("DofGoKernel")
	for values in [[3, -2, 7, 20], [0, 0, 0, 0], [-3, 2, -7, -20], [4294967297, 0, 0, 4294967297]]:
		if kernel.weighted_sum(values[0], values[1], values[2]) != values[3]:
			push_error("GO_PROBE wrong result")
			quit(1)
			return
	kernel.free()
	print("GO_PROBE PASS signed=20 zero=0 negative=-20 int64=4294967297")
	quit(0)
