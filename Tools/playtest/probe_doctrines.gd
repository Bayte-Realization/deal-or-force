extends RefCounted

# Does the Doctrines page exist, and does it show what the simulation says it should?
#
# The walk: main menu -> single player -> China -> the campaign -> the Laws screen -> the
# Doctrines tab. Then it counts what it found by name (the page names its own parts) and presses
# the first Study button it can find, which is the one thing a picture cannot prove happened.
#
# Names it looks for come from the code that builds them: `NavLaws` (ResponsiveCampaignNavigationUI),
# `PolicyTab_Doctrines` (PolicyWorkspaceUI), `DoctrineCurrencies`, `Doctrine_<branch>`,
# `StudyArticle_<node>` / `RushArticle_<node>` (DoctrineUI), `AdoptStrategy_<course>` and the
# top-bar readouts `HudStat_ARMY_XP` ... `HudStat_POLITICAL_CAPITAL` (TopBarHudUI).


func _buttons(hud: Node, prefix: String) -> Array:
	var found: Array = []
	for node in hud.find_children(prefix + "*", "Button", true, false):
		found.append(node)
	return found


func _report(ctx: Variant, hud: Node, wanted: String) -> bool:
	var node: Node = hud.find_child(wanted, true, false)
	ctx.log(str(wanted) + (" present" if node != null else " ABSENT"))
	return node != null


func run(ctx: Variant) -> void:
	var viewport: Vector2 = ctx.get_scene_root().get_viewport().get_visible_rect().size
	ctx.log("viewport=" + str(viewport))

	var single: String = ctx.button_path_by_text("Singleplayer")
	if single != "":
		await ctx.click_button(single)
		await ctx.wait(3.0)

	# Singleplayer opens the scenario screen, not the country picker: choose the world first.
	var on_map: String = ctx.button_path_by_text("Choose country on map")
	if on_map != "":
		ctx.log("choosing a country on the map")
		await ctx.click_button(on_map)
		await ctx.wait(4.0)

	# The country is chosen by NAME, not by a coordinate: the window is 1982 wide on this
	# machine, and a click that was written for 1920 lands on the next row.
	var picker: Node = ctx.get_scene_root()
	var rows: Array = []
	var seen: int = 0
	for button in picker.find_children("*", "Button", true, false):
		if not (button is Button) or str(button.text).is_empty():
			continue
		if seen < 20:
			ctx.log("button: '" + str(button.text) + "'")
			seen += 1
		if "China" in str(button.text) or str(button.text).begins_with("CHN"):
			rows.append(button)
	if rows.is_empty():
		ctx.log("no China row in the picker")
		await ctx.capture("doctrines-nopicker", 1280)
		return
	var shortest: Button = rows[0]
	for button in rows:
		if str(button.text).length() < str(shortest.text).length():
			shortest = button
	ctx.log("pressing '" + str(shortest.text) + "'")
	await ctx.click_button(shortest)
	await ctx.wait(2.5)
	var begin: String = ctx.button_path_by_text("BEGIN AS CHN")
	if begin == "":
		ctx.log("china was not selected")
		await ctx.capture("doctrines-noselect", 1280)
		return
	await ctx.click_button(begin)
	await ctx.wait(10.0)

	var hud: Node = ctx.get_scene_root()
	# The book of war moved out of the Laws workspace and into the Army panel, where the
	# experience that pays for it is earned: NavMilitary, then the panel's DOCTRINES tab.
	var military: Node = hud.find_child("NavMilitary", true, false)
	if military == null:
		ctx.log("no NavMilitary button")
		await ctx.capture("doctrines-nonav", 1280)
		return
	await ctx.click_button(military)
	await ctx.wait(2.5)

	var tabs: Node = hud.find_child("ArmyCommandTabs", true, false)
	if tabs == null or not (tabs is TabContainer):
		ctx.log("no army command tabs")
		await ctx.capture("doctrines-notab", 1280)
		return
	var tabIndex: int = -1
	for index in range(tabs.get_tab_count()):
		if "DOCTRINE" in str(tabs.get_tab_title(index)).to_upper():
			tabIndex = index
	ctx.log("doctrine tab index=" + str(tabIndex) + " of " + str(tabs.get_tab_count()))
	if tabIndex < 0:
		await ctx.capture("doctrines-notab", 1280)
		return
	tabs.current_tab = tabIndex
	await ctx.wait(2.5)

	hud = ctx.get_scene_root()
	_report(ctx, hud, "DoctrineCurrencies")
	_report(ctx, hud, "HudStat_ARMY_XP")
	_report(ctx, hud, "HudStat_AIR_XP")
	_report(ctx, hud, "HudStat_NAVY_XP")
	_report(ctx, hud, "HudStat_POLITICAL_CAPITAL")
	var books: Array = _buttons(hud, "DoctrineBook_")
	ctx.log("books=" + str(books.size()))
	var strategies: Array = hud.find_children("Strategy_*", "PanelContainer", true, false)
	ctx.log("courses=" + str(strategies.size()))
	var studies: Array = _buttons(hud, "StudyArticle_")
	var rushes: Array = _buttons(hud, "RushArticle_")
	var adopts: Array = _buttons(hud, "AdoptStrategy_")
	ctx.log("study buttons=" + str(studies.size()) + " rush=" + str(rushes.size()) +
		" adopt=" + str(adopts.size()))
	# The article names are the one thing the buttons do not say: measure that they are drawn.
	var names: Array = hud.find_children("Title_*", "Label", true, false)
	ctx.log("article labels=" + str(names.size()))
	if names.size() > 0:
		var first: Label = names[0]
		ctx.log("first article label text='" + str(first.text) + "' size=" + str(first.size) +
			" visible=" + str(first.is_visible_in_tree()))
	var tree: Node = hud.find_child("DoctrineGraph", true, false)
	var cards: Array = hud.find_children("DoctrineNode_*", "PanelContainer", true, false)
	ctx.log("doctrine tree: graph=" + str(tree != null) + " cards=" + str(cards.size()))
	await ctx.capture("doctrines-menu", 1280)

	# The one thing a picture cannot prove: that a button does what it says.
	if studies.size() > 0:
		var first: Button = studies[0]
		var enabled: bool = not first.disabled
		ctx.log("pressing '" + str(first.text) + "' enabled=" + str(enabled))
		await ctx.click_button(first)
		await ctx.wait(2.0)
		hud = ctx.get_scene_root()
		ctx.log("after the press: study buttons=" + str(_buttons(hud, "StudyArticle_").size()))
		await ctx.capture("doctrines-after-study", 1280)
	# The seven courses live below the tree: scroll the page to the end and look.
	var options: Node = hud.find_child("DoctrineRows", true, false)
	if options != null and options.get_parent() is ScrollContainer:
		var scroll: ScrollContainer = options.get_parent()
		scroll.scroll_vertical = int(scroll.get_v_scroll_bar().max_value)
		await ctx.wait(1.0)
		var courses: Array = hud.find_children("Strategy_*", "PanelContainer", true, false)
		ctx.log("courses after scrolling=" + str(courses.size()))
		await ctx.capture("doctrines-courses", 1280)
	ctx.log("done")