## Measure where a navigation button actually draws its icon.
##
## Written because "the icons look slightly off" had three plausible causes in the code and
## guessing which one would have been an afternoon spent on the wrong one. It rendered them
## and counted pixels instead, and the answer was two of the three: `icon_alignment` defaults
## to the left, which is three pixels in a fifty-four pixel button, and the stylebox's
## six-over-eight content margins - the optical lift a CAPTION wants - is a fourth pixel
## upward. `h_separation` was the innocent one, and was the first thing suspected.
##
## Kept rather than deleted, because the next person to nudge a stylebox or a theme constant
## has a way to find out what it did to the icons in one command rather than by eye.
##
##     godot --path . --script res://Tools/icon_centring_probe.gd
##
## It writes two pictures next to the user data: a row of style variants to measure, and the
## navigation bar itself at the size the game draws it.
extends SceneTree

const SIZE := 54
const GAP := 16
const ICON := "res://game/gfx/interface/icons/icon_economy.png"
const NAV := ["nation", "economy", "military", "diplomacy",
	"wars", "decisions", "state_affairs", "ledger"]


func _style(top_margin: int, bottom_margin: int) -> StyleBoxFlat:
	var box := StyleBoxFlat.new()
	box.bg_color = Color(0.10, 0.13, 0.17)
	box.border_color = Color(0.30, 0.36, 0.44)
	box.border_width_left = 1
	box.border_width_top = 1
	box.border_width_right = 1
	box.border_width_bottom = 2
	box.corner_radius_top_left = 3
	box.corner_radius_top_right = 3
	box.corner_radius_bottom_left = 3
	box.corner_radius_bottom_right = 3
	box.content_margin_left = 10
	box.content_margin_right = 10
	box.content_margin_top = top_margin
	box.content_margin_bottom = bottom_margin
	return box


func _dress(button: Button, top: int, bottom: int) -> void:
	for state in ["normal", "hover", "pressed", "focus", "disabled"]:
		button.add_theme_stylebox_override(state, _style(top, bottom))


## One button, built the way DealOrForceGame.ActionButton builds a navigation button.
func _button(icon_path: String, variant: int) -> Button:
	var button := Button.new()
	button.text = ""
	button.custom_minimum_size = Vector2(SIZE, SIZE)
	button.size = Vector2(SIZE, SIZE)
	button.icon = load(icon_path)
	button.expand_icon = true
	button.add_theme_constant_override("icon_max_width", 28)

	var top := 6
	var bottom := 8
	match variant:
		0:  # what the button used to be
			pass
		1:  # the innocent suspect
			button.add_theme_constant_override("h_separation", 0)
		2:  # the icon told to centre itself
			button.icon_alignment = HORIZONTAL_ALIGNMENT_CENTER
			button.vertical_icon_alignment = VERTICAL_ALIGNMENT_CENTER
		3:  # the margins made even
			top = 7
			bottom = 7
		_:  # what ships
			button.icon_alignment = HORIZONTAL_ALIGNMENT_CENTER
			button.vertical_icon_alignment = VERTICAL_ALIGNMENT_CENTER
			top = 7
			bottom = 7
	_dress(button, top, bottom)
	return button


func _initialize() -> void:
	# Everything in one window and one capture. Resizing the window between two captures
	# looked tidier and silently produced a blank second picture: the viewport texture is not
	# reallocated in the frame the size changes, so the second shot was of nothing.
	var window := get_root()
	var bar_top := SIZE + 2 * GAP
	window.size = Vector2i(5 * (SIZE + GAP) + GAP,
		bar_top + NAV.size() * (SIZE + 6) + GAP)

	var ground := ColorRect.new()
	ground.color = Color(0.04, 0.06, 0.09)
	ground.size = window.size
	window.add_child(ground)

	# A row of style variants, to measure against each other.
	for variant in 5:
		var button := _button(ICON, variant)
		button.position = Vector2(GAP + variant * (SIZE + GAP), GAP)
		window.add_child(button)

	# And the navigation bar as it ships, at the size the game draws it.
	for index in NAV.size():
		var button := _button("res://game/gfx/interface/icons/icon_%s.png" % NAV[index], 4)
		button.position = Vector2(GAP, bar_top + index * (SIZE + 6))
		window.add_child(button)

	for _frame in 4:
		await process_frame
	var image := window.get_texture().get_image()
	image = image.get_region(Rect2i(Vector2i.ZERO, window.size))
	image.save_png(ProjectSettings.globalize_path("user://icon_centring_probe.png"))
	print("written: ", ProjectSettings.globalize_path("user://icon_centring_probe.png"))
	print("variants row at y=%d, bar column at y=%d, button %d, gap %d"
		% [GAP, bar_top, SIZE, GAP])
	quit()
