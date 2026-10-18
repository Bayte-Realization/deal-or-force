extends RefCounted

# Step five: click Mongolia and photograph the country card.
#
# The card is the thing under review, so what matters is that the right panel is up before the
# picture is taken; anything else on screen is context. The position is read off the previous
# capture rather than guessed, and both the panel and the whole screen are captured, because a
# layout fault is sometimes only visible against the map behind it.

const MONGOLIA := Vector2(750, 180)


func run(ctx: Variant) -> void:
	await ctx.click_at(MONGOLIA)
	await ctx.wait(2.5)
	await ctx.capture("05-country-card")
	var root: Node = ctx.get_scene_root()
	for name in ["CountryCard", "DossierScroll", "CountryFactsScroll", "CountryActionsScroll", "GameHud"]:
		var found: Node = ctx.node("/root/Main/" + name)
		if found == null:
			var byPath: Node = ctx.node("GameHud/" + name)
			found = byPath
		ctx.log("node '" + str(name) + "' -> " + ("present" if found != null else "<absent>"))
	ctx.log("probe5 done")
