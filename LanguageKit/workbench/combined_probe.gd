extends SceneTree

var failures: Array[String] = []

func _initialize() -> void:
	_run.call_deferred()

func expect(condition: bool, message: String) -> void:
	if not condition:
		failures.append(message)
		push_error("LANGUAGE_WORKBENCH_FAIL " + message)

func _run() -> void:
	var requested := OS.get_cmdline_user_args()
	if requested.is_empty():
		requested = PackedStringArray(["luau", "fsharp", "rust"])
	for language in requested:
		var descriptor: String = "res://addons/dof_%s/dof_%s.gdextension" % [language, language]
		if language == "luau": descriptor = "res://addons/dof_luau/luau.gdextension"
		if language == "kotlin": descriptor = "res://addons/jvm/jvm.gdextension"
		if language == "lua": descriptor = "res://addons/dof_lua/lua.gdextension"
		if language == "sandbox": descriptor = "res://addons/godot_sandbox/bin/godot-riscv.gdextension"
		if language == "fsharp": continue
		if not FileAccess.file_exists(descriptor):
			expect(false, "missing descriptor: " + descriptor)
			continue
		var status := GDExtensionManager.load_extension(descriptor)
		expect(status in [GDExtensionManager.LOAD_STATUS_OK, GDExtensionManager.LOAD_STATUS_ALREADY_LOADED], "load " + language)
	if not failures.is_empty():
		quit(1)
		return
	if "fsharp" in requested:
		var managed := load("res://FSharpBridge.cs") as Script
		expect(managed != null and managed.can_instantiate(), "F# managed bridge registration")
		if managed != null and managed.can_instantiate():
			var first = managed.new()
			var second = managed.new()
			expect(first.WeightedScore(PackedInt64Array([2, 4, 6]), PackedInt64Array([1, 4, 4])) == 42, "F# batch call")
			expect(first.Increment() == 1 and first.Increment() == 2 and second.Increment() == 1, "F# instance isolation")
			expect(first.VerifyRejectedInput(), "F# rejected malformed input")
	if "luau" in requested:
		var luau := load("res://probe.luau") as Script
		expect(luau != null and luau.can_instantiate(), "Luau script registration")
		if luau != null and luau.can_instantiate():
			var first := RefCounted.new()
			var second := RefCounted.new()
			first.set_script(luau)
			second.set_script(luau)
			expect(first.call("add", 17, 25) == 42, "Luau typed method and module resolution")
			expect(first.call("increment") == 1 and first.call("increment") == 2 and second.call("increment") == 1, "Luau instance isolation")
			first.set_script(null)
			second.set_script(null)
	for language in ["rust", "nim"]:
		if language not in requested: continue
		var class_name_text: String = "DofRustKernel" if language == "rust" else "DofNimKernel"
		expect(ClassDB.class_exists(class_name_text), language + " native class registration")
		if not ClassDB.class_exists(class_name_text): continue
		var kernel = ClassDB.instantiate(class_name_text)
		expect(kernel.call("weighted_sum", PackedInt32Array([3, -2, 7])) == 20, language + " packed-array batch call")
		expect(kernel.call("weighted_sum", PackedInt32Array()) == 0, language + " empty batch")
	if "odin" in requested:
		expect(ClassDB.class_exists("DofOdinKernel"), "Odin native class registration")
		if ClassDB.class_exists("DofOdinKernel"):
			var kernel = ClassDB.instantiate("DofOdinKernel")
			expect(kernel.call("weighted_sum3", 3, -2, 7) == 20 and kernel.call("weighted_sum3", 0, 0, 0) == 0, "Odin method and zero input")
	if "kotlin" in requested:
		var kotlin := load("res://src/main/kotlin/dof/languages/LanguageProbe.kt") as Script
		expect(kotlin != null and kotlin.can_instantiate(), "Kotlin script registration")
		if kotlin != null and kotlin.can_instantiate():
			var instance = kotlin.new()
			expect(instance.call("add_checked", 19, 23) == 42 and instance.call("backend_label") == "Kotlin/JVM", "Kotlin dispatch")
	if "lua" in requested:
		var lua := load("res://lua_probe.lua") as Script
		expect(lua != null and lua.can_instantiate(), "Lua is distinct from Luau")
		if lua != null and lua.can_instantiate():
			var first := RefCounted.new()
			var second := RefCounted.new()
			first.set_script(lua)
			second.set_script(lua)
			expect(first.call("add", 19, 23) == 42, "Lua dispatch")
			expect(first.call("increment") == 1 and first.call("increment") == 2 and second.call("increment") == 1, "Lua instance isolation")
			first.set_script(null)
			second.set_script(null)
	if "sandbox" in requested:
		expect(ClassDB.class_exists("SafeGDScript"), "sandbox script registration")
		if ClassDB.class_exists("SafeGDScript"):
			var safe_script: Script = ClassDB.instantiate("SafeGDScript")
			safe_script.set_source_code("func bootstrap():\n\tpass\n")
			var node := Node.new()
			node.set_script(safe_script)
			node.set("restrictions", true)
			node.set("execution_timeout", 10)
			node.set("memory_max", 16)
			node.set("allocations_max", 1000)
			node.set("references_max", 16)
			safe_script.set_source_code("func add_checked(left: int, right: int) -> int:\n\treturn left + right\n")
			expect(node.call("add_checked", 19, 23) == 42 and node.get("monitor_exceptions") == 0 and node.get("restrictions"), "sandbox bounded arithmetic sample")
			node.free()
	if failures.is_empty():
		print("LANGUAGE_WORKBENCH_PASS languages=", ",".join(requested), " failures=0")
	quit(0 if failures.is_empty() else 1)
