extends RefCounted

# Step three: into the campaign, so the country card can be photographed.
#
# "Choose country on map" is the way in from the scenarios screen; the picker then has its own
# "BEGIN CAMPAIGN". Both by text, and every step logs what it found, because a probe that only
# fails tells nobody anything.

const STEPS := [
	"Choose country on map", "Choose country", "BEGIN CAMPAIGN", "Begin campaign",
]


func run(ctx: Variant) -> void:
	for text in STEPS:
		var path: String = ctx.button_path_by_text(text)
		ctx.log("button '" + str(text) + "' -> " + ('"' + path + '"' if path != "" else "<absent>"))
	await ctx.capture("03-before-choose")

	var choose: String = ctx.button_path_by_text("Choose country on map")
	if choose == "":
		ctx.log("no way in from here")
		return
	await ctx.click_button(choose)
	await ctx.wait(3.5)
	await ctx.capture("03-country-picker")

	for text in ["BEGIN CAMPAIGN", "Begin campaign", "China", "CHN"]:
		var found: String = ctx.button_path_by_text(text)
		ctx.log("after choose, '" + str(text) + "' -> " + ('"' + found + '"' if found != "" else "<absent>"))
	ctx.log("probe3 done")
