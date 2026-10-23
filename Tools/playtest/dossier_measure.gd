extends RefCounted

# What the left dossier actually lays out, group by group.
#
# The country card had a hole in it because two stretchy panels were stacked; this panel stacks
# everything in one scroll, so that particular fault cannot be here. What CAN be here is ordering:
# a control that belongs beside a figure ends up at the bottom because it was added last. That is
# not visible from a screenshot at 520 pixels wide, so it is read off the rects instead - every
# child of the scroll's column, with its class, its name and where it landed.


func _walk(ctx: Variant, node: Node, depth: int, lines: Array[String]) -> void:
	if depth > 1:
		return
	for index: int in range(node.get_child_count()):
		var child: Node = node.get_child(index)
		var control: Control = child as Control
		if control == null:
			continue
		var label: String = ""
		if child is Label:
			label = (child as Label).text
			if label.length() > 24:
				label = label.substr(0, 24)
		var needed: float = control.get_combined_minimum_size().y
		lines.append("%s%d %s '%s' vis=%s y=%d h=%d need=%d %s" % [
			"  ".repeat(depth), index, child.get_class(), child.name, str(control.visible),
			int(control.global_position.y), int(control.size.y), int(needed), label])
		if depth == 0:
			_walk(ctx, child, depth + 1, lines)


func run(ctx: Variant) -> void:
	var hud: Node = ctx.get_scene_root()
	var viewport: Vector2 = hud.get_viewport().get_visible_rect().size
	var scale: float = viewport.x / 1920.0

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

	hud = ctx.get_scene_root()
	var scroll: Control = hud.find_child("DossierScroll", true, false) as Control
	if scroll == null:
		ctx.log("no DossierScroll")
		return
	ctx.log("DossierScroll y=" + str(int(scroll.global_position.y)) + " h=" + str(int(scroll.size.y)))
	var lines: Array[String] = []
	_walk(ctx, scroll, 0, lines)
	for line in lines:
		ctx.log(line)
	ctx.log("dossier walked")
