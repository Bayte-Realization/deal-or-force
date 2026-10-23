extends SceneTree

func _initialize() -> void:
	run.call_deferred()

func run() -> void:
	for descriptor in ["res://addons/dof_lua/lua.gdextension", "res://addons/dof_luau/luau.gdextension"]:
		var status := GDExtensionManager.load_extension(descriptor)
		if status not in [GDExtensionManager.LOAD_STATUS_OK, GDExtensionManager.LOAD_STATUS_ALREADY_LOADED]:
			push_error("combined load failure " + descriptor)
			quit(1)
			return
	var lua_script = load("res://lua_probe.lua")
	var luau_script = load("res://luau_probe.luau")
	var fsharp_bridge = load("res://FSharpBridge.cs")
	var lua := RefCounted.new()
	lua.set_script(lua_script)
	var luau := RefCounted.new()
	luau.set_script(luau_script)
	var managed = fsharp_bridge.new()
	if lua.call("add", 17, 25) != 42 or luau.call("add", 17, 25) != 42 or managed.WeightedScore(PackedInt64Array([2, 4, 6]), PackedInt64Array([1, 4, 4])) != 42:
		push_error("combined dispatch failure")
		quit(1)
		return
	lua.set_script(null)
	luau.set_script(null)
	print("LANGUAGE_KIT_PASS combined=lua,luau,fsharp score=42")
	quit(0)
