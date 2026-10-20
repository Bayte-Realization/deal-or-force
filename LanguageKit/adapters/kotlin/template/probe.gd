extends SceneTree

func _initialize() -> void:
	var script: Script = load("res://src/main/kotlin/dof/languages/LanguageProbe.kt")
	if script == null or not script.can_instantiate():
		push_error("KOTLIN_PROBE: script could not be instantiated")
		quit(1)
		return
	var instance: RefCounted = script.new()
	var sum: int = instance.call("add_checked", 19, 23)
	var backend: String = instance.call("backend_label")
	if sum != 42 or backend != "Kotlin/JVM":
		push_error("KOTLIN_PROBE: unexpected result %s / %s" % [sum, backend])
		quit(1)
		return
	print("KOTLIN_PROBE:PASS sum=42 backend=Kotlin/JVM")
	instance = null
	quit(0)
