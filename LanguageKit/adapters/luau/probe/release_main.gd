extends Node
## Main scene of the exported (template_release) probe: same checks as probe.gd.

func _ready() -> void:
	_run.call_deferred()

func _run() -> void:
	var core = load("res://probe_core.gd")
	var failure: String = core.run()
	if failure != "":
		push_error("LANGUAGE_KIT_FAIL luau: " + failure)
		get_tree().quit(1)
		return
	print("LANGUAGE_KIT_PASS language=luau script=true methods=true instances=true native_callables=true modules=true build=%s" % ("debug" if OS.is_debug_build() else "release"))
	get_tree().quit(0)
