extends RefCounted

# Same walk, with two corrections learned from the last two runs:
#
# 1. A country is selected by clicking it ON THE MAP, not by clicking its row in the list - the
#    list is a shortcut, the map is the mechanism. So the probe clicks the map and then asks
#    which "BEGIN AS ..." button appeared, rather than assuming which country it landed on.
# 2. The picture is taken at 700 pixels wide, because 900 was still too large to read back.

const CHINA_POINTS := [Vector2(600, 450), Vector2(700, 500), Vector2(520, 380), Vector2(820, 430)]
const MONGOLIA := Vector2(750, 180)


func _start_button(ctx: Variant) -> String:
	var buttons: Array = ctx.get_scene_root().find_children("*", "Button", true, false)
	for button in buttons:
		if button is Button and "BEGIN AS" in button.text:
			return button.text
	return ""


func run(ctx: Variant) -> void:
	var selected: String = ""
	for point in CHINA_POINTS:
		await ctx.click_at(point)
		await ctx.wait(1.6)
		selected = _start_button(ctx)
		ctx.log("clicked " + str(point) + " -> start button '" + selected + "'")
		if selected == "BEGIN AS CHN":
			break
	if selected != "BEGIN AS CHN":
		await ctx.capture("shot3-wrong-country", 700)
		return

	await ctx.click_button(ctx.button_path_by_text(selected))
	await ctx.wait(7.0)
	await ctx.click_at(MONGOLIA)
	await ctx.wait(3.0)
	await ctx.capture("shot3-card", 700)
	ctx.log("photographed")
