## Turn the vector icons into pictures the game can actually ship.
##
## The sprite catalogue builds its index by walking `gfx/` on disk, so a picture only exists
## for the game if a real file of that name is sitting beside it - and the rest of the art is
## PNG for a reason written down in NationFlagUI: anybody can open a PNG in whatever they
## already have and paint over it, and a modding format half the people who want to use it
## cannot open is not a modding format.
##
## Rendered at four times the icon's own size and left there, the same way the flags are:
## downsampling is the direction that looks fine.
##
##     godot --headless --path . --script res://Tools/svg_to_png.gd
extends SceneTree

const FOLDER := "res://game/gfx/interface/ideologies"
const SCALE := 4.0


func _initialize() -> void:
	var folder := DirAccess.open(FOLDER)
	if folder == null:
		print("no such folder: ", FOLDER)
		quit(1)
		return

	var made := 0
	var failed := 0
	for name in folder.get_files():
		if not name.ends_with(".svg"):
			continue
		var source := FOLDER + "/" + name
		var bytes := FileAccess.get_file_as_bytes(source)
		if bytes.is_empty():
			print("  unreadable: ", name)
			failed += 1
			continue

		var picture := Image.new()
		if picture.load_svg_from_buffer(bytes, SCALE) != OK:
			print("  would not render: ", name)
			failed += 1
			continue

		var target := source.replace(".svg", ".png")
		if picture.save_png(ProjectSettings.globalize_path(target)) != OK:
			print("  would not save: ", target)
			failed += 1
			continue

		print("  %s -> %dx%d" % [name, picture.get_width(), picture.get_height()])
		made += 1

	print("%d icon(s) rendered, %d failed" % [made, failed])
	quit(1 if failed > 0 else 0)
