extends RefCounted

# Looks at the game the way a player does, and reports what it found.
#
# The point of this probe is the picture: a panel laid out in code cannot be reviewed by
# reading the code that lays it out, and every guess about a Control's height is a guess about
# a layout pass nobody has run. So this walks the game to the screen in question, photographs
# it, and says which buttons it could see on the way - so the next version of this file does
# not have to guess at a label either.

const CANDIDATES := [
	"Singleplayer", "New campaign", "New Campaign", "Play", "Continue", "Load",
	"BEGIN CAMPAIGN", "Begin campaign", "Handbook", "Menu",
]


func run(ctx: Variant) -> void:
	var root: Node = ctx.get_scene_root()
	ctx.log("root=" + str(root.name if root != null else "<none>"))
	await ctx.wait(1.5)
	await ctx.capture("01-where-we-are")
	for text in CANDIDATES:
		var path: String = ctx.button_path_by_text(text)
		ctx.log("button '" + str(text) + "' -> " + ('"' + path + '"' if path != "" else "<absent>"))
	ctx.log("probe done")
