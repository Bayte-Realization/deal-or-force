extends RefCounted

# Measure the gap between "Full diplomacy" and the first action, which is the thing under review.
#
# The gap is the number of empty pixels between the bottom of the links row and the top of the
# actions host. That is what a reader sees as a hole, and it is measurable exactly, so it does not
# need to be judged from a screenshot. A single scroll for the whole body should make it a
# separation value - seven pixels - whatever the dossier says.


func _gap(ctx: Variant, hud: Node) -> void:
	var links: Control = hud.find_child("CountryCardLinks", true, false) as Control
	var host: Control = hud.find_child("CountryActionsHost", true, false) as Control
	if links == null or host == null:
		ctx.log("links or host absent")
		return
	var bottom: float = links.global_position.y + links.size.y
	var empty: float = host.global_position.y - bottom
	ctx.log("links bottom=" + str(int(bottom)) + " actions top=" + str(int(host.global_position.y)) +
		" GAP=" + str(int(empty)))
	if host.get_child_count() > 0:
		var first: Control = host.get_child(0) as Control
		if first != null:
			var heading: String = ""
			var labels: Array[Node] = first.find_children("*", "Label", true, false)
			if labels.size() > 0:
				heading = (labels[0] as Label).text
			ctx.log("first action child y=" + str(int(first.global_position.y)) + " h=" + str(int(first.size.y)) +
				" label=" + heading)


func _scroll(ctx: Variant, hud: Node, wanted: String) -> void:
	var control: Control = hud.find_child(wanted, true, false) as Control
	if control == null:
		ctx.log(str(wanted) + " absent")
		return
	var line: String = str(wanted) + " y=" + str(int(control.global_position.y)) + " h=" + str(int(control.size.y))
	if control.get_child_count() > 0:
		var inside: Control = control.get_child(0) as Control
		if inside != null:
			line += " content=" + str(int(inside.get_combined_minimum_size().y))
	ctx.log(line)


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

	# Japan, read off an earlier frame. Any foreign country opens the same card.
	await ctx.click_at(Vector2(1181, 664) * scale)
	await ctx.wait(3.0)
	hud = ctx.get_scene_root()
	_scroll(ctx, hud, "CountryBodyScroll")
	_gap(ctx, hud)
	await ctx.capture("w5-screen", 520)
	ctx.log("measured")
