extends RefCounted

# Name the layer that actually hosts the budget workspace.
#
# Escape did not close it and the navigation did not switch, so the assumption that the
# budget lives in _stateAffairsLayer is worth checking against the live tree instead of
# against the source. The parent chain of the visible title names the owner outright.


func run(ctx: Variant) -> void:
	ctx.log("probe entered")
	await ctx.wait(3.0)
	var single: String = ctx.button_path_by_text("Singleplayer")
	if single.is_empty():
		return
	await ctx.click_button(single)
	await ctx.wait(3.5)
	var pick: String = ctx.button_path_by_text("Choose country on map")
	if pick.is_empty():
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
		return
	await ctx.click_button(begin)
	await ctx.wait(16.0)

	var budget: String = ctx.button_path_by_text("Budget")
	if budget.is_empty():
		ctx.log("no budget nav button")
		return
	await ctx.click_button(budget)
	await ctx.wait(2.5)

	var root: Node = ctx.get_scene_root()
	for node in root.find_children("*", "Label", true, false):
		var label := node as Label
		if label == null or not label.is_visible_in_tree():
			continue
		if not label.text.strip_edges().begins_with("STATE AFFAIRS"):
			continue
		var chain := ""
		var cursor: Node = label
		for i in range(8):
			if cursor == null:
				break
			chain = (str(cursor.name) + " < " + chain) if chain != "" else str(cursor.name)
			cursor = cursor.get_parent()
		ctx.log("budget title owner chain", {"chain": chain})
		break

	# And which of the known workspace layers say they are visible right now.
	for wanted in ["StateAffairs", "Ledger", "Wars", "Diplomacy", "Laws", "Trade", "Production",
			"Support", "Research", "Conflicts", "Situation", "SidePanel"]:
		var found: Node = root.find_child(wanted, true, false)
		if found == null:
			ctx.log("layer absent", {"name": wanted})
		else:
			ctx.log("layer state", {"name": wanted, "visible": bool(found.visible),
				"in_tree": bool(found.is_visible_in_tree())})
	ctx.log("done")
