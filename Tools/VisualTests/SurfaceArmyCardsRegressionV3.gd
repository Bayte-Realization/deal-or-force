extends RefCounted
# Paused Russia campaign. Native edge click must change an unselected card.

func inspect(ctx: Variant, label: String) -> void:
	var root: Node = ctx.get_scene_root()
	var camera: Camera3D = root.find_children("*", "Camera3D", true, false)[0]
	var flat: bool = camera.projection == Camera3D.PROJECTION_ORTHOGONAL
	var checked: int = 0
	var visible: int = 0
	var worst_normal: float = 0.0
	for node: Node3D in root.find_children("DivisionStack_*", "Node3D", true, false):
		var plate: Sprite3D = node.get_child(0)
		var count: Label3D = node.find_child("DivisionCount", true, false)
		var bars: Sprite3D = node.find_child("OrganisationEquipmentBars", true, false)
		var glow: Sprite3D = plate.find_child("SelectionGlow", true, false)
		var expected: Vector3 = Vector3.BACK if flat else node.position.normalized()
		var error: float = 1.0 - node.basis.z.normalized().dot(expected)
		worst_normal = maxf(worst_normal, absf(error))
		if absf(error) > 0.0001:
			ctx.error("Surface normal mismatch: " + str(node.name))
		for layer: Variant in [plate, count, bars, glow]:
			if layer.billboard != BaseMaterial3D.BILLBOARD_DISABLED or layer.fixed_size:
				ctx.error("Layer has independent billboard/size compensation")
		if count.position != Vector3.ZERO or bars.position != Vector3.ZERO:
			ctx.error("Text or bars detached from surface plane")
		if bars.texture != null and bars.visible and bars.pixel_size != plate.pixel_size:
			ctx.error("Bar/card pixel scales differ")
		if not flat and (node.scale.x <= 0.0 or node.scale.x > 0.121):
			ctx.error("Surface footprint exceeded limit")
		checked += 1
		if node.is_visible_in_tree() and plate.visible: visible += 1
	ctx.log(label, {"checked": checked, "visible": visible, "flat": flat, "normal_error": worst_normal})
	if checked == 0 or visible == 0: ctx.error("No visible division cards")

func run(ctx: Variant) -> void:
	var root: Node = ctx.get_scene_root()
	var pause_menu: Node = root.find_child("PauseMenu", true, false)
	if pause_menu != null and pause_menu.is_visible_in_tree():
		await ctx.tap_key(KEY_ESCAPE, {"physical": false})
		await ctx.wait(0.2)
	var support_row: Node = root.find_child("SupportRow_118", true, false)
	if support_row != null and support_row.is_visible_in_tree():
		await ctx.tap_key(KEY_ESCAPE, {"physical": false})
		await ctx.wait(0.2)
	root = ctx.get_scene_root()
	var camera: Camera3D = root.find_children("*", "Camera3D", true, false)[0]
	if camera.projection == Camera3D.PROJECTION_ORTHOGONAL:
		await ctx.tap_key(KEY_F2, {"physical": false})
		await ctx.wait(0.4)
	inspect(ctx, "Globe front")
	await ctx.capture("surface-cards-globe-front", 1920)
	var start: Vector2 = Vector2(1120, 620)
	ctx.press_mouse(MOUSE_BUTTON_MIDDLE, {"position": start})
	await ctx.move_mouse_to(start + Vector2(130, 50), {"steps": 12, "duration": 0.3, "button_mask": MOUSE_BUTTON_MASK_MIDDLE})
	ctx.release_mouse(MOUSE_BUTTON_MIDDLE, {"position": start + Vector2(130, 50)})
	await ctx.wait(0.8)
	inspect(ctx, "Globe oblique")
	await ctx.capture("surface-cards-globe-oblique", 1920)
	await ctx.tap_key(KEY_F2, {"physical": false})
	await ctx.wait(0.5)
	inspect(ctx, "Flat north up")
	await ctx.capture("surface-cards-flat", 1920)
	await ctx.tap_key(KEY_F2, {"physical": false})
	await ctx.wait(0.5)
	inspect(ctx, "Globe round trip")
	# Use the same public focus path as the UI, not a replacement camera rig.
	for map: Node in ctx.get_scene_root().find_children("*", "SubViewportContainer", true, false):
		if map.has_method("FocusOn"):
			map.call("SelectArmy", -1)
			map.call("FocusOn", 50.0, 36.0, 5.45)
	await ctx.wait(1.0)
	inspect(ctx, "Globe tactical close")
	root = ctx.get_scene_root()
	camera = root.find_children("*", "Camera3D", true, false)[0]
	var selected_name: String = ""
	for card: Node3D in root.find_children("DivisionStack_RUS_*", "Node3D", true, false):
		var plate: Sprite3D = card.get_child(0)
		if not plate.is_visible_in_tree(): continue
		var half: Vector2 = plate.texture.get_size() * plate.pixel_size * 0.5
		var edge: Vector2 = camera.unproject_position(plate.to_global(Vector3(half.x * 0.75, half.y * 0.2, 0)))
		if not Rect2(800, 220, 700, 500).has_point(edge): continue
		selected_name = str(card.name)
		if card.find_child("SelectionGlow", true, false).visible:
			ctx.error("Selection was not cleared before edge click")
		await ctx.click_at(edge)
		break
	await ctx.wait(0.4)
	var selected: Node = ctx.get_scene_root().find_child(selected_name, true, false)
	if selected == null or not selected.find_child("SelectionGlow", true, false).visible:
		ctx.error("Native click on tilted card edge failed")
	else: ctx.log("Native tilted edge selection passed", {"card": selected_name})
	await ctx.capture("surface-cards-tactical-selected", 1920)
	ctx.release_all_inputs()
