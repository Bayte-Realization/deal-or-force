extends RefCounted
# Session 2: main menu -> singleplayer -> the scenario picker -> the globe -> a campaign,
# then a walk of every screen a player actually lives in: the top bar, the country dossier,
# the decisions agenda, diplomacy, and the map itself.
# One script per session (the file channel is read once at startup).

const CAMPAIGN_WAIT := 16.0

var stage := "start"

func log_hud(ctx: Variant, label: String) -> void:
	var root: Node = ctx.get_scene_root()
	var buttons: Array[String] = []
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
	ctx.log("HUD " + label + ": buttons=" + str(buttons.size()) + " disabled=" + str(disabled.size()))
	for i in range(mini(buttons.size(), 70)):
		ctx.log("  BTN " + buttons[i] + ("  [DISABLED]" if disabled.has(buttons[i]) else ""))

func find_button(ctx: Variant, predicate: Callable) -> Button:
	var root: Node = ctx.get_scene_root()
	if root == null:
		return null
	for node: Node in root.find_children("*", "Button", true, false):
		var b: Button = node as Button
		if b != null and b.is_visible_in_tree() and predicate.call(b):
			return b
	return null

func any_button(ctx: Variant, needles: Array) -> Button:
	return find_button(ctx, func(b: Button) -> bool:
		var t: String = b.text.to_lower()
		for n in needles:
			if t.contains(String(n).to_lower()):
				return true
		return false
	)

func open_and_shoot(ctx: Variant, label: String, needles: Array, shot: String, settle := 1.8) -> bool:
	var btn: Button = any_button(ctx, needles)
	if btn == null:
		ctx.log("no button for " + label)
		return false
	ctx.log("opening " + label + " via '" + btn.text.strip_edges() + "'")
	await ctx.click_button(btn)
	await ctx.wait(settle)
	await ctx.capture(shot, 1920)
	log_hud(ctx, label)
	# The same rail button closes it again.
	var again: Button = any_button(ctx, needles)
	if again != null and again.is_visible_in_tree():
		await ctx.click_button(again)
		await ctx.wait(0.5)
	return true

func run(ctx: Variant) -> void:
	ctx.log("probe2 entered, stage=start")
	await ctx.wait(1.0)

	# --- 1. main menu ---------------------------------------------------------------
	stage = "menu"
	log_hud(ctx, "main-menu")
	await ctx.capture("01-main-menu", 1920)

	# --- 2. singleplayer -----------------------------------------------------------
	stage = "singleplayer"
	var sp: Button = any_button(ctx, ["singleplayer"])
	if sp == null:
		ctx.log("no singleplayer button")
		return
	ctx.log("clicking singleplayer")
	await ctx.click_button(sp)
	await ctx.wait(2.5)
	await ctx.capture("02-picker", 1920)
	log_hud(ctx, "picker")

	# --- 3. into the world ---------------------------------------------------------
	stage = "begin"
	var begin: Button = find_button(ctx, func(b: Button) -> bool: return b.text.to_upper().begins_with("BEGIN AS"))
	if begin != null:
		ctx.log("begin button text=" + begin.text)
		await ctx.click_button(begin)
	else:
		var on_map: Button = any_button(ctx, ["choose country on map", "on map", "pick on map"])
		if on_map == null:
			ctx.log("no BEGIN AS and no choose-on-map; picker buttons listed above")
			await ctx.capture("03-picker-stuck", 1920)
			return
		ctx.log("clicking '" + on_map.text.strip_edges() + "'")
		await ctx.click_button(on_map)
		await ctx.wait(3.0)
		await ctx.capture("03a-globe", 1920)
		# The globe is the picker: clicking the middle of the viewport takes whatever is there.
		var root2: Node = ctx.get_scene_root()
		var size: Vector2 = root2.get_viewport_rect().size if root2 != null else Vector2(1920, 1080)
		ctx.log("clicking the globe at " + str(size * 0.5))
		await ctx.click_at(size * 0.5)
		await ctx.wait(2.5)
		log_hud(ctx, "after-globe-click")
		begin = find_button(ctx, func(b: Button) -> bool: return b.text.to_upper().begins_with("BEGIN AS"))
		if begin == null:
			await ctx.capture("03b-no-begin", 1920)
			ctx.log("still no BEGIN AS after the globe click")
			return
		ctx.log("begin button now text=" + begin.text)
		await ctx.click_button(begin)

	ctx.log("waiting " + str(CAMPAIGN_WAIT) + "s for the campaign to open")
	await ctx.wait(CAMPAIGN_WAIT)

	# --- 4. the campaign, as a player finds it -------------------------------------
	stage = "campaign"
	log_hud(ctx, "campaign")
	await ctx.capture("04-campaign-globe", 1920)

	# --- 5. the country dossier ----------------------------------------------------
	stage = "dossier"
	# The selected country's card is the thing on the map; find its header.
	await open_and_shoot(ctx, "country-card", ["overview", "government", "relations"], "05-country-card", 2.0)

	# --- 6. the decisions agenda ---------------------------------------------------
	stage = "decisions"
	await open_and_shoot(ctx, "decisions", ["decisions"], "06-decisions", 2.2)

	# --- 7. every other screen -----------------------------------------------------
	stage = "screens"
	for pair in [["economy", "07-economy"], ["nation", "08-nation"], ["army", "09-army"],
			["diplomacy", "10-diplomacy"], ["conflict", "11-conflicts"], ["research", "12-research"],
			["trade", "13-trade"], ["subject", "14-subjects"]]:
		await open_and_shoot(ctx, pair[0], [pair[0]], pair[1], 1.8)

	# --- 8. time moving ------------------------------------------------------------
	stage = "running"
	await ctx.tap_key(KEY_SPACE)
	await ctx.wait(8.0)
	await ctx.capture("15-campaign-running", 1920)
	await ctx.tap_key(KEY_SPACE)

	ctx.log("probe2 complete at stage=" + stage)
	ctx.release_all_inputs()
