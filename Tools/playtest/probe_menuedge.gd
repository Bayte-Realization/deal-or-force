extends RefCounted

# The edge on a menu window, before and after.
#
# Screens outside the campaign (the scenario workshop, the country picker, the settings) are painted
# by the old panel artwork, and its own frame is faint enough that they read as flat dark rectangles
# with no edge and no depth. `TexturedPanel` now draws the shell's line over that artwork, behind the
# content (DealOrForceGame, CampaignShellUI.ShellEdgeStyle), and this walks to the two screens a
# player sees first so the change can be looked at rather than believed.
#
# No campaign is started on purpose: a world takes a minute to build and none of it is needed to
# judge a frame.


func _rect(node: Node) -> String:
	if node == null or not (node is Control):
		return "missing"
	var control: Control = node
	var rect: Rect2 = control.get_global_rect()
	return "%dx%d at %d,%d" % [rect.size.x, rect.size.y, rect.position.x, rect.position.y]


func run(ctx: Variant) -> void:
	await ctx.capture("edge-main-menu", 1280)

	var single: String = ctx.button_path_by_text("Singleplayer")
	if single != "":
		await ctx.click_button(single)
		await ctx.wait(3.0)
	ctx.log("workshop reached")
	# The workshop is a TexturedPanel outside the campaign: this is the screen the edge is for.
	await ctx.capture("edge-workshop", 1280)
	var edge: Node = (ctx.get_scene_root() as Node).find_child("ShellEdge", true, true)
	ctx.log("workshop shell edge: %s" % ("present" if edge != null else "absent"))

	var on_map: String = ctx.button_path_by_text("Choose country on map")
	if on_map == "":
		ctx.log("no Choose country on map button")
		await ctx.capture("edge-no-picker", 1280)
		return
	await ctx.click_button(on_map)
	await ctx.wait(3.5)
	var picker: Node = ctx.get_scene_root()
	var edges: Array = picker.find_children("ShellEdge", "Panel", true, true)
	ctx.log("picker panels with a shell edge: %d" % edges.size())
	var countries: Node = picker.find_child("CountryPicker", true, false)
	ctx.log("picker surface=%s" % _rect(countries))
	await ctx.capture("edge-country-picker", 1280)
	ctx.log("done")
