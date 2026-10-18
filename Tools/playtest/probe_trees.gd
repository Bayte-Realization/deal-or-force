extends RefCounted

# Two trees, and the hands they are read with.
#
# The report was that the trees cannot be moved around - only scrolled - so this drives the three
# wheel gestures at the real controls and measures what changed: Alt with the wheel zooms, Ctrl
# with the wheel moves the view, and the plain wheel stays an ordinary scroll of the page. A
# picture of a zoomed tree is not proof that the wheel did it; the scale and the scroll are.
#
# Names come from the code that builds them: `DoctrineTreeWindow`, `DoctrineTreeScroll`,
# `DoctrineGraph`, `DoctrineNode_<article>`, `DoctrineBook_<book>` (DoctrineTreeUI), and the
# research screen's `ResearchTreeScroll`, `TechnologyGraph`, `ResearchDependencyView` (ResearchScreenUI).


func _wheel(ctx: Variant, position: Vector2, alt: bool, ctrl: bool, shift: bool, up: bool) -> void:
	var btn: int = MOUSE_BUTTON_WHEEL_UP if up else MOUSE_BUTTON_WHEEL_DOWN
	ctx.input_event("InputEventMouseButton", {
		"button_index": btn,
		"pressed": true,
		"position": position,
		"alt_pressed": alt,
		"ctrl_pressed": ctrl,
		"shift_pressed": shift,
	})
	ctx.input_event("InputEventMouseButton", {
		"button_index": btn,
		"pressed": false,
		"position": position,
		"alt_pressed": alt,
		"ctrl_pressed": ctrl,
		"shift_pressed": shift,
	})
	await ctx.wait(0.45)


func _scale(node: Node) -> float:
	return 0.0 if node == null else node.scale.x


