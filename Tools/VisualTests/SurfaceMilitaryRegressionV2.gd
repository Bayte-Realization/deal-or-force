extends RefCounted
# Paused Russia campaign; uses public focus/selection and native map clicks.

func map_control(ctx: Variant) -> Node:
	for node: Node in ctx.get_scene_root().find_children("*", "SubViewportContainer", true, false):
		if node.has_method("FocusOn"): return node
	return null

func inspect_stability(ctx: Variant) -> void:
	var before: Dictionary = {}
	for card: Node3D in ctx.get_scene_root().find_children("DivisionStack_*", "Node3D", true, false):
		before[str(card.name)] = {"position": card.position, "members": str(card.get_meta("display_army_ids", [])), "visible": card.get_child(0).visible}
	await ctx.wait(1.5)
	var count: int = 0
	for card: Node3D in ctx.get_scene_root().find_children("DivisionStack_*", "Node3D", true, false):
		var previous: Dictionary = before[str(card.name)]
		if card.position.distance_to(previous.position) > 0.00001 or previous.members != str(card.get_meta("display_army_ids", [])) or previous.visible != card.get_child(0).visible:
			ctx.error("Paused surface layout is unstable: " + str(card.name))
		count += 1
	ctx.log("Paused surface stability", {"checked": count})

func inspect_auxiliary(ctx: Variant, label: String) -> void:
	var root: Node = ctx.get_scene_root()
	var camera: Camera3D = root.find_children("*", "Camera3D", true, false)[0]
	var flat: bool = camera.projection == Camera3D.PROJECTION_ORTHOGONAL
	var cards: Array[Node] = root.find_children("LongRange_*", "Node3D", true, false)
	cards.append_array(root.find_children("Fleet_*", "Node3D", true, false))
	var checked: int = 0
	for card: Node3D in cards:
		var plate: Sprite3D = card.get_child(0)
		var text: Label3D = card.get_child(1)
		var normal: Vector3 = Vector3.BACK if flat else card.position.normalized()
		if absf(1.0 - normal.dot(card.basis.z.normalized())) > 0.0001:
			ctx.error("Auxiliary counter has wrong surface frame: " + str(card.name))
		for layer: Variant in [plate, text]:
			if layer.fixed_size or layer.billboard != BaseMaterial3D.BILLBOARD_DISABLED:
				ctx.error("Independent auxiliary billboard")
		if text.position != Vector3.ZERO or plate.scale != Vector3.ONE or text.scale != Vector3.ONE:
			ctx.error("Auxiliary selection inflated or detached a layer")
		if not flat and (card.scale.x <= 0.0 or card.scale.x > 0.121):
			ctx.error("Auxiliary footprint outside default limit")
		checked += 1
	if checked < 12: ctx.error("Missing Russia support or fleet cards")
	ctx.log(label, {"checked": checked, "flat": flat})

func inspect_membership(ctx: Variant, label: String, require_groups: bool) -> int:
	var root: Node = ctx.get_scene_root()
	var camera: Camera3D = root.find_children("*", "Camera3D", true, false)[0]
	var view: Rect2 = Rect2(-80, -50, camera.get_viewport().get_visible_rect().size.x + 160, camera.get_viewport().get_visible_rect().size.y + 100)
	var actual: Dictionary = {}
	var displayed: Dictionary = {}
	var grouped: int = 0
	var visible_cards: int = 0
	var owners: Dictionary = {}
	for card: Node3D in root.find_children("DivisionStack_*", "Node3D", true, false):
		for id: int in card.get_meta("actual_army_ids", []): owners[id] = str(card.name).split("_")[1]
	for card: Node3D in root.find_children("DivisionStack_*", "Node3D", true, false):
		if not card.is_visible_in_tree() or camera.is_position_behind(card.global_position): continue
		if not view.has_point(camera.unproject_position(card.global_position)): continue
		for id: int in card.get_meta("actual_army_ids", []): actual[id] = true
		var plate: Sprite3D = card.get_child(0)
		var bars: Sprite3D = card.find_child("OrganisationEquipmentBars", true, false)
		if not plate.visible:
			if card.find_child("DivisionCount", true, false).visible or bars.visible:
				ctx.error("Hidden card left text or bars behind")
			continue
		visible_cards += 1
		if card.get_meta("display_provinces", 1) > 1: grouped += 1
		for id: int in card.get_meta("display_army_ids", []):
			if displayed.has(id): ctx.error("Division occurs in two displayed counters")
			if owners.get(id, "") != str(card.name).split("_")[1]: ctx.error("Different nations merged")
			displayed[id] = true
	if actual.size() != displayed.size(): ctx.error("Aggregation lost or invented divisions")
	for id: int in actual:
		if not displayed.has(id): ctx.error("Aggregation lost division " + str(id))
	if require_groups and grouped == 0: ctx.error("Overview did not aggregate overlapping cards")
	if not require_groups and grouped != 0: ctx.error("Tactical close view retained overview groups")
	ctx.log(label, {"divisions": actual.size(), "visible_cards": visible_cards, "groups": grouped})
	return grouped

