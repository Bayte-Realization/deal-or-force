extends RefCounted
# Paused Russia campaign, via Fennara runtime_script. No save/simulation mutation.
# Uses actual/display membership to catch both fanning and silently lost divisions.

func inspect(ctx: Variant, label: String, baseline: Dictionary = {}) -> Dictionary:
	var keys: Dictionary = {}
	var members: Dictionary = {}
	var displayed: Dictionary = {}
	var multi: int = 0
	var visible: int = 0
	var centered: int = 0
	var max_center_error: float = 0.0
	var camera: Camera3D = ctx.get_scene_root().find_children("*", "Camera3D", true, false)[0]
	for node: Node3D in ctx.get_scene_root().find_children("DivisionStack_*", "Node3D", true, false):
		var ids: Array = node.get_meta("actual_army_ids", [])
		var province: int = node.get_meta("province_id", 0)
		var nation: String = str(node.name).split("_")[1]
		if node.get_meta("display_provinces", 0) != 1:
			ctx.error("Counter combines unrelated provinces: " + str(node.name))
		var display_ids: Array = node.get_meta("display_army_ids", [])
		if display_ids != ids: ctx.error("Counter no longer represents its own province")
		if province > 0:
			if not node.has_meta("province_center_latlon"):
				ctx.error("Missing authoritative province centre")
			else:
				var geo: Vector2 = node.get_meta("province_center_latlon")
				var expected: Vector3
				if camera.projection == Camera3D.PROJECTION_ORTHOGONAL:
					var x: float = geo.y / 360.0 * 20.0
					while x - camera.position.x > 10.0: x -= 20.0
					while x - camera.position.x < -10.0: x += 20.0
					expected = Vector3(x, geo.x / 180.0 * 10.0, 0.004)
				else:
					var lat: float = deg_to_rad(geo.x)
					var lon: float = deg_to_rad(geo.y + 180.0)
					expected = Vector3(cos(lat) * sin(lon), sin(lat), cos(lat) * cos(lon)) * 5.011
				var error: float = node.position.distance_to(expected)
				max_center_error = maxf(max_center_error, error)
				centered += 1
				if error > 0.00001:
					ctx.error("Counter displaced from province centre: " + str(node.name))
		if province > 0:
			var key: String = nation + ":" + str(province)
			if keys.has(key): ctx.error("More than one counter in province: " + key)
			keys[key] = true
		if ids.size() > 1: multi += 1
		for id: int in ids:
			if members.has(id): ctx.error("Duplicate actual division: " + str(id))
			members[id] = province
		var plate: Sprite3D = node.get_child(0)
		var count: Label3D = node.find_child("DivisionCount", true, false)
		var bars: Sprite3D = node.find_child("OrganisationEquipmentBars", true, false)
		if count.visible and not plate.visible: ctx.error("Orphan counter text")
		if bars.visible and not plate.visible: ctx.error("Orphan condition bars")
		if not node.is_visible_in_tree() or not plate.visible: continue
		visible += 1
		for id: int in node.get_meta("display_army_ids", []):
			if displayed.has(id): ctx.error("Division drawn twice: " + str(id))
			displayed[id] = true
	if not baseline.is_empty() and baseline != members:
		ctx.error("Zoom/projection/selection changed actual stack membership")
	ctx.log(label, {"stacks": keys.size(), "divisions": members.size(), "multi_stacks": multi, "visible": visible,
		"centered": centered, "max_center_error": max_center_error})
	if centered != keys.size(): ctx.error("Every province counter must be centred, including marching stacks")
	if multi == 0 or visible == 0: ctx.error("Fixture needs visible counters and multi-division provinces")
	return members

func roster(ctx: Variant) -> Array[Button]:
	var result: Array[Button] = []
	for button: Button in ctx.get_scene_root().find_children("*", "Button", true, false):
		if button.is_visible_in_tree() and button.flat and button.text.contains("RUS Division"):
			result.append(button)
	return result

