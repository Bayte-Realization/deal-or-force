extends SceneTree

## Serialize a test project's settings through Godot, never edit the live editor's cache.
## The caller creates an empty, owned staging directory and copies its own assemblies.
func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() != 2:
		push_error("Expected source project.godot and destination project.godot")
		quit(2)
		return
	if FileAccess.file_exists(args[1]):
		push_error("Refusing to overwrite an existing project; choose a new owned staging directory")
		quit(2)
		return
	var config := ConfigFile.new()
	var error := config.load(args[0])
	if error != OK:
		quit(error)
		return
	# Native probes exercise the game, not editor extensions or their accounts.
	if config.has_section("autoload"):
		config.erase_section("autoload")
	config.set_value("editor_plugins", "enabled", PackedStringArray())
	config.set_value("application", "config/use_custom_user_dir", true)
	config.set_value("application", "config/custom_user_dir_name", "Deal or Force Visual Probe 20261028")
	config.set_value("application", "config/project_settings_override", "")
	config.set_value("display", "window/size/mode", 0)
	error = config.save(args[1])
	print("CARTOGRAPHY_PROBE_CONFIG result=", error)
	quit(error)
