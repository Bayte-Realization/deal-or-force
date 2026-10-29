extends Node
## Deal or Force - the start-up guard: the game starts on whatever the machine has.
##
## A tester's build showed the Godot splash and then nothing: a black window that never became
## the menu. Nothing said why, and there are three usual reasons - a graphics driver that
## accepts the renderer and then draws nothing, a driver that hangs compiling it, and the
## game's .NET code not loading at all (a build copied without its data folder, or blocked
## by an antivirus). This node is written in GDScript on purpose: it runs when the C# half of
## the game does not, and it is the first autoload, so it runs before anything else can fail.
##
## What it does, in exported builds (and in the editor with `-- --boot-guard`):
##
## - Every start is written down as pending, with a heartbeat every few seconds, until the
##   menu has been seen on screen. A start that stayed pending for more than a few seconds
##   and never reached the menu is one the player gave up on: the next start moves down the
##   renderer chain - Direct3D 12, Vulkan, OpenGL, OpenGL over Direct3D 11 (ANGLE) - and
##   restarts itself on it.
## - Four seconds after the first scene is up it looks at the frame. A frame that is black
##   where the menu has its logo and buttons is a renderer that draws nothing: it moves down
##   the chain and restarts at once, without the player having to do anything.
## - If the game's code did not load, it says so on screen, in English and Russian, with
##   where the log is, instead of leaving a black window.
##
## The choice is kept in user://graphics_override.cfg, which project.godot names as its
## settings override, so it holds for every later start. Launching with --rendering-driver
## or --rendering-method is a choice the player made: it is watched and never overridden.
## Settings > Graphics calls set_renderer() to choose by hand.

const STATE_PATH := "user://graphics.cfg"
const OVERRIDE_PATH := "user://graphics_override.cfg"
const HEARTBEAT_SECONDS := 3.0
## A start pending for longer than this without reaching the menu is a failed start.
const GAVE_UP_AFTER_MSEC := 12000
const CHECK_AFTER_SECONDS := 5.0
const SECOND_LOOK_SECONDS := 4.0
## Share of sampled pixels that must be brighter than dark for the frame to count as drawn.
const BRIGHT_SHARE := 0.004

## Which renderer to try after which, per platform, by Godot's own driver names.
const CHAINS := {
	"Windows": ["d3d12", "vulkan", "opengl3", "opengl3_angle"],
	"Linux": ["vulkan", "opengl3"],
	"FreeBSD": ["vulkan", "opengl3"],
	"macOS": ["metal", "vulkan", "opengl3"],
}

var _active := false
var _explicit := false
var _state := ConfigFile.new()
var _heartbeat := 0.0
var _scene_seconds := 0.0
var _done := false
var _looks := 0
var _overlay: CanvasLayer


func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	_start_autopilot()
	# Automated headless runs have no visible frame. A black dummy viewport is
	# expected, not a broken GPU; never persist a renderer fallback or restart here.
	_active = DisplayServer.get_name() != "headless" and \
		(not OS.has_feature("editor") or OS.get_cmdline_user_args().has("--boot-guard"))
	if not _active:
		set_process(false)
		return
	for argument in OS.get_cmdline_args():
		if argument.begins_with("--rendering-driver") or argument.begins_with("--rendering-method"):
			_explicit = true
	_state.load(STATE_PATH)

	# The last start never got to the menu and the player waited for it: that renderer does
	# not work here. Move on and start again on the next one.
	var driver := current_driver()
	if not _explicit and bool(_state.get_value("start", "pending", false)):
		var lasted := int(_state.get_value("start", "heartbeat", 0)) - int(_state.get_value("start", "started", 0))
		if lasted >= GAVE_UP_AFTER_MSEC and str(_state.get_value("start", "driver", "")) == driver:
			if _step_down(driver, "the last start on %s never reached the menu" % driver):
				return

	_state.set_value("start", "pending", true)
	_state.set_value("start", "driver", driver)
	_state.set_value("start", "started", Time.get_unix_time_from_system() * 1000.0)
	_state.set_value("start", "heartbeat", Time.get_unix_time_from_system() * 1000.0)
	_state.save(STATE_PATH)


