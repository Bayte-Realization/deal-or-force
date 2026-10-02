extends RefCounted
# Fennara runtime_script probe. Precondition: a paused Russian campaign, support
# panel open, one formation selected, 3D projection. Only drives view/input, no saves.

func check_flat(ctx: Variant, label: String) -> void:
	var root: Node = ctx.get_scene_root()
	var camera: Camera3D = root.find_children("*", "Camera3D", true, false)[0]
	var height: float = camera.get_viewport().get_visible_rect().size.y
	var army_max: float = 0.0
	var support_max: float = 0.0
	var armies: int = 0
	var formations: int = 0
	for node: Node in root.find_children("DivisionStack_*", "Node3D", true, false):
		var plate: Sprite3D = node.get_child(0)
		var count: Label3D = node.find_child("DivisionCount", true, false)
		var width: float = plate.texture.get_width() * plate.pixel_size * node.scale.x * plate.scale.x * height / camera.size
		army_max = maxf(army_max, width)
		armies += 1
		if plate.fixed_size or count.fixed_size or count.offset != Vector2(1, 4):
			ctx.error("Incorrect flat army label/card layout")
		var bars: Sprite3D = node.find_child("OrganisationEquipmentBars", true, false)
		if bars != null and bars.visible and bars.texture != null:
			if bars.position != plate.position or bars.offset != plate.offset or bars.texture.get_size() != plate.texture.get_size() or bars.pixel_size != plate.pixel_size or bars.fixed_size != plate.fixed_size:
				ctx.error("Condition bars detached from their plate canvas/origin/scale")
	for node: Node in root.find_children("LongRange_*", "Node3D", true, false):
		var plate: Sprite3D = node.get_child(0)
		var width: float = plate.texture.get_width() * plate.pixel_size * node.scale.x * plate.scale.x * height / camera.size
		support_max = maxf(support_max, width)
		formations += 1
	ctx.log(label, {"camera_size": camera.size, "army_count": armies, "army_max_px": army_max, "formations": formations, "support_max_px": support_max})
	if camera.projection != Camera3D.PROJECTION_ORTHOGONAL:
		ctx.error("Expected flat projection")
	if armies == 0 or formations == 0:
		ctx.error("Missing live military counters")
	if not is_finite(army_max) or not is_finite(support_max) or army_max > 100 or support_max > 100 or army_max < 1 or support_max < 1:
		ctx.error("Counter dimensions outside safe pixel range")

func run(ctx: Variant) -> void:
	var root: Node = ctx.get_scene_root()
	var camera: Camera3D = root.find_children("*", "Camera3D", true, false)[0]
	if camera.projection != Camera3D.PROJECTION_PERSPECTIVE:
		ctx.error("Start this regression in 3D projection")
		return
	var window: Window = root.get_window()
	var original_size: Vector2i = window.size
	var original_canvas: Vector2i = window.content_scale_size
	window.size = Vector2i(1920, 1080)
	window.content_scale_size = Vector2i(1920, 1080)
	await ctx.wait(0.3)
	await ctx.tap_key(KEY_F2, {"physical": false})
	await ctx.wait(0.3)
	check_flat(ctx, "Flat operational 1080")
	await ctx.capture("counter-regression-flat-operational", 1920)
	for step: int in range(22):
		await ctx.click_at(Vector2(1100, 650), {"button": MOUSE_BUTTON_WHEEL_DOWN})
	await ctx.wait(0.6)
	check_flat(ctx, "Flat far limit")
	for step: int in range(24):
		await ctx.click_at(Vector2(1100, 650), {"button": MOUSE_BUTTON_WHEEL_UP})
	await ctx.wait(0.6)
	check_flat(ctx, "Flat near limit")
	for step: int in range(2):
		await ctx.tap_key(KEY_F2, {"physical": false})
		await ctx.wait(0.2)
	check_flat(ctx, "Flat after projection round trip")
	window.size = Vector2i(1280, 720)
	window.content_scale_size = Vector2i(1280, 720)
	await ctx.wait(0.4)
	check_flat(ctx, "Flat native 720")
	await ctx.tap_key(KEY_F2, {"physical": false})
	await ctx.wait(0.3)
	root = ctx.get_scene_root()
	camera = root.find_children("*", "Camera3D", true, false)[0]
	if camera.projection != Camera3D.PROJECTION_PERSPECTIVE:
		ctx.error("Globe projection did not restore")
	for node: Node in root.find_children("DivisionStack_*", "Node3D", true, false):
		var plate: Sprite3D = node.get_child(0)
		var count: Label3D = node.find_child("DivisionCount", true, false)
		if plate.fixed_size or count.fixed_size or plate.billboard != BaseMaterial3D.BILLBOARD_DISABLED or count.billboard != BaseMaterial3D.BILLBOARD_DISABLED or count.position != Vector3.ZERO:
			ctx.error("Globe card retained billboard or detached text")
		if node.scale.x <= 0.0 or node.scale.x > 0.121:
			ctx.error("Globe card exceeded physical surface footprint")
	window.size = original_size
	window.content_scale_size = original_canvas
	await ctx.wait(0.2)
	ctx.release_all_inputs()
	ctx.log("Counter projection regression finished")