func run(ctx: Variant) -> void:
	var initial_camera: Camera3D = ctx.get_scene_root().find_children("*", "Camera3D", true, false)[0]
	if initial_camera.projection == Camera3D.PROJECTION_ORTHOGONAL:
		await ctx.tap_key(KEY_F2, {"physical": false})
		await ctx.wait(0.5)
	var support: Node = ctx.get_scene_root().find_child("Support", true, false)
	if support != null and support.is_visible_in_tree():
		for button: Button in support.find_children("*", "Button", true, false):
			if button.text == "×" and button.is_visible_in_tree():
				await ctx.click_button(button)
				break
	var map: Node = map_control(ctx)
	map.call("SelectArmy", -1)
	map.call("FocusOn", 50.0, 36.0, 8.2)
	await ctx.wait(1.3)
	inspect_auxiliary(ctx, "Surface auxiliaries overview")
	inspect_membership(ctx, "Regional view keeps province cards separate", false)
	await inspect_stability(ctx)
	await ctx.capture("surface-military-overview", 1920)
	# A click on a grouped host must select every displayed participant.
	var root: Node = ctx.get_scene_root()
	var camera: Camera3D = root.find_children("*", "Camera3D", true, false)[0]
	var clicked_ids: Array = []
	for card: Node3D in root.find_children("DivisionStack_RUS_*", "Node3D", true, false):
		if not card.is_visible_in_tree() or not card.get_child(0).visible or card.get_meta("display_army_ids", []).size() < 2: continue
		var center: Vector2 = camera.unproject_position(card.global_position)
		if not Rect2(800, 220, 700, 500).has_point(center): continue
		clicked_ids.assign(card.get_meta("display_army_ids", []))
		await ctx.click_at(center)
		break
	await ctx.wait(0.4)
	var fleet_panel: Node = ctx.get_scene_root().find_child("FleetPanel", true, false)
	if fleet_panel != null and fleet_panel.is_visible_in_tree():
		ctx.error("Army group click left the fleet inspector over the army panel")
	var selected_ids: Dictionary = {}
	for card: Node3D in ctx.get_scene_root().find_children("DivisionStack_RUS_*", "Node3D", true, false):
		if not card.find_child("SelectionGlow", true, false).visible: continue
		for id: int in card.get_meta("actual_army_ids", []): selected_ids[id] = true
	if clicked_ids.is_empty(): ctx.error("No native group click target")
	for id: int in clicked_ids:
		if not selected_ids.has(id): ctx.error("Native group click missed a participant")
	ctx.log("Native group click", {"participants": clicked_ids.size(), "selected": selected_ids.size()})
	await ctx.capture("surface-military-group-selected", 1920)
	map_control(ctx).call("SelectArmy", -1)
	map_control(ctx).call("FocusOn", 50.0, 36.0, 5.45)
	await ctx.wait(1.2)
	inspect_membership(ctx, "Tactical membership", false)
	var individual_count: int = 0
	for card: Node3D in ctx.get_scene_root().find_children("DivisionStack_*", "Node3D", true, false):
		if not card.is_visible_in_tree() or camera.is_position_behind(card.global_position): continue
		if not Rect2(600, 90, 950, 850).has_point(camera.unproject_position(card.global_position)): continue
		if card.get_meta("actual_army_ids", []).size() != 1:
			ctx.error("Close surface view still bundles divisions into province stacks")
		individual_count += 1
	if individual_count == 0: ctx.error("No individual tactical surface cards")
	ctx.log("Tactical individual cards", {"checked": individual_count})
	await ctx.capture("surface-military-tactical", 1920)
	await ctx.tap_key(KEY_F2, {"physical": false})
	await ctx.wait(0.5)
	if ctx.get_scene_root().find_children("*", "Camera3D", true, false)[0].projection != Camera3D.PROJECTION_ORTHOGONAL:
		ctx.error("Flat projection not activated")
	inspect_auxiliary(ctx, "Flat auxiliaries")
	await ctx.tap_key(KEY_F2, {"physical": false})
	await ctx.wait(0.5)
	inspect_auxiliary(ctx, "Restored surface auxiliaries")
	ctx.release_all_inputs()
