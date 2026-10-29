extends SceneTree

# Load-only: the engine started, the py4godot descriptor was accepted, a script ran, and the
# process exits 0. A Python class is not instantiated here - the example's names belong to
# py4godot, not to this kit. See README.md for why that boundary is deliberate.
func _initialize() -> void:
	print("PYTHON_PROBE PASS")
	quit(0)
