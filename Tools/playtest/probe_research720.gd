extends RefCounted

# The technology screen at 720p, and the zoom buttons on it.
#
# The concern is that at 1280x720 the workspace is only about 600 px tall, and the controls above the
# tree - search, the two view buttons, zoom out, the percentage, zoom in and 100% - are a fixed
# 440 px before the search field even starts. A row that is comfortable at 1080p can push the tree
# into nothing at 720p. This opens the screen at that size, captures it, and reports the rectangles
# the toolbar and the tree actually got, so the answer is a measurement rather than an opinion.
#
# Waiting is by state, not by the clock: `ctx.until_exists` waits for the campaign's navigation to
# appear, which is what "the world has finished bootstrapping" looks like from outside.


func _enter_campaign(ctx: Variant) -> bool:
	if ctx.exists("NavResearch"):
		ctx.log("already in a campaign")
		return true
	var single: String = ctx.button_path_by_text("Singleplayer")
	if single != "":
		await ctx.click_button(single)
		await ctx.wait(3.0)
	var on_map: String = ctx.button_path_by_text("Choose country on map")
	if on_map != "":
		await ctx.click_button(on_map)
		await ctx.wait(3.0)
	# Random nation is on the picker itself (CountryPicker/CountryPickerActions): the shortest road
	# into a campaign, and the one that does not care which country a screenshot is taken of.
	var random: String = ctx.button_path_by_text("Random nation")
	if random == "":
		await ctx.wait(3.0)
		random = ctx.button_path_by_text("Random nation")
	if random == "":
		ctx.log("no Random nation button on the picker")
		return false
	await ctx.click_button(random)
	# A world is built here: wait for the thing that proving it finished actually creates.
	if not await ctx.until_exists("NavResearch", 120.0):
		ctx.log("the campaign did not come up within 120 s")
		await ctx.capture("research720-nocampaign", 1280)
		return false
	await ctx.wait(2.0)
	return true


func _rect(node: Node) -> String:
	if node == null or not (node is Control):
		return "missing"
	var control: Control = node
	if not control.visible:
		return "hidden"
	var size: Vector2 = control.get_global_rect().size
	return "%dx%d at %d,%d" % [size.x, size.y, control.get_global_rect().position.x, control.get_global_rect().position.y]


func run(ctx: Variant) -> void:
	if not await _enter_campaign(ctx):
		await ctx.capture("research720-nocampaign", 1280)
		return

	var viewportSize: Vector2 = (ctx.get_scene_root() as Node).get_viewport().get_visible_rect().size
	ctx.log("viewport %dx%d" % [viewportSize.x, viewportSize.y])

	var research: String = ctx.button_path_by_text("Technologies")
	if research == "":
		var nav: Node = ctx.find_button_by_text("Technologies")
		research = "" if nav == null else str(nav)
	if research != "":
		await ctx.click_button(research)
	else:
		var node: Node = ctx.node("NavResearch")
		if node != null and node is Button:
			await ctx.click_button(node)
	await ctx.wait(3.0)

	var graph: Node = ctx.node("TechnologyGraph")
	var scroll: Node = ctx.node("ResearchTreeScroll")
	var zoomIn: Node = ctx.node("ResearchZoomIn")
	var zoomOut: Node = ctx.node("ResearchZoomOut")
	var zoomLabel: Node = ctx.node("ResearchZoomLabel")
	var zoomReset: Node = ctx.node("ResearchZoomReset")
	var search: Node = ctx.node("ResearchSearch")
	ctx.log("research zoom out=%s label=%s in=%s reset=%s" %
		[_rect(zoomOut), _rect(zoomLabel), _rect(zoomIn), _rect(zoomReset)])
	ctx.log("research search=%s graph=%s scroll=%s" % [_rect(search), _rect(graph), _rect(scroll)])
	if scroll != null and search != null and zoomReset != null and scroll is Control and search is Control:
		var scrollTop: float = (scroll as Control).get_global_rect().position.y
		var barBottom: float = (zoomReset as Control).get_global_rect().end.y
		ctx.log("the tree begins %d px below the toolbar's last control" % int(scrollTop - barBottom))
	await ctx.capture("research720-technology", 1280)

	# The buttons have to do what they say at this size, too.
	if zoomIn != null and graph != null and zoomIn is Button:
		var before: float = (graph as Node2D).scale.x
		await ctx.click_button(zoomIn)
		await ctx.wait(0.6)
		ctx.log("zoom in: scale %s -> %s" % [before, (graph as Node2D).scale.x])
	await ctx.capture("research720-zoomed", 1280)
	ctx.log("done")
