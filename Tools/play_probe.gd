extends RefCounted
# A full play session: menu -> country pick -> campaign -> HUD inventory -> several screens.
# One script per session (the file channel is read once at startup).

const CAMPAIGN_WAIT := 14.0

var stage := "start"

func log_hud(ctx: Variant, label: String) -> void:
	var root: Node = ctx.get_scene_root()
	var buttons: Array[String] = []
	var labels: Array[String] = []
	var disabled: Array[String] = []
	if root == null:
		ctx.log("HUD " + label + ": no scene root")
		return
	for node: Node in root.find_children("*", "Button", true, false):
		var b: Button = node as Button
		if b == null or not b.is_visible_in_tree():
			continue
		var text: String = b.text.strip_edges()
		if text.is_empty():
			continue
		buttons.append(text)
		if b.disabled:
			disabled.append(text)
	for node: Node in root.find_children("*", "Label", true, false):
		var l: Label = node as Label
		if l == null or not l.is_visible_in_tree():
			continue
		var text: String = l.text.strip_edges()
		if text.length() > 3:
			labels.append(text)
	ctx.log("HUD " + label + ": buttons=" + str(buttons.size()) + " labels=" + str(labels.size()) + " disabled=" + str(disabled.size()))
	for i in range(mini(buttons.size(), 60)):
		ctx.log("  BTN " + buttons[i] + ("  [DISABLED]" if disabled.has(buttons[i]) else ""))
	for i in range(mini(labels.size(), 40)):
		ctx.log("  LBL " + labels[i])

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
	ctx.log("probe entered, stage=start")
	var root: Node = ctx.get_scene_root()
	ctx.log("scene=" + (root.scene_file_path if root != null else "<null>"))
	await ctx.wait(1.0)

	# --- 1. main menu inventory -------------------------------------------------------
	stage = "menu"
	log_hud(ctx, "main-menu")
	await ctx.capture("01-main-menu", 1920)

	# --- 2. singleplayer --------------------------------------------------------------
	stage = "singleplayer"
	var sp: Button = find_button(ctx, func(b: Button) -> bool: return b.text.to_lower().contains("singleplayer"))
	if sp == null:
		ctx.log("no singleplayer button; buttons were listed above")
		return
	ctx.log("clicking singleplayer")
	await ctx.click_button(sp)
	await ctx.wait(2.0)
	root = ctx.get_scene_root()
	ctx.log("scene now=" + (root.scene_file_path if root != null else "<null>"))
	await ctx.capture("02-country-picker", 1920)
	log_hud(ctx, "country-picker")

	# --- 3. begin the campaign, reading the tag off the button -----------------------
	stage = "begin"
	var begin: Button = find_button(ctx, func(b: Button) -> bool: return b.text.begins_with("BEGIN AS"))
	if begin == null:
		ctx.log("no BEGIN AS button; picker layout differs")
		await ctx.capture("03-picker-no-begin", 1920)
		return
	var tag: String = begin.text.replace("BEGIN AS ", "").strip_edges()
	ctx.log("begin button text=" + begin.text + " tag=" + tag)
	await ctx.click_button(begin)
	ctx.log("clicked begin; waiting " + str(CAMPAIGN_WAIT) + "s for the globe")
	await ctx.wait(CAMPAIGN_WAIT)

	# --- 4. the campaign HUD ----------------------------------------------------------
	stage = "campaign"
	root = ctx.get_scene_root()
	ctx.log("campaign scene=" + (root.scene_file_path if root != null else "<null>"))
	log_hud(ctx, "campaign")
	await ctx.capture("04-campaign-globe", 1920)

	# --- 5. let time run, then look again ---------------------------------------------
	stage = "running"
	ctx.log("resuming time with Space")
	await ctx.tap_key(KEY_SPACE)
	await ctx.wait(6.0)
	await ctx.capture("05-campaign-running", 1920)
	ctx.log("pausing again")
	await ctx.tap_key(KEY_SPACE)

	# --- 6. walk the side rails -------------------------------------------------------
	stage = "screens"
	for caption in ["Economy", "Nation", "Army", "Diplomacy", "Conflicts", "Research", "Trade"]:
		var btn: Button = find_button(ctx, func(b: Button) -> bool:
			return b.text.to_upper().begins_with(caption.to_upper()))
		if btn == null:
			ctx.log("no nav button for " + caption)
			continue
		ctx.log("opening " + caption)
		await ctx.click_button(btn)
		await ctx.wait(1.6)
		await ctx.capture("06-screen-" + caption.to_lower(), 1920)
		# close it again with the same button
		var again: Button = find_button(ctx, func(b: Button) -> bool:
			return b.text.to_upper().begins_with(caption.to_upper()))
		if again != null:
			await ctx.click_button(again)
			await ctx.wait(0.6)

	ctx.log("probe complete at stage=" + stage)
	ctx.release_all_inputs()
