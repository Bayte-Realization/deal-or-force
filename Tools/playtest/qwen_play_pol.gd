extends RefCounted

# A real session, not a screenshot of a menu.
#
# Poland is the project's first country: Singleplayer -> choose on the map -> POL. The campaign
# opens paused, so nothing below happens until Space is pressed. Every stage dumps the buttons a
# player can actually see and read, because a panel that is named differently than expected is
# better discovered than guessed at.


func _visible_buttons(root: Node) -> Array:
	var found: Array = []
	_walk(root, found)
	return found


func _walk(node: Node, out: Array) -> void:
	# Only what is on screen. The HUD keeps closed windows in the tree, and an unfiltered walk
	# reports hundreds of buttons on a screen showing nine.
	if not node.is_visible_in_tree():
		return
	if node is Button:
		var text: String = str((node as Button).text).strip_edges()
		if text.length() > 0 and text.length() < 60:
			out.append({"text": text, "path": str(node.get_path()), "disabled": bool((node as Button).disabled)})
	for child in node.get_children():
		_walk(child, out)


func _dump(ctx: Variant, stage: String, limit: int = 60) -> Array:
	var root: Node = ctx.get_scene_root()
	if root == null:
		ctx.log(stage + ": no scene root")
		return []
	var buttons: Array = _visible_buttons(root)
	ctx.log(stage + " buttons", {"count": buttons.size()})
	var shown := 0
	for b in buttons:
		if shown >= limit:
			ctx.log(stage + " truncated", {"remaining": buttons.size() - limit})
			break
		shown += 1
		ctx.log(stage + " button", b)
	return buttons


# Country rows read "POL  Poland", and plenty of other controls contain the word Poland in a
# paragraph. Shortest visible match wins - a containment test alone once pressed "Technocracy".
func _pick_country(ctx: Variant, needles: Array) -> Button:
	var root: Node = ctx.get_scene_root()
	if root == null:
		return null
	var all: Array = root.find_children("*", "Button", true, false)
	var best: Button = null
	for needle in needles:
		for node in all:
			var button := node as Button
			if button == null or not button.is_visible_in_tree():
				continue
			if not str(needle).to_lower() in button.text.to_lower():
				continue
			if best == null or button.text.length() < best.text.length():
				best = button
	return best


func _click_text(ctx: Variant, text: String, stage: String) -> bool:
	var path: String = ctx.button_path_by_text(text)
	if path.is_empty():
		ctx.log(stage + ": button not found", {"wanted": text})
		return false
	await ctx.click_button(path)
	ctx.log(stage + ": pressed", {"text": text})
	return true


func run(ctx: Variant) -> void:
	# Log before the first await, so "the script never ran" and "the script is waiting"
	# are distinguishable in the log instead of both looking like silence.
	ctx.log("probe entered")
	await ctx.wait(3.0)
	ctx.log("initial wait finished")
	var root: Node = ctx.get_scene_root()
	if root == null:
		ctx.log("no scene root at all")
		return
	var viewport: Vector2 = root.get_viewport().get_visible_rect().size
	ctx.log("start", {"scene": str(root.name), "viewport": str(viewport)})
	_dump(ctx, "main-menu")
	await ctx.capture("01-main-menu", 1280)

	if not await _click_text(ctx, "Singleplayer", "singleplayer"):
		return
	await ctx.wait(3.5)
	_dump(ctx, "scenario-library")
	await ctx.capture("02-scenario-library", 1280)

	if not await _click_text(ctx, "Choose country on map", "picker"):
		return
	await ctx.wait(12.0)
	_dump(ctx, "country-picker", 40)
	await ctx.capture("03-country-picker", 1280)

	var poland: Button = _pick_country(ctx, ["Poland", "POL"])
	if poland != null:
		ctx.log("selecting", {"text": poland.text})
		await ctx.click_button(poland)
		await ctx.wait(3.0)
		await ctx.capture("04-poland-selected", 1280)
	else:
		# Poland is not in this picker: the panel lists four large countries and then a wall of
		# movements, and the rest of the world is chosen by clicking the globe. Play whatever is
		# already selected rather than failing the whole session over one tag.
		ctx.log("poland is not listed in the picker - playing the preselected country")

	# The begin button names the tag it will start as, so read the tag from it instead of
	# hardcoding one that may not be selected.
	var begin_button: Button = _pick_country(ctx, ["BEGIN AS"])
	if begin_button == null:
		ctx.log("no begin button - nothing was selected")
		return
	ctx.log("beginning as", {"text": begin_button.text})
	await ctx.click_button(begin_button)
	ctx.log("campaign starting")
	await ctx.wait(16.0)

	_dump(ctx, "campaign-paused", 80)
	await ctx.capture("05-campaign-paused", 1280)

	# The campaign opens paused; Space is the documented resume key.
	await ctx.tap_key(KEY_SPACE)
	ctx.log("unpaused with Space")
	await ctx.wait(25.0)
	_dump(ctx, "campaign-running", 80)
	await ctx.capture("06-campaign-running", 1280)
	await ctx.wait(40.0)
	await ctx.capture("07-campaign-later", 1280)
	ctx.log("done")
