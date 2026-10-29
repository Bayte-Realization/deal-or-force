extends SceneTree

# Godot owns its config serialization; no hand-edited project.godot templates.
func _initialize() -> void:
	var args = OS.get_cmdline_user_args()
	if args.size() != 2:
		push_error("Expected output project.godot path and title")
		quit(2)
		return
	var config := ConfigFile.new()
	config.set_value("", "config_version", 5)
	config.set_value("application", "config/name", args[1])
	config.set_value("rendering", "renderer/rendering_method", "gl_compatibility")
	config.set_value("rendering", "renderer/rendering_method.mobile", "gl_compatibility")
	var error := config.save(args[0])
	if error != OK:
		push_error("Cannot serialize probe project: %s" % error)
		quit(1)
		return
	print("PROBE_PROJECT_CREATED")
	quit(0)
