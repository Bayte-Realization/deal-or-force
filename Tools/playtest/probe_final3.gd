extends RefCounted

# The walk again, with the country found by NAME and by shortness.
#
# The last attempt matched "CHN" as a substring and pressed "Technocracy", which is what a
# case-insensitive containment test does to an abbreviation. This one takes buttons naming
# China and keeps the shortest, because a country row is "CHN  China" and everything else with
# the word in it is a paragraph.


func _candidates(hud: Node, needle: String) -> Array:
	var found: Array = []
	var buttons: Array = hud.find_children("*", "Button", true, false)
	for button in buttons:
		if button is Button and needle.to_lower() in button.text.to_lower():
			found.append(button)
	return found


func _report(ctx: Variant, hud: Node, wanted: String) -> void:
	var node: Node = hud.find_child(wanted, true, false)
	if node == null:
		ctx.log(str(wanted) + " absent")
		return
	var control: Control = node as Control
	if control == null:
		ctx.log(str(wanted) + " not a control")
		return
	ctx.log(str(wanted) + " y=" + str(int(control.global_position.y)) + " h=" + str(int(control.size.y)))


func run(ctx: Variant) -> void:
	var hud: Node = ctx.get_scene_root()
	var viewport: Vector2 = hud.get_viewport().get_visible_rect().size
	ctx.log("viewport=" + str(viewport))

	var single: String = ctx.button_path_by_text("Singleplayer")
	if single != "":
		await ctx.click_button(single)
		await ctx.wait(3.0)
		hud = ctx.get_scene_root()

	var found: Array = _candidates(hud, "China")
	for button in found:
		ctx.log("candidate: '" + str(button.text) + "'")
	if found.is_empty():
		ctx.log("no china button")
		return
	var shortest: Button = found[0]
	for button in found:
		if str(button.text).length() < str(shortest.text).length():
			shortest = button
	ctx.log("pressing '" + str(shortest.text) + "'")
	await ctx.click_button(shortest)
	await ctx.wait(2.5)

	var begin: String = ctx.button_path_by_text("BEGIN AS CHN")
	if begin == "":
		ctx.log("china was not selected")
		await ctx.capture("f3-picker", 520)
		return
	await ctx.click_button(begin)
	await ctx.wait(8.0)

	await ctx.click_at(Vector2(750, 180) * (viewport.x / 1920.0))
	await ctx.wait(3.0)

	hud = ctx.get_scene_root()
	for wanted in ["CountryCard", "CountryFactsScroll", "CountryActionsScroll", "DiplomaticActionList"]:
		_report(ctx, hud, str(wanted))
	await ctx.capture("f3-card", 520)
	ctx.log("done")
