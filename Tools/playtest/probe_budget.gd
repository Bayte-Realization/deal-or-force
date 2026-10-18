extends RefCounted

# The pink edge, and whether it is gone.
#
# The report was an ugly pink border around the budget menu and the state-affairs screen, and then
# around everything full-screen. It was not a border: those two workspaces build their panel with
# `atlas_section_ground = "surface.base"`, a token that was in no palette, and an unknown token comes
# back magenta on purpose - so the whole screen was filled with it (Ui.Colour, InterfaceSkin.Colour).
# The token is now in the palette and a misspelt ground falls back with a warning (AtlasSurfaceSkin),
# and this photographs the two screens so the colour can be judged rather than believed.
#
# The route is the one a player takes: choose a country on the map, begin as it, open state affairs,
# and press Public debt inside it - that button is what opens the finance workspace (StateAffairsUI,
# "BudgetOpenSovereignFinance"). It also runs against a game that is already in a campaign, so it can
# be sent to a session that is still open.


func _visible_button(root: Node, names: Array, texts: Array) -> Node:
	for name in names:
		var node: Node = root.find_child(str(name), true, false)
		if node != null and node is Button and (node as Button).visible:
			return node
	for button in root.find_children("*", "Button", true, false):
		if not (button is Button) or not button.visible:
			continue
		var text: String = str((button as Button).text).strip_edges().to_lower()
		for want in texts:
			if text == str(want).to_lower():
				return button
	return null


func _button_starting_with(root: Node, prefix: String) -> Node:
	for button in root.find_children("*", "Button", true, false):
		if not (button is Button) or not button.visible:
			continue
		if str((button as Button).text).strip_edges().to_upper().begins_with(prefix.to_upper()):
			return button
	return null


# The player's road to a campaign: Singleplayer, choose on the map, a country row, then BEGIN AS TAG.
func _enter_campaign(ctx: Variant) -> bool:
	var root: Node = ctx.get_scene_root()
	if _visible_button(root, ["NavStateAffairs"], ["Players", "Game menu"]) != null:
		ctx.log("already in a campaign")
		return true
	var single: String = ctx.button_path_by_text("Singleplayer")
	if single != "":
		await ctx.click_button(single)
		await ctx.wait(3.0)
		root = ctx.get_scene_root()
	var on_map: String = ctx.button_path_by_text("Choose country on map")
	if on_map == "":
		var picker: Node = _visible_button(ctx.get_scene_root(), [],
			["Choose country on map", "Random nation", "New campaign"])
		if picker == null:
			ctx.log("no way to start a campaign from here")
			return false
		await ctx.click_button(picker)
	else:
		await ctx.click_button(on_map)
	await ctx.wait(3.5)

	# A country is chosen by NAME, not by coordinate: the picker is wider than 1920 and a click
	# written for 1920 lands on the next row (probe_trees learned this the same way).
	root = ctx.get_scene_root()
	var rows: Array = []
	for button in root.find_children("*", "Button", true, false):
		if not (button is Button) or not button.visible:
			continue
		var text: String = str((button as Button).text).strip_edges()
		if text.is_empty() or text.length() > 40:
			continue
		rows.append(button)
	if rows.is_empty():
		ctx.log("the picker shows no country rows")
		return false
	var chosen: Button = rows[0]
	for button in rows:
		if "CHN" in str((button as Button).text) or "China" in str((button as Button).text):
			chosen = button
			break
	await ctx.click_button(chosen)
	await ctx.wait(2.0)

	var began: Node = _button_starting_with(root, "BEGIN AS")
	if began == null:
		# The picker takes a moment to register the selection; one look is a coin toss.
		await ctx.wait(2.5)
		began = _button_starting_with(ctx.get_scene_root(), "BEGIN AS")
	if began == null:
		ctx.log("no BEGIN AS button after choosing a country")
		await ctx.capture("budget-nobegin", 1280)
		return false
	await ctx.click_button(began)
	# A bootstrap is a world, an economy and a map: this is the one wait that cannot be short.
	await ctx.wait(14.0)
	return true


func run(ctx: Variant) -> void:
	if not await _enter_campaign(ctx):
		await ctx.capture("budget-nocampaign", 1280)
		return

	var root: Node = ctx.get_scene_root()
	var affairs: Node = _visible_button(root, ["NavStateAffairs", "StateAffairsOpen"],
		["State affairs", "State Affairs", "State affairs "])
	var opened: bool = affairs != null
	if opened:
		await ctx.click_button(affairs)
		await ctx.wait(2.5)
	var affairsPanel: Node = root.find_child("StateAffairsPanel", true, false)
	ctx.log("state affairs: button=%s panel=%s visible=%s" % [str(opened), str(affairsPanel != null),
		str(affairsPanel != null and affairsPanel.visible)])
	await ctx.capture("budget-state-affairs", 1280)

	# And the budget is Public debt inside it - the one that was magenta.
	var debt: Node = _visible_button(root, ["BudgetOpenSovereignFinance"], ["Public debt"])
	if debt == null:
		ctx.log("no Public debt button on the state-affairs screen")
		await ctx.capture("budget-nodebt", 1280)
		return
	await ctx.click_button(debt)
	await ctx.wait(2.5)
	var finance: Node = root.find_child("SovereignFinancePanel", true, false)
	ctx.log("sovereign finance: panel=%s visible=%s" % [str(finance != null),
		str(finance != null and finance.visible)])
	var material: Node = null if finance == null else finance.find_child("AtlasMaterial", true, false)
	ctx.log("sovereign finance ground=%s material=%s" % [str("" if material == null else material.get("Ground")),
		str(material != null)])
	await ctx.capture("budget-sovereign-finance", 1280)
	ctx.log("done")