func run(ctx: Variant) -> void:
	for label: Label in ctx.get_scene_root().find_children("*", "Label", true, false):
		if label.is_visible_in_tree() and label.text in ["LONG-RANGE FORCES", "ДАЛЬНОБОЙНЫЕ СИЛЫ"]:
			await ctx.tap_key(KEY_ESCAPE, {"physical": false})
			break
	await ctx.wait(0.3)
	var root: Node = ctx.get_scene_root()
	var camera: Camera3D = root.find_children("*", "Camera3D", true, false)[0]
	if camera.projection != Camera3D.PROJECTION_PERSPECTIVE:
		await ctx.tap_key(KEY_F2, {"physical": false})
		await ctx.wait(0.4)
	var map: Node = null
	for candidate: Node in ctx.get_scene_root().find_children("*", "SubViewportContainer", true, false):
		if candidate.has_method("FocusOn"): map = candidate; break
	if map == null:
		ctx.error("Map presentation not found")
		return
	map.call("SelectArmy", -1)
	# Find the largest existing Russian province, including mixed templates.
	var target: Node3D = null
	var most: int = 1
	for node: Node3D in ctx.get_scene_root().find_children("DivisionStack_RUS_*", "Node3D", true, false):
		var ids: Array = node.get_meta("actual_army_ids", [])
		if ids.size() > most:
			target = node
			most = ids.size()
	if target == null:
		ctx.error("No multi-division Russian province in fixture")
		return
	var target_name: String = str(target.name)
	var normal: Vector3 = target.position.normalized()
	var latitude: float = rad_to_deg(asin(normal.y))
	var longitude: float = wrapf(rad_to_deg(atan2(normal.x, normal.z)) - 180.0, -180.0, 180.0)
	map.call("FocusOn", latitude, longitude, 5.45)
	await ctx.wait(1.2)
	var baseline: Dictionary = inspect(ctx, "Globe close: one stack per province")
	await ctx.capture("province-stack-globe-close", 1920)
	map.call("FocusOn", latitude, longitude, 10.0)
	await ctx.wait(1.2)
	inspect(ctx, "Globe overview remains province-centred", baseline)
	await ctx.capture("province-stack-globe-overview", 1920)
	map.call("FocusOn", latitude, longitude, 5.45)
	await ctx.wait(1.2)
	await ctx.tap_key(KEY_F2, {"physical": false})
	await ctx.wait(0.5)
	inspect(ctx, "Flat tactical entry", baseline)
	for step: int in range(24):
		camera = ctx.get_scene_root().find_children("*", "Camera3D", true, false)[0]
		if camera.size <= 0.1001: break
		await ctx.click_at(Vector2(960, 540), {"button": MOUSE_BUTTON_WHEEL_UP})
	await ctx.wait(0.8)
	inspect(ctx, "Flat maximum zoom", baseline)
	camera = ctx.get_scene_root().find_children("*", "Camera3D", true, false)[0]
	if absf(camera.size - 0.1) > 0.002: ctx.error("Flat close limit changed")
	var near_zoom: float = camera.size
	await ctx.tap_key(KEY_F2, {"physical": false})
	await ctx.wait(0.4)
	inspect(ctx, "Globe projection round trip", baseline)
	await ctx.tap_key(KEY_F2, {"physical": false})
	await ctx.wait(0.5)
	camera = ctx.get_scene_root().find_children("*", "Camera3D", true, false)[0]
	ctx.log("Close zoom round trip", {"before": near_zoom, "after": camera.size})
	if absf(camera.size - near_zoom) > 0.002: ctx.error("Projection round trip lost close zoom")
	target = ctx.get_scene_root().find_child(target_name, true, false)
	if target == null or not target.get_child(0).is_visible_in_tree():
		ctx.error("Province stack disappeared at close zoom")
		return
	var expected: Array = target.get_meta("display_army_ids", [])
	await ctx.click_at(camera.unproject_position(target.global_position))
	await ctx.wait(0.4)
	var rows: Array[Button] = roster(ctx)
	ctx.log("Native stack click", {"expected": expected.size(), "roster_rows": rows.size()})
	if rows.size() != expected.size(): ctx.error("Stack click did not select every division")
	await ctx.capture("province-stack-flat-selected", 1920)
	if not rows.is_empty():
		await ctx.click_button(rows[0])
		await ctx.wait(0.4)
		if roster(ctx).size() != 1: ctx.error("Cannot select an individual division from stack roster")
		inspect(ctx, "Partial selection keeps province stacked", baseline)
		await ctx.capture("province-stack-flat-single-selected", 1920)
	# Clicking a roster row intentionally focuses its division, including the zoom.
	# Test the close zoom round trip BEFORE that focus, not halfway through its tween.
	map.call("FocusOn", latitude, longitude, 5.9)
	await ctx.wait(1.2)
	inspect(ctx, "Flat operational zoom", baseline)
	await ctx.capture("province-stack-flat-operational", 1920)
	for step: int in range(16):
		await ctx.click_at(Vector2(960, 540), {"button": MOUSE_BUTTON_WHEEL_DOWN})
	await ctx.wait(0.8)
	inspect(ctx, "Flat overview remains province-centred", baseline)
	await ctx.capture("province-stack-flat-overview", 1920)
	ctx.release_all_inputs()
