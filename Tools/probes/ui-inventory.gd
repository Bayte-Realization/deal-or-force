extends RefCounted

# What is on screen, so a screen can be driven without guessing at node names.
#
# Every button with its text, its path and whether it is disabled, then the labels that
# surround them. Logged to the game log as FENNARA_SCRIPT_LOG lines, which is where this is
# read from - no picture needed.

func run(ctx: Variant) -> void:
	await ctx.wait(2.5)
	var root: Node = ctx.get_scene_root()
	ctx.log("scene", {"root": str(root.name), "path": str(root.scene_file_path)})
	_dump(ctx, root, "screen")

	# Into the scenario library.
	var single: String = ctx.button_path_by_text("Singleplayer")
	ctx.log("singleplayer path", {"path": single})
	if single.is_empty():
		return
	await ctx.click_button(single)
	await ctx.wait(3.5)
	_dump(ctx, ctx.get_scene_root(), "after-singleplayer")

	# Into the country picker on the map.
	var pick: String = ctx.button_path_by_text("Choose country on map")
	ctx.log("picker path", {"path": pick})
	if pick.is_empty():
		return
	await ctx.click_button(pick)
	await ctx.wait(12.0)
	_dump(ctx, ctx.get_scene_root(), "country-picker")
	var shot: Dictionary = await ctx.capture("country-picker", 1920)
	ctx.log("captured", shot)

func _dump(ctx: Variant, root: Node, stage: String) -> void:
	var buttons: Array = []
	var labels: Array = []
	_walk(root, buttons, labels)
	ctx.log(stage + " button count", {"count": buttons.size()})
	var shown := 0
	for b in buttons:
		if shown >= 45:
			break
		shown += 1
		ctx.log(stage + " button", b)
	ctx.log(stage + " label count", {"count": labels.size()})
	shown = 0
	for l in labels:
		if shown >= 30:
			break
		shown += 1
		ctx.log(stage + " label", {"text": l})

func _walk(node: Node, buttons: Array, labels: Array) -> void:
	# Only what a player can actually see and read. The main menu keeps its settings and mods
	# windows in the tree whether or not they are up, so an unfiltered walk reported nineteen
	# buttons on a screen with nine, and the campaign reported hundreds.
	if not node.is_visible_in_tree():
		return
	if node is Button:
		var text: String = str((node as Button).text).strip_edges()
		if text.length() > 0 and text.length() < 60:
			buttons.append({"text": text, "path": str(node.get_path()),
				"disabled": bool((node as Button).disabled)})
	elif node is Label:
		var label_text: String = str((node as Label).text).strip_edges()
		if label_text.length() > 0 and label_text.length() < 80:
			labels.append(label_text)
	for child in node.get_children():
		_walk(child, buttons, labels)
