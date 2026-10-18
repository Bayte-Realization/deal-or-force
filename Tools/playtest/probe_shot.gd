extends RefCounted

# The whole walk, in one script: main menu -> country picker -> China -> campaign -> Mongolia.
#
# This file exists so that a layout change can be looked at rather than argued about. Every
# position in it was read off a previous capture, and the two button presses are by text: a
# position is a guess about a layout pass, a label is not. Rewrite the last capture's label to
# photograph a different panel.


func run(ctx: Variant) -> void:
	var single: String = ctx.button_path_by_text("Singleplayer")
	if single == "":
		ctx.log("no Singleplayer button")
		await ctx.capture("shot-menu")
		return
	await ctx.click_button(single)
	await ctx.wait(3.0)

	# The country list's rows are a tag and a name in two labels, so the row is a position.
	await ctx.click_at(Vector2(1700, 345))
	await ctx.wait(2.0)
	var begin: String = ctx.button_path_by_text("BEGIN AS CHN")
	if begin == "":
		ctx.log("no BEGIN AS CHN button")
		await ctx.capture("shot-picker")
		return
	await ctx.click_button(begin)
	await ctx.wait(7.0)

	await ctx.click_at(Vector2(750, 180))
	await ctx.wait(2.5)
	await ctx.capture("shot-card", 900)
	ctx.log("photographed")
