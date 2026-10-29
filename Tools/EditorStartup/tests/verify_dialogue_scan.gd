extends SceneTree

var failures: int = 0

func check(ok: bool, label: String) -> void:
	print(("PASS " if ok else "FAIL ") + label)
	if not ok:
		failures += 1

func fixture_file(path: String, text: String) -> void:
	DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	var file: FileAccess = FileAccess.open(path, FileAccess.WRITE)
	assert(file != null)
	file.store_string(text)
	file.close()

func _initialize() -> void:
	if not FileAccess.file_exists("res://.dof-fennara-fixture"):
		printerr("Refusing fixture writes outside an explicitly marked test directory")
		quit(2)
		return
	var args: PackedStringArray = OS.get_cmdline_user_args()
	if args.size() != 1:
		printerr("Expected the production dialogue_cache.gd path")
		quit(2)
		return
	# Compile the production function verbatim, without unrelated cache/compiler
	# global classes. This does not claim to test the whole Dialogue Manager UI.
	var source: String = FileAccess.get_file_as_string(args[0]).replace("\r\n", "\n")
	var start: int = source.find("static func _get_dialogue_files_in_filesystem(")
	var finish: int = source.find("\n#region Signals", start)
	check(start >= 0 and finish > start, "production scan function located")
	if start < 0 or finish <= start:
		quit(1)
		return
	var script: GDScript = GDScript.new()
	script.source_code = "extends RefCounted\n\n" + source.substr(start, finish - start) + "\n"
	check(script.reload() == OK, "production scan function compiles")
	fixture_file("res://root.dialogue", "~ start\nNarrator: Root\n=> END\n")
	fixture_file("res://stories/nested/kept.dialogue", "~ start\nNarrator: Kept\n=> END\n")
	fixture_file("res://ignored/.gdignore", "")
	fixture_file("res://ignored/ignored.dialogue", "ignored")
	fixture_file("res://other_project/foreign.dialogue", "foreign")
	var child: ConfigFile = ConfigFile.new()
	child.set_value("application", "config/name", "Separate dialogue fixture")
	check(child.save("res://other_project/project.godot") == OK, "nested project serialized by Godot")
	fixture_file("res://.agent-cache/hidden.dialogue", "hidden")
	var files: PackedStringArray = script.call("_get_dialogue_files_in_filesystem")
	check(files.has("res://root.dialogue"), "root dialogue remains visible")
	check(files.has("res://stories/nested/kept.dialogue"), "ordinary nested dialogue remains visible")
	check(files.size() == 2, "ignored, foreign, hidden and junction dialogues excluded")
	check(DirAccess.open("res://").is_link("loop"), "real cyclic junction in fixture")
	var missing: PackedStringArray = script.call("_get_dialogue_files_in_filesystem", "res://absent")
	check(missing.is_empty(), "missing directory handled without null access")
	var nested: PackedStringArray = script.call("_get_dialogue_files_in_filesystem", "res://stories")
	check(nested.size() == 1 and nested[0] == "res://stories/nested/kept.dialogue", "explicit ordinary subtree still works")
	script = null
	print("DIALOGUE_SCAN_VERIFY failures=" + str(failures))
	quit(0 if failures == 0 else 1)
