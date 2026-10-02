extends RefCounted
# Paused Russia campaign. Public view controls, real native clicks and mesh inspection.
# No campaign edits, orders, saves or scene replacements.

func map_control(ctx: Variant) -> Node:
	for node: Node in ctx.get_scene_root().find_children("*", "SubViewportContainer", true, false):
		if node.has_method("FocusOn"): return node
	return null

func camera(ctx: Variant) -> Camera3D:
	return ctx.get_scene_root().find_children("*", "Camera3D", true, false)[0]

func focus_card(ctx: Variant, card: Node3D, distance: float) -> void:
	var normal: Vector3 = card.position.normalized()
	var latitude: float = rad_to_deg(asin(normal.y))
	var longitude: float = wrapf(rad_to_deg(atan2(normal.x, normal.z)) - 180.0, -180.0, 180.0)
	map_control(ctx).call("FocusOn", latitude, longitude, distance)
	await ctx.wait(0.8)

func inspect_reach(ctx: Variant, label: String) -> void:
	var reach: MeshInstance3D = ctx.get_scene_root().find_child("SelectedLongRangeReach", true, false)
	var mesh: Mesh = reach.mesh
	if not reach.visible or mesh.get_surface_count() != 1:
		ctx.error("Expected a selected standby outline with one nonempty surface: " + label)
		return
	if not mesh.has_method("surface_get_arrays"):
		ctx.error("Mesh cannot be inspected for projected outline width")
		return
	var arrays: Array = mesh.surface_get_arrays(0)
	var vertices: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
	var maximum: float = 0.0
	var minimum: float = INF
	for index: int in range(0, vertices.size(), 6):
		var a: Vector2 = camera(ctx).unproject_position(reach.to_global(vertices[index]))
		var b: Vector2 = camera(ctx).unproject_position(reach.to_global(vertices[index + 1]))
		var width: float = a.distance_to(b)
		maximum = maxf(maximum, width)
		minimum = minf(minimum, width)
	if vertices.is_empty() or not is_finite(maximum) or minimum < 0.4 or maximum > 3.1:
		ctx.error("Reach outline escaped its screen-pixel thickness limit: " + label)
	ctx.log(label, {"triangles": int(vertices.size() / 3.0), "min_px": minimum, "max_px": maximum})

func native_edge(ctx: Variant, plate: Sprite3D) -> void:
	var half: Vector2 = plate.texture.get_size() * plate.pixel_size * 0.5
	var point: Vector3 = plate.to_global(Vector3(half.x * 0.75, half.y * 0.2, 0.0))
	await ctx.click_at(camera(ctx).unproject_position(point))
	await ctx.wait(0.3)

func run(ctx: Variant) -> void:
	if camera(ctx).projection == Camera3D.PROJECTION_ORTHOGONAL:
		await ctx.tap_key(KEY_F2, {"physical": false})
		await ctx.wait(0.3)
	await ctx.tap_key(KEY_ESCAPE, {"physical": false})
	var map: Node = map_control(ctx)
	map.call("SelectArmy", -1)
	map.call("SelectSupportFormation", -1)
	var card: Node3D = ctx.get_scene_root().find_child("LongRange_118", true, false)
	if card == null:
		ctx.error("Russia's standby support formation 118 is missing")
		return
	await focus_card(ctx, card, 6.8)
	var plate: Sprite3D = card.get_child(0)
	var before: int = plate.texture.get_instance_id()
	await native_edge(ctx, plate)
	if plate.texture.get_instance_id() == before:
		ctx.error("Native tilted support-card edge click did not select its formation")
	ctx.log("Native support edge click", {"texture_changed": plate.texture.get_instance_id() != before})
	# Close the panel, retaining the actual selected standby formation's outline.
	await ctx.tap_key(KEY_ESCAPE, {"physical": false})
	await ctx.wait(0.3)
	var positions: Dictionary = {}
	for wing: Node3D in ctx.get_scene_root().find_children("LongRange_*", "Node3D", true, false):
		positions[wing.name] = wing.position
	inspect_reach(ctx, "Globe reach operational")
	await focus_card(ctx, card, 5.35)
	inspect_reach(ctx, "Globe reach close")
	for wing: Node3D in ctx.get_scene_root().find_children("LongRange_*", "Node3D", true, false):
		if wing.position.distance_to(positions[wing.name]) > 0.00001:
			ctx.error("Support base slots drifted with the camera")
	await ctx.capture("surface-support-close", 1920)
	await focus_card(ctx, card, 8.2)
	inspect_reach(ctx, "Globe reach overview")
	await ctx.tap_key(KEY_F2, {"physical": false})
	await ctx.wait(0.4)
	inspect_reach(ctx, "Flat reach")
	await ctx.tap_key(KEY_F2, {"physical": false})
	await ctx.wait(0.4)
	map.call("SelectSupportFormation", -1)
	map.call("SelectFleet", -1)
	var fleets: Array[Node] = ctx.get_scene_root().find_children("Fleet_*_RUS", "Node3D", true, false)
	if fleets.is_empty():
		ctx.error("Missing Russia fleet card")
		return
	var fleet: Node3D = fleets[0]
	await focus_card(ctx, fleet, 6.8)
	await native_edge(ctx, fleet.get_child(0))
	var glow: Sprite3D = fleet.find_child("SelectionGlow", true, false)
	if not glow.visible:
		ctx.error("Native tilted fleet-card edge click did not select its fleet")
	ctx.log("Native fleet edge click", {"selected": glow.visible, "name": fleet.name})
	await ctx.capture("surface-fleet-selected", 1920)
	await ctx.click_button(ctx.get_scene_root().find_child("NavSupport", true, false))
	await ctx.wait(0.3)
	if ctx.get_scene_root().find_child("FleetPanel", true, false).is_visible_in_tree() or glow.visible:
		ctx.error("Support screen retained the previous fleet inspector or selection")
	ctx.log("Fleet to support inspector transition checked")
	await ctx.tap_key(KEY_ESCAPE, {"physical": false})
	await native_edge(ctx, fleet.get_child(0))
	# Leave the fleet panel open: SurfaceMilitaryRegressionV2 then verifies replacement
	# by an aggregated army-card click, which uses the box-selection event path.
	ctx.release_all_inputs()
