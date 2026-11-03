extends RefCounted
# A full play session: intro -> menu -> scenarios -> country pick -> campaign -> HUD inventory -> live play.
# One script per session (the file channel is read once at startup).

const CAMPAIGN_WAIT := 10.0

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

	# --- 1. Skip intro cards if active and inventory main menu ------------------------
	stage = "menu"
	ctx.log("skipping intro plaques with space key")
	await ctx.tap_key(KEY_SPACE)
	await ctx.wait(0.6)
	await ctx.tap_key(KEY_SPACE)
	await ctx.wait(1.0)

	var sp: Button = null
	for attempt in range(12):
		sp = find_button(ctx, func(b: Button) -> bool: return b.text.to_lower().contains("singleplayer"))
		if sp != null:
			break
		ctx.log("waiting for singleplayer button... attempt " + str(attempt + 1))
		await ctx.tap_key(KEY_SPACE)
		await ctx.wait(0.8)

	log_hud(ctx, "main-menu")
	await ctx.capture("01-main-menu", 1920)

	if sp == null:
		ctx.log("ERROR: no singleplayer button found on main menu")
		return

	# --- 2. Enter singleplayer (Scenario Library) -------------------------------------
	stage = "scenarios"
	ctx.log("clicking singleplayer button")
	await ctx.click_button(sp)
	await ctx.wait(2.0)

	log_hud(ctx, "scenario-library")
	await ctx.capture("02-scenario-library", 1920)

	var choose_country: Button = null
	for attempt in range(15):
		choose_country = find_button(ctx, func(b: Button) -> bool:
			var t: String = b.text.to_lower()
			return t.contains("choose country") or t.contains("country on map") or t.contains("выбрать страну"))
		if choose_country != null and not choose_country.disabled:
			break
		ctx.log("waiting for choose country button... attempt " + str(attempt + 1))
		await ctx.wait(0.6)

	if choose_country == null:
		ctx.log("ERROR: no choose country button in scenario library")
		return

	ctx.log("clicking: " + choose_country.text)
	await ctx.click_button(choose_country)
	ctx.log("waiting for scenario loading & country picker to emerge...")
	await ctx.wait(3.0)

	# --- 3. Country Picker & map preview ----------------------------------------------
	stage = "picker"
	var begin: Button = null
	for attempt in range(30):
		begin = find_button(ctx, func(b: Button) -> bool:
			var t: String = b.text.to_lower()
			return t.begins_with("begin as") or t.begins_with("начать за") or t.begins_with("begin"))
		if begin != null and not begin.disabled:
			break
		await ctx.wait(0.8)

	log_hud(ctx, "country-picker")
	await ctx.capture("03-country-picker", 1920)

	if begin == null:
		ctx.log("ERROR: no BEGIN AS button found on picker")
		return

	var tag: String = begin.text.replace("BEGIN AS ", "").strip_edges()
	ctx.log("clicking begin: " + begin.text + " (country " + tag + ")")
	await ctx.click_button(begin)
	ctx.log("waiting " + str(CAMPAIGN_WAIT) + "s for campaign globe and HUD...")
	await ctx.wait(CAMPAIGN_WAIT)

	# --- 4. The campaign globe HUD & DEFCON indicator ---------------------------------
	stage = "campaign"
	root = ctx.get_scene_root()
	ctx.log("campaign scene=" + (root.scene_file_path if root != null else "<null>"))
	log_hud(ctx, "campaign")
	await ctx.capture("04-campaign-globe", 1920)

	# --- 5. Unpause time, let simulation cycles tick ----------------------------------
	stage = "running"
	ctx.log("unpausing campaign with Space")
	await ctx.tap_key(KEY_SPACE)
	await ctx.wait(6.0)
	await ctx.capture("05-campaign-running", 1920)

	ctx.log("pausing simulation with Space")
	await ctx.tap_key(KEY_SPACE)
	await ctx.wait(0.5)

	# --- 6. Walk the main interface rails ---------------------------------------------
	stage = "screens"
	for caption in ["Economy", "Nation", "Army", "Diplomacy", "Conflicts", "Research", "Trade"]:
		var btn: Button = find_button(ctx, func(b: Button) -> bool:
			return b.text.to_upper().begins_with(caption.to_upper()))
		if btn == null:
			ctx.log("no nav button for " + caption)
			continue
		ctx.log("opening " + caption)
		await ctx.click_button(btn)
		await ctx.wait(1.5)
		await ctx.capture("06-screen-" + caption.to_lower(), 1920)
		# toggle closed
		var again: Button = find_button(ctx, func(b: Button) -> bool:
			return b.text.to_upper().begins_with(caption.to_upper()))
		if again != null:
			await ctx.click_button(again)
			await ctx.wait(0.5)

	ctx.log("probe complete successfully at stage=" + stage)
	ctx.release_all_inputs()
