extends SceneTree

func _initialize() -> void:
	if not FileAccess.file_exists("res://.dof-fennara-fixture"):
		printerr("Refusing fixture writes outside an explicitly marked test directory")
		quit(2)
		return
	var args: PackedStringArray = OS.get_cmdline_user_args()
	if args.size() != 2:
		printerr("Expected Luau adapter DLL path and one module source path")
		quit(2)
		return
	var config: ConfigFile = ConfigFile.new()
	config.set_value("configuration", "entry_symbol", "luau_script_init")
	config.set_value("configuration", "compatibility_minimum", "4.4")
	config.set_value("configuration", "reloadable", false)
	config.set_value("libraries", "windows.debug.x86_64", args[0])
	config.set_value("libraries", "windows.release.x86_64", args[0])
	if config.save("res://luau_parse_probe.gdextension") != OK:
		quit(1)
		return
	var status: int = GDExtensionManager.load_extension("res://luau_parse_probe.gdextension")
	if status not in [GDExtensionManager.LOAD_STATUS_OK, GDExtensionManager.LOAD_STATUS_ALREADY_LOADED]:
		printerr("FAIL real Luau adapter loads: " + str(status))
		quit(1)
		return
	print("PASS real Luau adapter loads")
	var script: Script = ResourceLoader.load(args[1], "Script", ResourceLoader.CACHE_MODE_IGNORE) as Script
	if script == null or script.get_class() != "LuauScript" or script.reload() != OK:
		printerr("FAIL Luau module compiles")
		quit(1)
		return
	print("PASS Luau module compiles")
	script = null
	print("LUAU_PARSE_VERIFY failures=0")
	quit(0)
