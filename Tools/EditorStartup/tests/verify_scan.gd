extends SceneTree
var failures := 0

func check(ok: bool, label: String) -> void:
	print(("PASS " if ok else "FAIL ") + label)
	if not ok:
		failures += 1

func fixture_file(path: String, text: String) -> void:
	DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	var file := FileAccess.open(path, FileAccess.WRITE)
	assert(file != null)
	file.store_string(text)
	file.close()

func _initialize() -> void:
	if not FileAccess.file_exists("res://.dof-fennara-fixture"):
		printerr("Refusing fixture writes outside an explicitly marked test directory")
		quit(2)
		return
	var settings := ConfigFile.new()
	settings.set_value("configuration", "entry_symbol", "fennara_scan_probe_init")
	settings.set_value("configuration", "compatibility_minimum", "4.5")
	settings.set_value("libraries", "windows.debug.x86_64", "res://scan_probe.dll")
	settings.set_value("libraries", "windows.release.x86_64", "res://scan_probe.dll")
	check(settings.save("res://scan_probe.gdextension") == OK, "probe descriptor serialized by Godot")
	check(GDExtensionManager.load_extension("res://scan_probe.gdextension") == GDExtensionManager.LOAD_STATUS_OK, "native scanner probe loads")
	fixture_file("res://Root.csproj", "<Project />")
	fixture_file("res://modules/library/Library.csproj", "<Project />")
	fixture_file("res://ignored/.gdignore", "")
	fixture_file("res://ignored/decoy/Root.csproj", "<Project />")
	fixture_file("res://ignored/decoy/ignored.gd", "invalid sentinel")
	fixture_file("res://other_project/Hidden.csproj", "<Project />")
	fixture_file("res://other_project/hidden.gd", "invalid sentinel")
	var child := ConfigFile.new()
	child.set_value("application", "config/name", "Separate fixture")
	check(child.save("res://other_project/project.godot") == OK, "nested project serialized by Godot")
	fixture_file("res://source/kept.gd", "extends RefCounted\n")
	fixture_file("res://obj/generated.gd", "invalid sentinel")
	fixture_file("res://.agent-cache/snapshot/Hidden.csproj", "<Project />")
	fixture_file("res://.agent-cache/snapshot/hidden_cache.gd", "invalid sentinel")
	ProjectSettings.set_setting("dotnet/project/assembly_name", "Root")
	var probe: RefCounted = ClassDB.instantiate("DofEditorScanProbe")
	var data: Dictionary = probe.call("inspect")
	var support: Dictionary = data.csharp
	check(support.state == "ready", "root C# project remains ready")
	check(support.project_count == 2, "ignored and nested-project decoys are excluded")
	check(not support.projects.any(func(item): return "/.agent-cache/" in str(item.path)), "hidden tool snapshots are excluded from project discovery")
	check(support.selected_project.path == "res://Root.csproj", "root entry project remains selected")
	var scripts: Array = data.scripts
	check(scripts.any(func(path): return str(path).ends_with("/source/kept.gd")), "ordinary source remains indexed")
	check(not scripts.any(func(path): return "/.agent-cache/" in str(path)), "hidden tool snapshots are excluded from source indexing")
	check(not scripts.any(func(path): return str(path).ends_with("/ignored.gd") or str(path).ends_with("/hidden.gd") or str(path).ends_with("/generated.gd")), "automatic index excludes ignored, nested and generated scripts")
	check(support.projects.any(func(item): return item.path == "res://modules/library/Library.csproj"), "legitimate nested C# library is still discoverable")
	check(DirAccess.open("res://").is_link("loop"), "fixture includes a real directory junction")
	check(not scripts.any(func(path): return "/loop/" in str(path)), "junction is not followed by source indexing")
	DirAccess.remove_absolute(ProjectSettings.globalize_path("res://Root.csproj"))
	ProjectSettings.set_setting("dotnet/project/assembly_name", "Library")
	ProjectSettings.set_setting("dotnet/project/project_directory", "res://modules/library")
	data = probe.call("inspect")
	check(data.csharp.selected_project.path == "res://modules/library/Library.csproj", "explicit non-root C# directory still works")
	print("SCAN_VERIFY failures=" + str(failures))
	probe = null
	quit(0 if failures == 0 else 1)
