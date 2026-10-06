extends SceneTree

func _initialize() -> void:
	_run.call_deferred()

func _run() -> void:
	var descriptor := "res://game/extensions/luau/luau_script.gdextension"
	var status := GDExtensionManager.load_extension(descriptor)
	if status != GDExtensionManager.LOAD_STATUS_OK and status != GDExtensionManager.LOAD_STATUS_ALREADY_LOADED:
		fail("extension registration: %s" % status)
		return
	var found := false
	for index in range(Engine.get_script_language_count()):
		if Engine.get_script_language(index).get_class() == "LuauLanguage":
			found = true
	if not found:
		fail("Luau is absent from Engine script languages")
		return
	if OS.get_cmdline_user_args().has("--only-registration"):
		print("PASS registration-only shutdown control")
		quit(0)
		return
	var script := load("res://Tools/LanguageAdapters/fixtures/probe.lua") as Script
	if script == null or not script.can_instantiate():
		fail("native Script resource cannot instantiate")
		return
	var instance := RefCounted.new()
	instance.set_script(script)
	if not instance.has_method("add") or instance.call("add", 17, 25) != 42:
		fail("registered method dispatch")
		return
	if instance.call("increment") != 1 or instance.call("increment") != 2:
		fail("per-instance native state")
		return
	var another := RefCounted.new()
	another.set_script(script)
	if another.call("increment") != 1:
		fail("instance state leaked")
		return
	print("PASS Luau registered as ScriptLanguageExtension")
	print("PASS native Script loads, attaches and dispatches typed methods")
	print("PASS persistent state is separate for each script instance")
	if OS.get_cmdline_user_args().has("--skip-api"):
		another.set_script(null)
		instance.set_script(null)
		quit(0)
		return
	var native_target := RefCounted.new()
	var capabilities: Dictionary = {
		"flag": Callable(native_target, "is_class"),
		"day": Callable(native_target, "get_reference_count"),
	}
	capabilities.make_read_only()
	if instance.call("check_capabilities", capabilities) != true:
		fail("callable return values or read-only capabilities")
		return
	print("PASS native Callable returns bool/int and capabilities stay read-only")
	another.set_script(null)
	instance.set_script(null)
	quit(0)

func fail(message: String) -> void:
	push_error("Native adapter probe failed: " + message)
	quit(1)
