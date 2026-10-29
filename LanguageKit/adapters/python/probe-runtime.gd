extends SceneTree

func _initialize() -> void:
	call_deferred("_check")

func _check() -> void:
	var script = load("res://dof_python_kernel.py")
	if script == null or not script.can_instantiate():
		push_error("PYTHON_PROBE Python script cannot instantiate")
		quit(1)
		return
	var kernel = Node.new()
	kernel.set_script(script)
	for values in [[3, -2, 7, 20], [0, 0, 0, 0], [-3, 2, -7, -20], [4294967297, 0, 0, 4294967297]]:
		var actual = kernel.call("weighted_sum", values[0], values[1], values[2])
		if actual != values[3]:
			push_error("PYTHON_PROBE expected %s, got %s for %s" % [values[3], actual, values.slice(0, 3)])
			kernel.free()
			quit(1)
			return
	print("PYTHON_PROBE PASS signed=20 zero=0 negative=-20 int64=4294967297 runtime=", kernel.call("runtime_version"))
	kernel.free()
	quit(0)
