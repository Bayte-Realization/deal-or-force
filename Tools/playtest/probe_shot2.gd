extends RefCounted

# Same walk as probe_shot.gd, but with the two positions scaled to whatever viewport the window
# actually got. The first version hard-coded 1920-wide coordinates and worked in one run and not
# the next, because the window is sized by the display and not by this file.


func _press(ctx: Variant, wanted: String, scale: float, reference: Vector2) -> void:
	await ctx.click_at(reference * scale)


func run(ctx: Variant) -> void:
	var hud: Node = ctx.get_scene_root()
	var viewport: Vector2 = hud.get_viewport().get_visible_rect().size
	var scale: float = viewport.x / 1920.0
	ctx.log("viewport=" + str(viewport) + " scale=" + str(scale))

	# The picker may already be up; if the menu is, press the way in first.
	var single: String = ctx.button_path_by_text("Singleplayer")
	if single != "":
		ctx.log("at the main menu")
		await ctx.click_button(single)
		await ctx.wait(3.0)

	await _press(ctx, "china row", scale, Vector2(1700, 345))
	await ctx.wait(2.5)

	var begin: String = ctx.button_path_by_text("BEGIN AS CHN")
	if begin == "":
		var buttons: Array = hud.find_children("*", "Button", true, false)
		for button in buttons:
			if button is Button and "BEGIN AS" in button.text:
				ctx.log("start button reads: " + button.text)
		ctx.log("china was not selected")
		await ctx.capture("shot2-picker", 900)
		return

	await ctx.click_button(begin)
	await ctx.wait(7.0)
	await _press(ctx, "mongolia", scale, Vector2(750, 180))
	await ctx.wait(3.0)
	await ctx.capture("shot2-card", 900)
	ctx.log("photographed")
