extends RefCounted

# Look at the map, which is the only place the colours are visible.
#
# The 33 signature colours were a table in the scenario builder and are a file in history now. The
# guard test proves the values arrived intact; this proves they are the values the renderer is
# given, by photographing the map that draws them.


func run(ctx: Variant) -> void:
	var hud: Node = ctx.get_scene_root()
	var viewport: Vector2 = hud.get_viewport().get_visible_rect().size
	var scale: float = viewport.x / 1920.0
	ctx.log("viewport=" + str(viewport))

	await ctx.click_button(ctx.button_path_by_text("Singleplayer"))
	await ctx.wait(3.0)
	await ctx.click_button(ctx.button_path_by_text("Choose country on map"))
	await ctx.wait(3.0)
	var begin: String = ""
	for point in [Vector2(520, 380), Vector2(600, 450), Vector2(660, 420), Vector2(700, 500)]:
		await ctx.click_at(point * scale)
		await ctx.wait(1.8)
		begin = ctx.button_path_by_text("BEGIN AS CHN")
		if begin != "":
			break
	if begin == "":
		ctx.log("could not pick a country")
		return
	await ctx.click_button(begin)
	await ctx.wait(10.0)

	# China, Japan, the Koreas and the Russian south are all in the opening frame.
	await ctx.capture("colours-map", 640)
	ctx.log("map photographed")
