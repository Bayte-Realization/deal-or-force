extends SceneTree
## Serialize a tooling-free export configuration in a separate staging checkout.
## This process never calls ProjectSettings.save() on the developer's project.

func _initialize() -> void:
	var args: PackedStringArray = OS.get_cmdline_user_args()
	if args.size() != 2:
		push_error("Expected the source and staging project.godot paths")
		quit(2)
		return
	var source: String = args[0].replace("\\", "/").simplify_path()
	var destination: String = args[1].replace("\\", "/").simplify_path()
	var allowed: String = source.get_base_dir().path_join("build") + "/"
	if source.get_file() != "project.godot" or not destination.begins_with(allowed) or destination.get_file() != "project.godot":
		push_error("Export configuration must remain inside the project's build folder")
		quit(2)
		return
	var source_settings := ConfigFile.new()
	var load_result: Error = source_settings.load(source)
	if load_result != OK:
		push_error("Unable to read the source project.godot: %s" % load_result)
		quit(2)
		return
	for section: String in source_settings.get_sections():
		for key: String in source_settings.get_section_keys(section):
			ProjectSettings.set_setting(section + "/" + key, source_settings.get_value(section, key))
	var removed: int = 0
	for property: Dictionary in ProjectSettings.get_property_list():
		var key: String = property.name
		if key.begins_with("autoload/"):
			var path: String = str(ProjectSettings.get_setting(key))
			if path.trim_prefix("*").begins_with("res://addons/"):
				ProjectSettings.set_setting(key, null)
				removed += 1
	ProjectSettings.set_setting("editor_plugins/enabled", PackedStringArray())
	var result: Error = ProjectSettings.save_custom(destination)
	print("EXPORT_CONFIG result=", result, " development_autoloads_removed=", removed)
	quit(0 if result == OK else 1)