func run(ctx: Variant) -> void:
	var single: String = ctx.button_path_by_text("Singleplayer")
	if single != "":
		await ctx.click_button(single)
		await ctx.wait(3.0)
	var on_map: String = ctx.button_path_by_text("Choose country on map")
	if on_map != "":
		await ctx.click_button(on_map)
		await ctx.wait(4.0)

	# The country is chosen by NAME, not by coordinate: this window is wider than 1920 and a
	# click written for 1920 lands on the next row.
	var picker: Node = ctx.get_scene_root()
	var rows: Array = []
	for button in picker.find_children("*", "Button", true, false):
		if not (button is Button) or str(button.text).is_empty():
			continue
		if "China" in str(button.text) or str(button.text).begins_with("CHN"):
			rows.append(button)
	if rows.is_empty():
		ctx.log("no China row in the picker")
		await ctx.capture("trees-nopicker", 1280)
		return
	var shortest: Button = rows[0]
	for button in rows:
		if str(button.text).length() < str(shortest.text).length():
			shortest = button
	await ctx.click_button(shortest)
	await ctx.wait(2.5)
	var begin: String = ctx.button_path_by_text("BEGIN AS CHN")
	if begin == "":
		# The picker takes a moment to register the selection; a single look is a coin toss.
		await ctx.wait(2.0)
		begin = ctx.button_path_by_text("BEGIN AS CHN")
	if begin == "":
		# Fall back to the scenario screen's own entry: any country will do to look at a screen.
		ctx.log("china was not selected; falling back to Random nation")
		await ctx.capture("trees-noselect", 1280)
		var back: String = ctx.button_path_by_text("Back")
		if back != "":
			await ctx.click_button(back)
			await ctx.wait(2.0)
		var random: String = ctx.button_path_by_text("Random nation")
		if random == "":
			ctx.log("no Random nation button either")
			return
		await ctx.click_button(random)
		await ctx.wait(12.0)
	else:
		await ctx.click_button(begin)
		await ctx.wait(10.0)

	var hud: Node = ctx.get_scene_root()
	# The book of war is a screen of its own. The navigation row is not appearing yet (a data
	# question of its own), so the walk goes through the Army panel's door: DOCTRINES tab, then the
	# button that opens the screen.
	var warBook: Node = hud.find_child("NavDoctrines", true, false)
	if warBook != null and warBook is Button:
		await ctx.click_button(warBook)
		await ctx.wait(3.0)
	else:
		var military: Node = hud.find_child("NavMilitary", true, false)
		if military == null or not (military is Button):
			ctx.log("no NavDoctrines and no NavMilitary: nothing to walk through")
			await ctx.capture("trees-nonav", 1280)
			return
		await ctx.click_button(military)
		await ctx.wait(1.5)
		var openBook: Node = hud.find_child("MilitaryOpenDoctrineScreen", true, false)
		if openBook != null and openBook is Button:
			await ctx.click_button(openBook)
			await ctx.wait(2.5)
		else:
			var openTab: Node = hud.find_child("MilitaryOpenDoctrineTab", true, false)
			if openTab != null and openTab is Button:
				await ctx.click_button(openTab)
				await ctx.wait(1.5)
			var open: Node = hud.find_child("OpenDoctrineScreen", true, false)
			if open == null or not (open is Button):
				ctx.log("no OpenDoctrineScreen button in the Army panel")
				await ctx.capture("trees-nodoor", 1280)
				return
			await ctx.click_button(open)
			await ctx.wait(2.5)

	# ---- the doctrine tree ----
	var graph: Node = hud.find_child("DoctrineGraph", true, false)
	var scroll: Node = hud.find_child("DoctrineTreeScroll", true, false)
	var window: Node = hud.find_child("DoctrineTreeWindow", true, false)
	var cards: Array = hud.find_children("DoctrineNode_*", "PanelContainer", true, false)
	var books: Array = hud.find_children("ScreenBook_*", "Button", true, false)
	var studies: Array = hud.find_children("StudyArticle_*", "Button", true, false)
	ctx.log("doctrine tree: books=%d cards=%d study buttons=%d window=%s card0_size=%s" %
		[books.size(), cards.size(), studies.size(), str(window != null),
		str((cards[0] as Control).size) if cards.size() > 0 else "none"])
	if graph == null or scroll == null:
		ctx.log("the doctrine tree is not on the page")
		await ctx.capture("trees-missing", 1280)
		return
	await ctx.capture("trees-doctrine", 1280)

	var centre: Vector2 = graph.get_global_rect().get_center()
	# The tree sits below the panel's heading and currency strip, so bring it into view first: a
	# wheel aimed at an off-screen rectangle says nothing about what a player's wheel does.
	var page: Node = window.get_parent()
	while page != null and not (page is ScrollContainer):
		page = page.get_parent()
	if page is ScrollContainer:
		page.scroll_vertical = int(window.position.y) - 30
		await ctx.wait(0.7)
	var screen: Vector2 = ctx.get_scene_root().get_viewport().get_visible_rect().size
	var rect: Rect2 = window.get_global_rect()
	centre = rect.get_center()
	centre.x = clampf(centre.x, 40.0, screen.x - 40.0)
	centre.y = clampf(centre.y, 40.0, screen.y - 40.0)
	ctx.log("doctrine window rect=" + str(rect) + " screen=" + str(screen) + " aimed at=" + str(centre))
	var zoomBefore: float = _scale(graph)
	await _wheel(ctx, centre, true, false, false, true)
	ctx.log("doctrine alt+wheel: scale %s -> %s" % [zoomBefore, _scale(graph)])
	await ctx.capture("trees-doctrine-zoomed", 1280)

	var panBefore: int = scroll.scroll_horizontal
	await _wheel(ctx, centre, false, true, false, false)
	ctx.log("doctrine ctrl+wheel: scroll_horizontal %s -> %s" % [panBefore, scroll.scroll_horizontal])

	var plainBefore: int = scroll.scroll_horizontal
	await _wheel(ctx, centre, false, false, false, false)
	ctx.log("doctrine plain wheel: tree stayed at %s (was %s)" % [scroll.scroll_horizontal, plainBefore])

	# The third hand: the middle button drags the tree, the way every map does.
	var dragBefore: int = scroll.scroll_horizontal
	ctx.input_event("InputEventMouseButton", {
		"button_index": MOUSE_BUTTON_MIDDLE, "pressed": true, "position": centre,
	})
	ctx.input_event("InputEventMouseMotion", {"position": centre - Vector2(150, 0)})
	await ctx.wait(0.4)
	ctx.input_event("InputEventMouseButton", {
		"button_index": MOUSE_BUTTON_MIDDLE, "pressed": false, "position": centre - Vector2(150, 0),
	})
	await ctx.wait(0.4)
	ctx.log("doctrine middle-drag: scroll_horizontal %s -> %s" % [dragBefore, scroll.scroll_horizontal])

	# And the same two hands as buttons, for a layout where Alt belongs to the window manager.
	# Can any control on this layer be clicked at all? The close button is the honest probe: if the
	# screen is still up afterwards, the problem is the layer's input, not one toolbar.
	var closeButton: Node = hud.find_child("CloseDoctrineScreen", true, false)
	if closeButton != null and closeButton is Button:
		# Who is actually under the pointer? Godot answers this itself, and a name is worth more
		# than a guess about layers.
		var target: Rect2 = (closeButton as Control).get_global_rect()
		ctx.move_mouse_to(target.get_center())
		await ctx.wait(0.6)
		var under: Node = ctx.get_scene_root().get_viewport().gui_get_hovered_control()
		var chain: String = ""
		var walk: Node = under
		for step in range(5):
			if walk == null:
				break
			chain = str(walk.name) + (" < " if chain != "" else "") + chain
			walk = walk.get_parent()
		ctx.log("doctrine close button: under the pointer=%s chain=%s (wanted %s)" %
			[str(under), chain, str(closeButton)])
		await ctx.click_button(closeButton)
		await ctx.wait(0.8)
		var layer: Node = hud.find_child("Doctrines", true, false)
		ctx.log("doctrine close button: layer still visible=%s" % str(layer != null and layer.visible))
		# Open it again for the rest of the walk.
		var reopen: Node = hud.find_child("NavDoctrines", true, false)
		if reopen != null and reopen is Button:
			await ctx.click_button(reopen)
			await ctx.wait(1.5)
		else:
			var navMil2: Node = hud.find_child("NavMilitary", true, false)
			if navMil2 != null and navMil2 is Button:
				await ctx.click_button(navMil2)
				await ctx.wait(1.0)
			var openBook2: Node = hud.find_child("MilitaryOpenDoctrineScreen", true, false)
			if openBook2 != null and openBook2 is Button:
				await ctx.click_button(openBook2)
				await ctx.wait(1.5)
	var zoomIn: Node = hud.find_child("DoctrineZoomIn", true, false)
	if zoomIn != null and zoomIn is Button:
		# Does the click even reach the button? Its own signal is the honest answer: if this line
		# does not appear, the problem is the pointer, not the zoom.
		zoomIn.pressed.connect(func(): ctx.log("doctrine zoom button: its signal fired"))
		var beforeButton: float = _scale(graph)
		await ctx.click_button(zoomIn)
		await ctx.wait(0.6)
		ctx.log("doctrine zoom button: scale %s -> %s" % [beforeButton, _scale(graph)])
	var zoomLabel: Node = hud.find_child("DoctrineZoomLabel", true, false)
	if zoomLabel != null:
		ctx.log("doctrine zoom label='" + str(zoomLabel.text) + "'")
	var reset: Node = hud.find_child("DoctrineZoomReset", true, false)
	if reset != null and reset is Button:
		await ctx.click_button(reset)
		await ctx.wait(0.6)
		ctx.log("doctrine zoom reset: scale %s" % _scale(graph))

	# The two views of the book: do the buttons actually switch it? The cards are the answer -
	# articles for a tactical book, courses for the strategic one.
	var courseView: Node = hud.find_child("DoctrineCourseView", true, false)
	var tacticalView: Node = hud.find_child("DoctrineTacticalView", true, false)
	if courseView != null and courseView is Button:
		await ctx.click_button(courseView)
		await ctx.wait(1.2)
		var courses: Array = hud.find_children("Strategy_*", "PanelContainer", true, false)
		ctx.log("doctrine view switch: after the course button courses=%d" % courses.size())
		await ctx.capture("trees-doctrine-courses", 1280)
	if tacticalView != null and tacticalView is Button:
		await ctx.click_button(tacticalView)
		await ctx.wait(1.2)
		var back: Array = hud.find_children("DoctrineNode_*", "PanelContainer", true, false)
		ctx.log("doctrine view switch: after the tactical button cards=%d" % back.size())
	await ctx.capture("trees-doctrine-wide", 1280)

	# ---- the technology tree ----
	var research: Node = hud.find_child("NavResearch", true, false)
	if research != null and research is Button:
		await ctx.click_button(research)
		await ctx.wait(2.5)
	var dependencies: Node = hud.find_child("ResearchDependencyView", true, false)
	if dependencies != null and dependencies is Button:
		await ctx.click_button(dependencies)
		await ctx.wait(2.0)
	var techGraph: Node = hud.find_child("TechnologyGraph", true, false)
	var techScroll: Node = hud.find_child("ResearchTreeScroll", true, false)
	var techCards: Array = hud.find_children("Technology_*", "Button", true, false)
	ctx.log("technology tree: nav=%s dependencies=%s cards=%d graph=%s visible=%s" %
		[str(research != null), str(dependencies != null), techCards.size(), str(techGraph != null),
		str(techGraph != null and techGraph.visible)])
	await ctx.capture("trees-technology", 1280)
	var techZoomIn: Node = hud.find_child("ResearchZoomIn", true, false)
	var techZoomLabel: Node = hud.find_child("ResearchZoomLabel", true, false)
	if techZoomIn != null and techZoomIn is Button and techGraph != null:
		var beforeTech: float = _scale(techGraph)
		await ctx.click_button(techZoomIn)
		await ctx.wait(0.6)
		ctx.log("technology zoom button: scale %s -> %s label='%s'" %
			[beforeTech, _scale(techGraph), str(techZoomLabel.text if techZoomLabel != null else "")])
	if techGraph == null or techScroll == null:
		ctx.log("the technology graph is not on the page")
		await ctx.capture("trees-technology-missing", 1280)
		ctx.log("done")
		return

	var techCentre: Vector2 = (techScroll as Control).get_global_rect().get_center()
	var techZoomBefore: float = _scale(techGraph)
	await _wheel(ctx, techCentre, true, false, false, true)
	ctx.log("technology alt+wheel: scale %s -> %s" % [techZoomBefore, _scale(techGraph)])
	await ctx.capture("trees-technology-zoomed", 1280)

	var techPanBefore: int = techScroll.scroll_horizontal
	await _wheel(ctx, techCentre, false, true, false, false)
	ctx.log("technology ctrl+wheel: scroll_horizontal %s -> %s" % [techPanBefore, techScroll.scroll_horizontal])
	ctx.log("done")
	ctx.close_scene()
