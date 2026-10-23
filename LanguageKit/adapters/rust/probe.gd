extends SceneTree

func _initialize() -> void:
	var kernel = ClassDB.instantiate("DofRustKernel")
	if kernel == null:
		push_error("RUST_PROBE class missing")
		quit(1)
		return
	var result: int = kernel.weighted_sum(PackedInt32Array([3, -2, 7]))
	if result != 20 or kernel.language_name() != "Rust" or kernel.weighted_sum(PackedInt32Array()) != 0:
		push_error("RUST_PROBE wrong result: %s" % result)
		quit(1)
		return
	print("RUST_PROBE PASS weighted_sum=20 empty=", kernel.weighted_sum(PackedInt32Array()))
	quit(0)