func _process(delta: float) -> void:
	if _done:
		return
	_heartbeat += delta
	if _heartbeat >= HEARTBEAT_SECONDS:
		_heartbeat = 0.0
		_state.set_value("start", "heartbeat", Time.get_unix_time_from_system() * 1000.0)
		_state.save(STATE_PATH)

	var scene := get_tree().current_scene
	if scene == null:
		return
	_scene_seconds += delta
	if _scene_seconds < CHECK_AFTER_SECONDS:
		return

	# The game's code: the first scene is a C# scene, and a script that cannot be made is a
	# .NET half that did not load.
	var script := scene.get_script() as Script
	if script == null or not script.can_instantiate():
		_done = true
		_finish("code did not load")
		_show_code_failure()
		return

	# The frame: the menu has a logo and a row of lit buttons, so a frame with next to nothing
	# bright on it is a renderer that is not drawing. Asked twice, four seconds apart, so a
	# slow machine still building its menu is not taken for a broken driver.
	if _looks == 0 or _scene_seconds >= CHECK_AFTER_SECONDS + SECOND_LOOK_SECONDS:
		_looks += 1
		if _frame_is_drawn():
			_done = true
			_finish("ok")
			return
	if _looks < 2:
		return
	_done = true
	if not _explicit and _step_down(current_driver(), "the menu came up black on %s" % current_driver()):
		return
	_finish("frame black, nothing left to try")


## The renderer this start is using, by Godot's name for it.
func current_driver() -> String:
	return RenderingServer.get_current_rendering_driver_name()


## The renderers this platform can be set to, in the order the guard tries them.
func renderers() -> Array:
	return CHAINS.get(OS.get_name(), ["vulkan", "opengl3"])


## Choose a renderer by hand ("auto" to let the project decide). Takes effect on the next start.
func set_renderer(driver: String) -> void:
	if driver == "auto" or driver.is_empty():
		DirAccess.remove_absolute(ProjectSettings.globalize_path(OVERRIDE_PATH))
	else:
		_write_override(driver)
	_state.set_value("renderer", "chosen", driver)
	_state.save(STATE_PATH)


## What the player chose in the settings, or "auto".
func chosen_renderer() -> String:
	return str(_state.get_value("renderer", "chosen", "auto"))


func _step_down(failed: String, why: String) -> bool:
	var chain := renderers()
	var at := chain.find(failed)
	if at < 0 or at + 1 >= chain.size():
		return false
	var next: String = chain[at + 1]
	push_warning("Deal or Force: %s; trying %s." % [why, next])
	_write_override(next)
	_state.set_value("start", "pending", false)
	_state.set_value("renderer", "stepped_down_from", failed)
	_state.set_value("renderer", "reason", why)
	_state.save(STATE_PATH)
	_done = true
	OS.set_restart_on_exit(true, _restart_arguments())
	get_tree().quit()
	return true


## What to start again with. The engine keeps the arguments it has consumed to itself, so the
## project path (for a start from the editor binary) and the player's own arguments after `--`
## are put back by hand.
func _restart_arguments() -> PackedStringArray:
	var arguments := PackedStringArray()
	if OS.has_feature("editor"):
		arguments.append("--path")
		arguments.append(ProjectSettings.globalize_path("res://"))
	for argument in OS.get_cmdline_args():
		arguments.append(argument)
	var user := OS.get_cmdline_user_args()
	if not user.is_empty():
		arguments.append("--")
		for argument in user:
			arguments.append(argument)
	return arguments


func _write_override(driver: String) -> void:
	var platform := _platform_key()
	var override := ConfigFile.new()
	if driver.begins_with("opengl3"):
		override.set_value("rendering", "renderer/rendering_method", "gl_compatibility")
		override.set_value("rendering", "gl_compatibility/driver." + platform, driver)
	else:
		override.set_value("rendering", "renderer/rendering_method", "forward_plus")
		override.set_value("rendering", "rendering_device/driver." + platform, driver)
	override.save(OVERRIDE_PATH)


