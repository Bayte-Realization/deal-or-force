extends RefCounted
## Shared checks for the Luau ScriptLanguageExtension probe.
## Used by probe.gd (editor binary, --script) and release_main.gd (exported
## template_release build). Returns "" on success or the first failure.

const DESCRIPTOR := "res://addons/dof_luau/luau.gdextension"


class EngineHost extends RefCounted:
	## Stands in for the game's ModApi: only plain methods, reached through
	## method Callables in a read-only Dictionary, as EngineScriptMod.CreateApi does.
	var lines: PackedStringArray = []
	var day := 1
	var features := {}

	func log_line(text: String) -> void:
		lines.append(text)

	func get_day() -> int:
		return day

	func set_feature(feature: String, enabled: bool) -> bool:
		features[feature] = enabled
		return true


static func expected_suffixes() -> PackedStringArray:
	var file := FileAccess.open("res://probe_expect.json", FileAccess.READ)
	if file == null:
		return PackedStringArray(["luau"])
	var data = JSON.parse_string(file.get_as_text())
	if typeof(data) != TYPE_DICTIONARY or not data.has("suffixes"):
		return PackedStringArray(["luau"])
	return PackedStringArray(data["suffixes"])


static func run() -> String:
	var status := GDExtensionManager.load_extension(DESCRIPTOR)
	if status not in [GDExtensionManager.LOAD_STATUS_OK, GDExtensionManager.LOAD_STATUS_ALREADY_LOADED]:
		return "extension load: %s" % status
	var found := false
	for index in range(Engine.get_script_language_count()):
		if Engine.get_script_language(index).get_class() == "LuauLanguage":
			found = true
	if not found:
		return "Luau script language registration"

	var expected := expected_suffixes()
	var recognised := ResourceLoader.get_recognized_extensions_for_type("Script")
	for suffix in expected:
		if not recognised.has(suffix):
			return "loader does not recognise .%s (recognised: %s)" % [suffix, ",".join(recognised)]
	print("LUAU_SUFFIXES expected=%s" % ",".join(expected))

	var failure := check_script("res://probe.luau")
	if failure != "":
		return failure
	if expected.has("lua"):
		failure = check_script("res://probe_dual.lua")
		if failure != "":
			return failure
		failure = check_engine_mod()
		if failure != "":
			return failure
		failure = check_module_from_lua()
		if failure != "":
			return failure
	return ""


## Script registration, typed dispatch through a required module, per-instance
## state and read-only native Callable results.
static func check_script(path: String) -> String:
	var script := ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_IGNORE) as Script
	if script == null or not script.can_instantiate():
		return "%s cannot instantiate" % path
	var first := RefCounted.new()
	first.set_script(script)
	var second := RefCounted.new()
	second.set_script(script)
	if first.call("add", 17, 25) != 42:
		return "%s typed method dispatch / module resolution" % path
	if first.call("increment") != 1 or first.call("increment") != 2 or second.call("increment") != 1:
		return "%s independent instance state" % path
	var native_target := RefCounted.new()
	var capabilities: Dictionary = {
		"flag": Callable(native_target, "is_class"),
		"day": Callable(native_target, "get_reference_count"),
	}
	capabilities.make_read_only()
	if first.call("check_capabilities", capabilities) != true:
		return "%s read-only native Callable results" % path
	first.set_script(null)
	second.set_script(null)
	print("LUAU_SCRIPT_PASS path=%s add=42 instances=independent" % path)
	return ""


## The game's EngineScriptMod.Load + Invoke sequence, step for step, on a copy
## of mods/_templates/luau_engine/corecode/main.lua.
static func check_engine_mod() -> String:
	var path := "res://engine_mod/corecode/main.lua"
	var script := ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_IGNORE) as Script
	if script == null or not script.can_instantiate() or script.get_class() != "LuauScript":
		return "engine mod: cannot instantiate %s" % path
	if script.get_instance_base_type() != "RefCounted":
		return "engine mod: entry must extend RefCounted"
	var instance := RefCounted.new()
	instance.set_script(script)
	if instance.get_script() != script or not instance.has_method("on_load"):
		return "engine mod: on_load(api) is not registered"
	var host := EngineHost.new()
	var api: Dictionary = {
		"log": Callable(host, "log_line"),
		"get_day": Callable(host, "get_day"),
		"set_feature": Callable(host, "set_feature"),
	}
	api.make_read_only()
	for hook in ["on_load", "on_campaign_created"]:
		var acknowledgement = instance.call(hook, api)
		if typeof(acknowledgement) != TYPE_BOOL or not acknowledgement:
			return "engine mod: %s did not return true" % hook
	host.day = 364
	if instance.call("on_day_advanced", api) != true or host.features.has("aviation"):
		return "engine mod: on_day_advanced(364)"
	host.day = 365
	if instance.call("on_day_advanced", api) != true or host.features.get("aviation") != true:
		return "engine mod: on_day_advanced(365) did not call set_feature"
	if not host.lines.has("Native Luau campaign mod loaded") or not host.lines.has("Luau sees campaign day 1"):
		return "engine mod: log callable not reached (%s)" % ", ".join(host.lines)
	instance.set_script(null)
	print("LUAU_ENGINE_MOD_PASS on_load=true on_campaign_created=true on_day_advanced=true set_feature=aviation")
	return ""


## A .lua script requiring a .mod.lua module by a path below its own folder.
static func check_module_from_lua() -> String:
	var script := ResourceLoader.load("res://engine_module_check.lua", "", ResourceLoader.CACHE_MODE_IGNORE) as Script
	if script == null or not script.can_instantiate():
		return "engine module check cannot instantiate"
	var instance := RefCounted.new()
	instance.set_script(script)
	var options = instance.call("grid_failure_options")
	instance.set_script(null)
	if options != 3:
		return "war_damage.mod.lua returned %s options, expected 3" % options
	print("LUAU_MODULE_PASS module=war_damage.mod.lua options=3")
	return ""
