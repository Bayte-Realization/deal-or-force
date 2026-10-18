extends RefCounted

# Step four: pick China and begin, so the campaign map is up.
#
# The list row is clicked by position because a row's text is a tag and a name in two labels,
# not one string a search can match; the button that starts the game IS one string, so it is
# found by text - and logged either way, since its wording depends on who is selected.

const CHINA_ROW := Vector2(1700, 345)
const START_CANDIDATES := ["BEGIN AS CHN", "BEGIN AS KOR", "BEGIN AS USA", "BEGIN CAMPAIGN"]


func run(ctx: Variant) -> void:
	await ctx.click_at(CHINA_ROW)
	await ctx.wait(2.0)
	await ctx.capture("04-china-selected")
	for text in START_CANDIDATES:
		var path: String = ctx.button_path_by_text(text)
		ctx.log("' " + str(text) + "' -> " + ('"' + path + '"' if path != "" else "<absent>"))

	var start: String = ""
	for text in START_CANDIDATES:
		start = ctx.button_path_by_text(text)
		if start != "":
			ctx.log("starting with '" + str(text) + "'")
			break
	if start == "":
		ctx.log("no start button found")
		return
	await ctx.click_button(start)
	await ctx.wait(6.0)
	await ctx.capture("04-campaign")
	ctx.log("probe4 done, root=" + str(ctx.get_scene_root().name))
