extends RefCounted

# Does Escape answer now, and does the navigation switch again afterwards?
#
# Before the fix a workspace was a full-screen layer with no Escape handler: the first nav
# click opened it and every click after that died on the layer, so four different nav buttons
# produced four identical photographs of the budget. This probe replays exactly that sequence
# and reports, at each step, whether a workspace title is on screen and which one.


func _open_title(ctx: Variant) -> String:
	var root: Node = ctx.get_scene_root()
	if root == null:
		return ""
	for node in root.find_children("*", "Label", true, false):
		var label := node as Label
		if label == null or not label.is_visible_in_tree():
			continue
		var text := label.text.strip_edges()
		if text.begins_with("STATE AFFAIRS") or text.begins_with("LAWS") or text.begins_with("DIPLOMACY") \
			or text.begins_with("CONFLICTS") or text.begins_with("WARS") or text.begins_with("LEDGER"):
			return text
	return ""


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
	await ctx.click_button(begin)
	await ctx.wait(16.0)

	ctx.log("step: map", {"workspace": _open_title(ctx)})

	var budget: String = ctx.button_path_by_text("Budget")
	if budget.is_empty():
		ctx.log("no budget nav button")
		return
	await ctx.click_button(budget)
	await ctx.wait(2.5)
	ctx.log("step: after Budget click", {"workspace": _open_title(ctx)})
	await ctx.capture("esc-1-budget-open", 1000)

	await ctx.tap_key(KEY_ESCAPE)
	await ctx.wait(1.5)
	ctx.log("step: after Escape", {"workspace": _open_title(ctx)})
	await ctx.capture("esc-2-after-escape", 1000)

	var laws: String = ctx.button_path_by_text("Laws")
	if laws.is_empty():
		ctx.log("no laws nav button")
		return
	await ctx.click_button(laws)
	await ctx.wait(2.5)
	ctx.log("step: after Laws click", {"workspace": _open_title(ctx)})
	await ctx.capture("esc-3-laws-open", 1000)

	await ctx.tap_key(KEY_ESCAPE)
	await ctx.wait(1.5)
	var diplomacy: String = ctx.button_path_by_text("Diplomacy")
	if diplomacy.is_empty():
		ctx.log("no diplomacy nav button")
		return
	await ctx.click_button(diplomacy)
	await ctx.wait(2.5)
	ctx.log("step: after Diplomacy click", {"workspace": _open_title(ctx)})
	await ctx.capture("esc-4-diplomacy-open", 1000)
	ctx.log("done")
