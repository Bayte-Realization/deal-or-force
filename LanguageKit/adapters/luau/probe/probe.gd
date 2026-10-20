extends SceneTree

func _initialize() -> void:
	_run.call_deferred()

func _run() -> void:
	var status := GDExtensionManager.load_extension("res://addons/dof_luau/luau.gdextension")
	if status not in [GDExtensionManager.LOAD_STATUS_OK, GDExtensionManager.LOAD_STATUS_ALREADY_LOADED]:
		fail("extension load: %s" % status)
		return
	var found := false
	for index in range(Engine.get_script_language_count()):
		if Engine.get_script_language(index).get_class() == "LuauLanguage":
			found = true
	if not found:
		fail("Luau script language registration")
		return
	var script := load("res://probe.luau") as Script
	if script == null or not script.can_instantiate():
		fail("Script cannot instantiate")
		return
	var first := RefCounted.new()
	first.set_script(script)
	var second := RefCounted.new()
	second.set_script(script)
	if first.call("add", 17, 25) != 42:
		fail("typed method dispatch")
		return
	if first.call("increment") != 1 or first.call("increment") != 2 or second.call("increment") != 1:
		fail("independent instance state")
		return
	var native_target := RefCounted.new()
	var capabilities: Dictionary = {
		"flag": Callable(native_target, "is_class"),
		"day": Callable(native_target, "get_reference_count"),
	}
	capabilities.make_read_only()
	if first.call("check_capabilities", capabilities) != true:
		fail("read-only native Callable results")
		return
	first.set_script(null)
	second.set_script(null)
	print("LANGUAGE_KIT_PASS language=luau script=true methods=true instances=true native_callables=true modules=true")
	quit(0)

func fail(message: String) -> void:
	push_error("LANGUAGE_KIT_FAIL luau: " + message)
	quit(1)
