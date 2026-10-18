extends RefCounted

# Who draws the orphan "Resources" caption above the top bar?
#
# Two campaign frames show a clipped label reading "Resources" centred above the top bar, and
# no UI string of that exact shape exists in Scripts/. A downscaled screenshot cannot say
# which node it is, so the probe asks the live tree instead: every visible control whose text
# contains the word, with its rect and its parent chain, so the owner is a fact and not a guess.


func _walk(ctx: Variant, node: Node, depth: int) -> void:
	if node == null:
		return
	if node.is_visible_in_tree():
		var text := ""
		if node is Label:
			text = str((node as Label).text)
		elif node is Button:
			text = str((node as Button).text)
		elif node is LineEdit:
			text = str((node as LineEdit).text)
		if "resource" in text.to_lower():
			var control := node as Control
			var rect := Rect2()
			if control != null:
				rect = control.get_global_rect()
			var chain := ""
			var cursor: Node = node
			for i in range(4):
				if cursor == null:
					break
				chain = str(cursor.name) + (" < " if chain == "" else " < ") + chain
				cursor = cursor.get_parent()
			ctx.log("resource text found", {
				"text": text,
				"type": str(node.get_class()),
				"rect": str(rect),
				"chain": chain,
				"depth": depth,
			})
	for child in node.get_children():
		_walk(ctx, child, depth + 1)


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
	var all: Array = ctx.get_scene_root().find_children("*", "Button", true, false)
	for node in all:
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

	ctx.log("campaign up - scanning for resource captions")
	_walk(ctx, ctx.get_scene_root(), 0)
	await ctx.capture("resources-scan", 1280)
	ctx.log("done")
