extends SceneTree

func _initialize() -> void:
	var bridge := load("res://FSharpBridge.cs") as Script
	if bridge == null or not bridge.can_instantiate():
		fail("managed C# bridge cannot instantiate")
		return
	var first = bridge.new()
	var second = bridge.new()
	if first.WeightedScore(PackedInt64Array([2, 4, 6]), PackedInt64Array([1, 4, 4])) != 42:
		fail("F# deterministic calculation called from Godot")
		return
	if first.Increment() != 1 or first.Increment() != 2 or second.Increment() != 1:
		fail("independent F# instance state")
		return
	if not first.VerifyRejectedInput():
		fail("managed boundary input validation")
		return
	print("LANGUAGE_KIT_PASS language=fsharp managed_bridge=true score=42 instances=true invalid_input=true")
	quit(0)

func fail(message: String) -> void:
	push_error("LANGUAGE_KIT_FAIL fsharp: " + message)
	quit(1)
