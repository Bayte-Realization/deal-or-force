extends RefCounted
# Paused Russia campaign. Real UI interaction; no synthetic campaign mutation.
func run(ctx: Variant) -> void:
	var diplomacy: Control = ctx.get_scene_root().find_child("Diplomacy", true, false)
	if not diplomacy.visible: await ctx.click_button(ctx.get_scene_root().find_child("NavDiplomacy", true, false))
	await ctx.wait(0.3)
	for entry: LineEdit in ctx.get_scene_root().find_children("*", "LineEdit", true, false):
		if entry.is_visible_in_tree() and entry.placeholder_text == "Find a country...":
			entry.text = "LVA"
			entry.text_changed.emit("LVA")
	await ctx.wait(0.3)
	var picked: bool = false
	for button: Button in ctx.get_scene_root().find_children("*", "Button", true, false):
		if not button.is_visible_in_tree(): continue
		for label: Label in button.find_children("*", "Label", true, false):
			if label.text == "Latvia":
				await ctx.click_button(button)
				picked = true
				break
		if picked: break
	if not picked:
		ctx.error("Latvia row was not found")
		return
	await ctx.wait(0.3)
	await ctx.click_button(ctx.get_scene_root().find_child("IssueUltimatumWide", true, false))
	await ctx.wait(0.3)
	var choices: Array[Node] = ctx.get_scene_root().find_children("UltimatumRegion_*", "CheckBox", true, false)
	if choices.size() < 3:
		ctx.error("Multi-region fixture lacks a retained third region")
		return
	await ctx.click_button(choices[0])
	await ctx.wait(0.3)
	await ctx.click_button(choices[1])
	await ctx.wait(0.3)
	var chosen: int = 0
	for choice: CheckBox in ctx.get_scene_root().find_children("UltimatumRegion_*", "CheckBox", true, false):
		if choice.button_pressed: chosen += 1
	if chosen != 2:
		ctx.error("Native region clicks did not select exactly two regions: " + str(chosen))
		return
	var cash: SpinBox = ctx.get_scene_root().find_child("UltimatumReparations", true, false)
	# Native keyboard edit of the actual SpinBox LineEdit, not a direct simulation write.
	cash.get_line_edit().grab_focus()
	await ctx.tap_key(KEY_A, {"ctrl": true, "physical": false})
	await ctx.tap_key(KEY_1, {"physical": false, "unicode": 49})
	await ctx.tap_key(KEY_ENTER, {"physical": false})
	await ctx.wait(0.3)
	var send: Button = ctx.get_scene_root().find_child("SendComposedUltimatum", true, false)
	if send.disabled: ctx.error("Valid multi-region package is disabled")
	ctx.log("Multi-region preview", {"regions": 2, "cash": cash.value,
		"assessment": ctx.get_scene_root().find_child("UltimatumAssessment", true, false).text,
		"terms": ctx.get_scene_root().find_child("UltimatumTerms", true, false).text})
	await ctx.capture("ultimatum-multi-region-preview", 1600)
	var window: Window = ctx.get_scene_root().get_window()
	window.size = Vector2i(1280, 720)
	window.content_scale_size = Vector2i(1280, 720)
	await ctx.wait(0.4)
	var composer: Control = ctx.get_scene_root().find_child("UltimatumComposer", true, false)
	for button: Button in composer.find_children("*", "Button", true, false):
		if button.is_visible_in_tree() and not Rect2(Vector2.ZERO, Vector2(1280, 720)).encloses(button.get_global_rect()):
			ctx.error("Composer button outside small viewport: " + str(button.name))
	await ctx.capture("ultimatum-small-preview", 1280)
	await ctx.click_button(ctx.get_scene_root().find_child("SendComposedUltimatum", true, false))
	await ctx.wait(0.2)
	if ctx.get_scene_root().find_child("SendComposedUltimatum", true, false).text != "CONFIRM AND SEND":
		ctx.error("First click did not request confirmation")
	await ctx.click_button(ctx.get_scene_root().find_child("SendComposedUltimatum", true, false))
	await ctx.wait(0.4)
	ctx.log("Settlement result", {"assessment": ctx.get_scene_root().find_child("UltimatumAssessment", true, false).text,
		"terms": ctx.get_scene_root().find_child("UltimatumTerms", true, false).text})
	if not ctx.get_scene_root().find_child("SendComposedUltimatum", true, false).disabled:
		ctx.error("Delivered package remains sendable")
	await ctx.capture("ultimatum-multi-region-result", 1280)
	await ctx.tap_key(KEY_ESCAPE, {"physical": false})
	await ctx.wait(0.2)
	if ctx.get_scene_root().find_child("UltimatumComposer", true, false) != null:
		ctx.error("Escape left composer open")
	var blocked: bool = false
	for button: Button in ctx.get_scene_root().find_children("*", "Button", true, false):
		if button.is_visible_in_tree() and button.text == "Declare war":
			blocked = button.disabled
			ctx.log("Post-settlement war gate", {"disabled": button.disabled, "reason": button.tooltip_text})
	if not blocked: ctx.error("War is not blocked after accepted settlement")
	window.size = Vector2i(1920, 1080)
	window.content_scale_size = Vector2i(1920, 1080)
	ctx.release_all_inputs()
