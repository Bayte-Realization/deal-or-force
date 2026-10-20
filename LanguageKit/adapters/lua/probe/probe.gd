extends SceneTree

func _initialize() -> void:
	_run.call_deferred()

func _run() -> void:
	var status := GDExtensionManager.load_extension("res://addons/dof_lua/lua.gdextension")
	if status not in [GDExtensionManager.LOAD_STATUS_OK, GDExtensionManager.LOAD_STATUS_ALREADY_LOADED]:
		fail("extension load: %s" % status)
		return
	if not ClassDB.class_exists("LuaState"):
		fail("LuaState class registration")
		return
	var script := load("res://probe.lua") as Script
	if script == null or not script.can_instantiate():
		fail("native Script cannot instantiate")
		return
	var first := RefCounted.new()
	first.set_script(script)
	var second := RefCounted.new()
	second.set_script(script)
	if first.call("add", 17, 25) != 42:
		fail("Lua method dispatch")
		return
	if first.call("increment") != 1 or first.call("increment") != 2 or second.call("increment") != 1:
		fail("independent Lua instance state")
		return
	var isolated = ClassDB.instantiate("LuaState")
	if isolated.do_string("return io == nil and os == nil and package == nil") != true:
		fail("unopened libraries unexpectedly available")
		return
	if isolated.do_string("return 17 + 25") != 42:
		fail("isolated LuaState evaluation")
		return
	var table = isolated.create_table()
	table.set_value(7, "numeric")
	table.set_value("greeting", "hello")
	if table.get_value(7) != "numeric" or table.get_value("missing", 42) != 42 or table.greeting != "hello":
		fail("4.8 non-colliding LuaTable key API")
		return
	first.set_script(null)
	second.set_script(null)
	print("LANGUAGE_KIT_PASS language=lua script=true methods=true instances=true restricted_state=true table_keys=true")
	quit(0)

func fail(message: String) -> void:
	push_error("LANGUAGE_KIT_FAIL lua: " + message)
	quit(1)
