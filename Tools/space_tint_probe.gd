## Does the backdrop's tint eat its texture?
##
## The sky went on being invisible after it was switched on, and the suspect is one line:
## the material sets `albedo_color` from `shell.space`, which is #080f18, and Godot multiplies
## albedo_color into albedo_texture. A near-black tint times a starfield is a black sphere.
##
## Measured rather than argued, because the last confident guess about a rendering bug in this
## project was wrong. Two spheres, same texture, one tinted and one white, and the mean
## brightness of each is the answer.
##
##     godot --path . --script res://Tools/space_tint_probe.gd
extends SceneTree

const TEX := "res://game/gfx/globe/globe_space_background.png"
const TINT := Color("#080f18")


func _sphere(colour: Color, texture: Texture2D, position: Vector3) -> MeshInstance3D:
	var mesh := SphereMesh.new()
	mesh.radius = 1.0
	mesh.height = 2.0
	var material := StandardMaterial3D.new()
	material.albedo_color = colour
	material.albedo_texture = texture
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mesh.material = material
	var node := MeshInstance3D.new()
	node.mesh = mesh
	node.position = position
	return node


func _initialize() -> void:
	var window := get_root()
	window.size = Vector2i(640, 320)

	var texture := load(TEX) as Texture2D
	if texture == null:
		print("the backdrop texture would not load from ", TEX)
		quit(1)
		return
	print("texture loaded: ", texture.get_width(), "x", texture.get_height())

	var world := Node3D.new()
	window.add_child(world)
	world.add_child(_sphere(TINT, texture, Vector3(-1.25, 0, 0)))
	world.add_child(_sphere(Color.WHITE, texture, Vector3(1.25, 0, 0)))

	var camera := Camera3D.new()
	camera.position = Vector3(0, 0, 4)
	camera.current = true
	world.add_child(camera)

	var environment := WorldEnvironment.new()
	var settings := Environment.new()
	settings.background_mode = Environment.BG_COLOR
	settings.background_color = Color(0, 0, 0)
	settings.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	settings.ambient_light_color = Color(1, 1, 1)
	environment.environment = settings
	world.add_child(environment)

	for _frame in 4:
		await process_frame
	var image := window.get_texture().get_image()
	image = image.get_region(Rect2i(Vector2i.ZERO, window.size))
	image.save_png(ProjectSettings.globalize_path("user://space_tint_probe.png"))
	print("written: ", ProjectSettings.globalize_path("user://space_tint_probe.png"))
	print("left sphere is tinted ", TINT.to_html(false), ", right sphere is white")
	quit()
