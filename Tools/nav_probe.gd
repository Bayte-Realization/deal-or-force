extends RefCounted
# One screen: into a campaign, and a photograph of the left rail.
# Written to check the navigation group captions after a change to
# ResponsiveCampaignNavigationUI - the rail used to say COUNTRY and then COUNTRY again.

func find_button(ctx: Variant, predicate: Callable) -> Button:
	var root: Node = ctx.get_scene_root()
	if root == null:
		return null
	for node: Node in root.find_children("*", "Button", true, false):
		var b: Button = node as Button
		if b != null and b.is_visible_in_tree() and predicate.call(b):
			return b
	return null

func run(ctx: Variant) -> void:
	await ctx.wait(1.0)
	await ctx.tap_key(KEY_SPACE)
	await ctx.wait(0.6)
	await ctx.tap_key(KEY_SPACE)
	await ctx.wait(1.0)

	var sp: Button = null
	for attempt in range(12):
		sp = find_button(ctx, func(b: Button) -> bool: return b.text.to_lower().contains("singleplayer"))
		if sp != null:
			break
		await ctx.tap_key(KEY_SPACE)
		await ctx.wait(0.8)
	if sp == null:
		ctx.log("ERROR: no singleplayer button")
		return
	await ctx.click_button(sp)
	await ctx.wait(2.0)

	var pick: Button = null
	for attempt in range(15):
		pick = find_button(ctx, func(b: Button) -> bool:
			var t: String = b.text.to_lower()
			return t.contains("choose country") or t.contains("country on map") or t.contains("выбрать страну"))
		if pick != null and not pick.disabled:
			break
		await ctx.wait(0.6)
	if pick == null:
		ctx.log("ERROR: no choose country button")
		return
	await ctx.click_button(pick)
	await ctx.wait(3.0)

	var begin: Button = null
	for attempt in range(30):
		begin = find_button(ctx, func(b: Button) -> bool:
			var t: String = b.text.to_lower()
			return t.begins_with("begin as") or t.begins_with("начать за") or t.begins_with("begin"))
		if begin != null and not begin.disabled:
			break
		await ctx.wait(0.8)
	if begin == null:
		ctx.log("ERROR: no begin button")
		return
	await ctx.click_button(begin)
	await ctx.wait(10.0)

	# The rail's own captions, read out of the tree: the check is the words, not only the pixels.
	var root: Node = ctx.get_scene_root()
	var captions: PackedStringArray = []
	var rows: PackedStringArray = []
	if root != null:
		for node: Node in root.find_children("NavGroup*", "Label", true, false):
			if node.is_visible_in_tree():
				captions.append((node as Label).text)
		for node: Node in root.find_children("Nav*", "Button", true, false):
			if node.is_visible_in_tree():
				rows.append((node as Button).text)
	ctx.log("CAPTIONS " + str(captions))
	ctx.log("ROWS " + str(rows))
	# What the situation desk is actually saying, row by row: the empty panel was the defect.
	var desk: PackedStringArray = []
	if root != null:
		for node: Node in root.find_children("Desk_*", "HBoxContainer", true, false):
			if not node.is_visible_in_tree():
				continue
			var words: PackedStringArray = []
			for label: Node in node.find_children("*", "Label", true, false):
				var l: Label = label as Label
				if l == null or not l.is_visible_in_tree():
					continue
				var text: String = l.text.strip_edges()
				if text.length() > 0:
					words.append(text)
			desk.append(node.name + " :: " + " | ".join(words))
	ctx.log("DESK " + str(desk))
	# How tall the desk actually is: the panel used to hold a fifth of the screen for one line.
	for node: Node in root.find_children("*Desk*", "PanelContainer", true, false):
		if node.is_visible_in_tree():
			var c: Control = node as Control
			ctx.log("DESK_PANEL " + str(node.name) + " size=" + str(c.size) + " min=" + str(c.get_combined_minimum_size()))
	await ctx.wait(1.0)
	await ctx.capture("01-nav-rail", 1920)
	ctx.log("nav probe complete")
	ctx.release_all_inputs()
