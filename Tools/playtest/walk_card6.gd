extends RefCounted

# Two panels and a static top: measure what the country card actually did.
#
# The wish is that the top of the card never moves, the facts take the height their own contents
# ask for, and the diplomatic actions live in a panel with a scrollbar of their own. The thing that
# went wrong before was a stretchy facts panel leaving a hole above the buttons, and a hole is a
# number ('links bottom' against 'actions top'), not an impression.


func _measure(ctx: Variant, hud: Node, wanted: String) -> void:
	var control: Control = hud.find_child(wanted, true, false) as Control
	if control == null:
		ctx.log(str(wanted) + " absent")
		return
	var line: String = str(wanted) + " y=" + str(int(control.global_position.y)) + " h=" + str(int(control.size.y))
	if control.get_child_count() > 0:
		var inside: Control = control.get_child(0) as Control
		if inside != null:
			var needed: Vector2 = inside.get_combined_minimum_size()
			line += " content=" + str(int(needed.y)) + " slack=" + str(int(control.size.y - needed.y))
	# A scrollbar that exists is a panel that scrolls on its own.
	if control is ScrollContainer:
		var scrolled: ScrollContainer = control as ScrollContainer
		line += " bar=" + str(int(scrolled.get_v_scroll_bar().size.y))
	ctx.log(line)


func _gap(ctx: Variant, hud: Node) -> void:
	var links: Control = hud.find_child("CountryCardLinks", true, false) as Control
	var host: Control = hud.find_child("CountryActionsHost", true, false) as Control
	if links == null or host == null:
		ctx.log("links or host absent")
		return
	var empty: float = host.global_position.y - (links.global_position.y + links.size.y)
	ctx.log("links bottom=" + str(int(links.global_position.y + links.size.y)) +
		" actions top=" + str(int(host.global_position.y)) + " GAP=" + str(int(empty)))


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
	await ctx.wait(9.0)

	# Japan, read off an earlier frame: any foreign country opens the same card.
	await ctx.click_at(Vector2(1181, 664) * scale)
	await ctx.wait(3.0)
	hud = ctx.get_scene_root()
	_measure(ctx, hud, "CountryCard")
	_measure(ctx, hud, "CountryFactsScroll")
	_measure(ctx, hud, "CountryActionsScroll")
	_gap(ctx, hud)
	await ctx.capture("w6-card", 520)
	ctx.log("measured")
