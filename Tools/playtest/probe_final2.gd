extends RefCounted

# The walk, with the country found by its label rather than by a position.
#
# The picker's list is drawn from the display's size, and the window was 1982 pixels wide in one
# run and 1920 in the next, so a hard-coded row position lands on a different country each time.
# The row's label does not move, so this searches the buttons for one naming China and presses
# that. Only the map click is still a position, and that one is scaled to the viewport.


func _button_naming(hud: Node, needle: String) -> Button:
	var buttons: Array = hud.find_children("*", "Button", true, false)
	for button in buttons:
		if button is Button and needle.to_lower() in button.text.to_lower():
			return button
	return null


func _report(ctx: Variant, hud: Node, wanted: String) -> void:
	var found: Node = hud.find_child(wanted, true, false)
	if found == null:
		ctx.log(str(wanted) + " absent")
		return
	var control: Control = found as Control
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

	var china: Button = _button_naming(hud, "CHN")
	if china == null:
		china = _button_naming(hud, "China")
	if china == null:
		var buttons: Array = hud.find_children("*", "Button", true, false)
		for button in buttons:
			ctx.log("button: " + str(button.text))
		ctx.log("no china button")
		return
	ctx.log("pressing '" + china.text + "'")
	await ctx.click_button(china)
	await ctx.wait(2.5)

	var begin: String = ctx.button_path_by_text("BEGIN AS CHN")
	if begin == "":
		ctx.log("china was not selected")
		await ctx.capture("f2-picker", 520)
		return
	await ctx.click_button(begin)
	await ctx.wait(8.0)

	var scale: float = viewport.x / 1920.0
	await ctx.click_at(Vector2(750, 180) * scale)
	await ctx.wait(3.0)

	hud = ctx.get_scene_root()
	for wanted in ["CountryCard", "CountryFactsScroll", "CountryActionsScroll", "DiplomaticActionList"]:
		_report(ctx, hud, str(wanted))
	await ctx.capture("f2-card", 520)
	ctx.log("done")
