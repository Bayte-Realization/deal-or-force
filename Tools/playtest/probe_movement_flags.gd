extends RefCounted

# Do the movement rows in the country picker actually have a flag?
#
# The user's report: "the flags fell off most of the movements". This counts it instead of
# guessing: every row of the picker that offers a movement (a country with no land) is asked
# whether its icon resolved, and the rows without one are named.


func run(ctx: Variant) -> void:
	var single: String = ctx.button_path_by_text("Singleplayer")
	if single != "":
		await ctx.click_button(single)
		await ctx.wait(3.0)
	var on_map: String = ctx.button_path_by_text("Choose country on map")
	if on_map != "":
		await ctx.click_button(on_map)
		await ctx.wait(4.0)

	var pickerRoot: Node = ctx.get_scene_root().find_child("CountryPicker", true, false)
	if pickerRoot == null:
		ctx.log("no CountryPicker in the scene")
		await ctx.capture("movement-flags", 1280)
		return
	# A row may draw its flag as the button's own icon or as a picture inside it, so both count.
	var rows: int = 0
	var withFlag: int = 0
	var missing: Array = []
	for node in pickerRoot.find_children("*", "Button", true, false):
		if not (node is Button) or str(node.text).is_empty():
			continue
		rows += 1
		var has: bool = node.icon != null
		if not has:
			for picture in node.find_children("*", "TextureRect", true, false):
				if picture is TextureRect and picture.texture != null:
					has = true
					break
		if has:
			withFlag += 1
		elif missing.size() < 14:
			missing.append(str(node.text))
	ctx.log("picker rows=" + str(rows) + " with a flag=" + str(withFlag) +
		" without=" + str(rows - withFlag))
	if missing.size() > 0:
		ctx.log("rows without a flag: " + ", ".join(missing))
	await ctx.capture("movement-flags", 1280)
	ctx.log("done")
