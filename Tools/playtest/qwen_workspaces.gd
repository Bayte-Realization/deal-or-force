extends RefCounted

# One session, two jobs.
#
# 1. Open the workspaces the first play-through never touched - Budget, Laws, Diplomacy,
#    Conflicts - and photograph each, because "the interface works" has to mean the screens a
#    player actually lives in, not the map behind them.
# 2. Find the orphan "Resources" caption that sits clipped above the top bar on every campaign
#    frame. A walk over visible Controls found nothing, so this pass reads EVERY string
#    property of EVERY node in the tree, Label3D and hidden included, and reports the owner
#    with its rect and parent chain. A fact, not a guess.


func _scan_all(ctx: Variant, node: Node, depth: int) -> void:
	if node == null:
		return
	for property in node.get_property_list():
		if not (property is Dictionary):
			continue
		if str(property.get("type", "")) != str(TYPE_STRING):
			continue
		var value: Variant = node.get(property.get("name", ""))
		if value == null:
			continue
		var text := str(value)
		if not ("resource" in text.to_lower()):
			continue
		var rect := ""
		if node is Control:
			rect = str((node as Control).get_global_rect())
		elif node is Node3D:
			rect = "3D at " + str((node as Node3D).global_transform.origin)
		var chain := ""
		var cursor: Node = node
		for i in range(5):
			if cursor == null:
				break
			chain = (str(cursor.name) + " < " + chain) if chain != "" else str(cursor.name)
			cursor = cursor.get_parent()
		ctx.log("resource string owner", {
			"node": str(node.name),
			"type": str(node.get_class()),
			"property": str(property.get("name", "")),
			"text": text,
			"visible": bool(node.is_visible_in_tree()),
			"rect": rect,
			"chain": chain,
		})
	for child in node.get_children():
		_scan_all(ctx, child, depth + 1)


func _open_and_shoot(ctx: Variant, label: String, shot: String) -> void:
	var path: String = ctx.button_path_by_text(label)
	if path.is_empty():
		ctx.log("workspace button missing", {"wanted": label})
		return
	await ctx.click_button(path)
	await ctx.wait(2.5)
	var root: Node = ctx.get_scene_root()
	var buttons: Array = root.find_children("*", "Button", true, false)
	var visible_count := 0
	for node in buttons:
		var button := node as Button
		if button != null and button.is_visible_in_tree() and button.text.strip_edges() != "":
			visible_count += 1
	ctx.log("workspace open", {"screen": label, "visible_buttons": visible_count})
	await ctx.capture(shot, 1100)


func run(ctx: Variant) -> void:
	ctx.log("probe entered")
	await ctx.wait(3.0)

	var single: String = ctx.button_path_by_text("Singleplayer")
	if single.is_empty():
		ctx.log("no singleplayer button")
		return
	await ctx.click_button(single)
	await ctx.wait(3.5)
	var pick: String = ctx.button_path_by_text("Choose country on map")
	if pick.is_empty():
		ctx.log("no picker button")
		return
	await ctx.click_button(pick)
	await ctx.wait(12.0)

	var begin: Button = null
	for node in ctx.get_scene_root().find_children("*", "Button", true, false):
		var button := node as Button
		if button != null and button.is_visible_in_tree() and button.text.begins_with("BEGIN AS"):
			begin = button
			break
	if begin == null:
		ctx.log("no begin button")
		return
	ctx.log("beginning as", {"text": begin.text})
	await ctx.click_button(begin)
	await ctx.wait(16.0)

	for entry in [["Budget", "ws-budget"], ["Laws", "ws-laws"], ["Diplomacy", "ws-diplomacy"], ["Conflicts", "ws-conflicts"]]:
		await _open_and_shoot(ctx, str(entry[0]), str(entry[1]))
		# Back to the map between screens, the way a player leaves with Escape.
		await ctx.tap_key(KEY_ESCAPE)
		await ctx.wait(1.2)

	ctx.log("exhaustive scan for the resources caption")
	_scan_all(ctx, ctx.get_scene_root(), 0)
	await ctx.capture("ws-final", 1100)
	ctx.log("done")
