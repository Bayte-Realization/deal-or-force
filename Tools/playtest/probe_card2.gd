extends RefCounted

# Step two: get into a campaign. The main menu's way in is "Singleplayer", which opens the
# country picker; the picker's own way in is "BEGIN CAMPAIGN". Both are pressed by text rather
# than by position, because a position is a guess about a layout pass that has not happened yet.


func run(ctx: Variant) -> void:
	var path: String = ctx.button_path_by_text("Singleplayer")
	ctx.log("Singleplayer -> " + ('"' + path + '"' if path != "" else "<absent>"))
	if path == "":
		await ctx.capture("02-no-way-in")
		return
	await ctx.click_button(path)
	await ctx.wait(3.0)
	await ctx.capture("02-country-picker")
	for text in ["BEGIN CAMPAIGN", "Begin campaign", "China", "CHN", "Singleplayer"]:
		var found: String = ctx.button_path_by_text(text)
		ctx.log("button '" + str(text) + "' -> " + ('"' + found + '"' if found != "" else "<absent>"))
	ctx.log("probe2 done")
