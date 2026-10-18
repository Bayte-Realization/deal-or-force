extends RefCounted

# The whole walk, then the numbers.
#
# A picture of a panel answers "does it look right" slowly and unreliably - a busy map
# compresses badly and the frame comes back too large to read. The rects answer it exactly: the
# height the facts scroll was given against the height its content needed is the size of the gap
# above the action list, in pixels, with nothing to interpret.


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
	var single: String = ctx.button_path_by_text("Singleplayer")
	if single != "":
		await ctx.click_button(single)
		await ctx.wait(3.0)

	await ctx.click_at(Vector2(1700, 345))
	await ctx.wait(2.5)
	var begin: String = ctx.button_path_by_text("BEGIN AS CHN")
	if begin == "":
		ctx.log("china was not selected")
		return
	await ctx.click_button(begin)
	await ctx.wait(8.0)

	await ctx.click_at(Vector2(750, 180))
	await ctx.wait(3.0)

	var hud: Node = ctx.get_scene_root()
	ctx.log("scene=" + str(hud.name))
	for wanted in ["CountryCard", "CountryFactsScroll", "CountryActionsScroll", "DiplomaticActionList"]:
		_report(ctx, hud, str(wanted))
	await ctx.capture("final-card", 520)
	ctx.log("done")
