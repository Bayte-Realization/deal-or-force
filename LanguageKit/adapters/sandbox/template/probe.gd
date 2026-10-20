extends SceneTree

func _initialize() -> void:
	if not ClassDB.class_exists("SafeGDScript"):
		push_error("SANDBOX_PROBE: SafeGDScript is not registered")
		quit(1)
		return
	# A harmless bootstrap establishes the instance before untrusted initializers.
	var script: Script = ClassDB.instantiate("SafeGDScript")
	script.set_source_code("func bootstrap():\n\tpass\n")
	var instance := Node.new()
	instance.set_script(script)
	instance.set("restrictions", true)
	instance.set("execution_timeout", 10)
	instance.set("memory_max", 16)
	instance.set("allocations_max", 1000)
	instance.set("references_max", 16)
	script.set_source_code("func add_checked(left: int, right: int) -> int:\n\treturn left + right\n")
	var value: int = instance.call("add_checked", 19, 23)
	var exceptions: int = instance.get("monitor_exceptions")
	if value != 42 or exceptions != 0 or not instance.get("restrictions"):
		push_error("SANDBOX_PROBE: unexpected result %s / exceptions %s" % [value, exceptions])
		instance.free()
		quit(1)
		return
	print("SANDBOX_PROBE:PASS sum=42 restrictions=true")
	instance.free()
	quit(0)
