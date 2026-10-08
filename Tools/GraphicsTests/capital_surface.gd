extends SceneTree

# Native integration regression; build the C# project first, then run:
# Godot --headless --path . --script Tools/GraphicsTests/capital_surface.gd
# Omit --headless and pass -- proof-label for actual rendered evidence.

var failures: int = 0

func check(condition: bool, message: String) -> void:
	print(("PASS " if condition else "FAIL ") + message)
	if not condition: failures += 1

func _initialize() -> void:
	call_deferred("observe")

func observe() -> void:
	var scene: PackedScene = load("res://Scenes/Main.tscn") as PackedScene
	var game: Node = scene.instantiate()
	root.add_child(game)
	current_scene = game
	root.size = Vector2i(1920, 1080)
	for attempt: int in range(240):
		await create_timer(0.5).timeout
		var loading: Control = game.find_child("LoadingScreen", true, false) as Control
		if loading == null or not loading.visible: break
	game.call("BeginSession")
	var globe: Node = null
	for child: Node in game.get_children():
		if child.has_method("SetMapMode"): globe = child
	if globe == null:
		check(false, "game exposes map")
		quit(1)
		return
	print(game.call("ConsoleTag", "DEU"))
	var capitals: Node = globe.find_child("CapitalMarkers", true, false)
	check(capitals != null and capitals.get_child_count() > 150, "capital layer contains the world's actual capitals")
	# Nodes rebuilt while their old names were still queued for deletion could silently
	# acquire generic @Sprite3D names. Check the stable identity independently of rendering.
	check(globe.find_child("Capital_DEU", true, false) != null, "capital rebuild preserves its stable country identity")
	var star: Sprite3D = null
	var nearest: float = INF
	var berlin: Vector3 = Vector3(-0.144, 0.794, -0.591).normalized()
	if capitals != null:
		for candidate: Node in capitals.get_children():
			if not candidate is Sprite3D: continue
			var distance: float = (candidate as Sprite3D).position.normalized().distance_to(berlin)
			if distance < nearest:
				nearest = distance
				star = candidate as Sprite3D
	var stage: String = "before"
	var args: PackedStringArray = OS.get_cmdline_user_args()
	if args.size() > 0: stage = args[0]
	var folder: String = "res://.fennara/capital-surface-proof/" + stage
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(folder))
	var initial_basis: Basis
	var initial_position: Vector3
	var view_index: int = 0
	for view: Array in [[false, 50.0, 10.0, 6.2], [false, 48.0, 3.0, 8.5], [true, 50.0, 10.0, 6.2], [false, 50.0, 10.0, 6.2]]:
		globe.call("SetFlatMap", view[0])
		globe.call("FocusOn", view[1], view[2], view[3])
		await create_timer(1.0).timeout
		check(star != null and star.is_visible_in_tree(), "Berlin capital remains visible at this viewpoint")
		if star == null: continue
		check(star.billboard == BaseMaterial3D.BILLBOARD_DISABLED, "capital cannot billboard toward the camera")
		check(not star.fixed_size, "capital uses a real surface quad, not shader distance compensation")
		var normal: Vector3 = Vector3.BACK if view[0] else star.position.normalized()
		check(star.basis.z.normalized().dot(normal) > .999, "capital plane follows the map's outward normal")
		if view[0]:
			check(star.basis.x.normalized().dot(Vector3.RIGHT) > .999 and star.basis.y.normalized().dot(Vector3.UP) > .999, "flat capital remains east/north aligned")
		elif initial_position == Vector3.ZERO:
			initial_basis = star.basis.orthonormalized()
			initial_position = star.position
		else:
			check(star.position.is_equal_approx(initial_position) and star.basis.orthonormalized().is_equal_approx(initial_basis), "camera/projection round trip cannot rotate or move the capital on the globe")
		var camera: Camera3D = star.get_viewport().get_camera_3d()
		var half: Vector2 = star.texture.get_size() * star.pixel_size * .5
		var low: Vector2 = Vector2(INF, INF)
		var high: Vector2 = Vector2(-INF, -INF)
		for x: float in [-half.x, half.x]:
			for y: float in [-half.y, half.y]:
				var point: Vector2 = camera.unproject_position(star.to_global(Vector3(x, y, 0)))
				low = low.min(point)
				high = high.max(point)
		check(maxf((high - low).x, (high - low).y) <= 30.5, "capital quad cannot grow to cover a country when zooming")
		print("CAPITAL_PIXELS view=", view_index, " extent=", high - low, " ground_width=", star.scale.x)
		if DisplayServer.get_name() != "headless":
			await RenderingServer.frame_post_draw
			check(root.get_texture().get_image().save_png(folder + "/view-" + str(view_index) + ".png") == OK, "view captured")
		view_index += 1
	print("CAPITAL_SURFACE_COMPLETE failures=", failures)
	quit(0 if failures == 0 else 1)