func _platform_key() -> String:
	match OS.get_name():
		"Windows": return "windows"
		"macOS": return "macos"
		_: return "linuxbsd"


func _finish(outcome: String) -> void:
	_state.set_value("start", "pending", false)
	_state.set_value("start", "outcome", outcome)
	_state.set_value("start", "driver", current_driver())
	_state.save(STATE_PATH)


func _frame_is_drawn() -> bool:
	# For testing the chain on a machine whose drivers all work.
	if OS.get_cmdline_user_args().has("--boot-guard-pretend-black"):
		return false
	var texture := get_viewport().get_texture()
	if texture == null:
		return true
	var image := texture.get_image()
	if image == null or image.is_empty():
		return false
	var size := image.get_size()
	var samples := 0
	var bright := 0
	# A whole-pixel stride across roughly ninety-six by fifty-four samples: the truncation is the
	# point, so the division is written as one on purpose. As `size.x / 96` it was integer division
	# too, and Godot said so on every headless run - a warning that was correct about the arithmetic
	# and wrong about the intent, and which hid any real warning beside it.
	var step_x := maxi(1, int(size.x / 96.0))
	var step_y := maxi(1, int(size.y / 54.0))
	for y in range(0, size.y, step_y):
		for x in range(0, size.x, step_x):
			var colour := image.get_pixel(x, y)
			samples += 1
			if colour.r + colour.g + colour.b > 1.05:
				bright += 1
	return samples == 0 or float(bright) / float(samples) >= BRIGHT_SHARE


func _show_code_failure() -> void:
	var log_path := OS.get_user_data_dir().path_join("logs/godot.log")
	var text := "Deal or Force could not load its game code (.NET).\n\n" + \
		"If the game came as a folder, keep everything in it together - the .exe, the .pck and the data_ folder next to them - and check that an antivirus has not quarantined any of it.\n\n" + \
		"The log is at:\n%s\n\n" % log_path + \
		"Deal or Force не смог загрузить код игры (.NET).\n\n" + \
		"Если игра пришла папкой, держите всё вместе: .exe, .pck и папку data_ рядом с ними, и проверьте, не поместил ли антивирус что-то из этого в карантин.\n\n" + \
		"Лог лежит здесь:\n%s" % log_path
	_show_message(text)


func _show_message(text: String) -> void:
	if _overlay != null:
		return
	_overlay = CanvasLayer.new()
	_overlay.layer = 128
	add_child(_overlay)
	var back := ColorRect.new()
	back.color = Color(0.04, 0.06, 0.09)
	back.set_anchors_preset(Control.PRESET_FULL_RECT)
	_overlay.add_child(back)
	var label := Label.new()
	label.text = text
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.set_anchors_preset(Control.PRESET_FULL_RECT)
	label.offset_left = 80
	label.offset_top = 80
	label.offset_right = -80
	label.offset_bottom = -80
	label.add_theme_font_size_override("font_size", 20)
	label.add_theme_color_override("font_color", Color(0.92, 0.90, 0.84))
	_overlay.add_child(label)


## A development hook, nothing a player meets: DOF_AUTOPILOT names a GDScript file (res:// or a
## path on disk) that is added to the tree as a node, so a screenshot tour or a click-through
## can drive the game out of the live project without editing project.godot or mirroring the
## tree. It saves its own pictures from the viewport, so the window need not have focus.
func _start_autopilot() -> void:
	var path := OS.get_environment("DOF_AUTOPILOT")
	if path.is_empty():
		return
	var script := load(path) as Script
	if script == null:
		push_warning("DOF_AUTOPILOT: cannot load " + path)
		return
	var pilot: Node = script.new()
	pilot.name = "DofAutopilot"
	get_tree().root.add_child.call_deferred(pilot)
