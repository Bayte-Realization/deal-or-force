@tool
extends EditorPlugin

# Keep native Ziva in its functioning side-dock mode. This GDScript plugin supplies
# an explicit EditorDock registration, avoiding the old async main-screen path.
# The live native panel/browser is reused, not reloaded or duplicated.
var _panel: Control
var _native_dock: EditorDock
var _central_dock: EditorDock
var _attempts: int = 0
var _elapsed: float = 0.0

func _enter_tree() -> void:
	EditorInterface.get_editor_settings().set_setting("ziva/ui/use_main_screen_tab", false)
	set_process(true)
	call_deferred("_attach_panel")

func _process(delta: float) -> void:
	_elapsed += delta
	if _elapsed < 0.25:
		return
	_elapsed = 0.0
	_attempts += 1
	if _attach_panel() or _attempts >= 40:
		set_process(false)

func _attach_panel() -> bool:
	if is_instance_valid(_panel):
		return true
	# Native and legacy central docks can share a title. Resolve by native class,
	# not by that ambiguous title, and retain the actual owning dock for rollback.
	var pending: Array[Node] = [EditorInterface.get_base_control()]
	var visited: int = 0
	while not pending.is_empty() and visited < 12000:
		var candidate: Node = pending.pop_back()
		visited += 1
		if candidate.get_class() == "DockZiva" and candidate.get_parent() is EditorDock:
			_panel = candidate as Control
			_native_dock = candidate.get_parent() as EditorDock
			break
		pending.append_array(candidate.get_children())
	if not is_instance_valid(_panel):
		return false
	_central_dock = EditorDock.new()
	_central_dock.title = "Ziva AI"
	_central_dock.layout_key = "DealOrForceZivaCentral"
	_central_dock.available_layouts = EditorDock.DOCK_LAYOUT_MAIN_SCREEN
	_central_dock.default_slot = EditorDock.DOCK_SLOT_MAIN_SCREEN
	_central_dock.dock_icon = _get_plugin_icon()
	_panel.reparent(_central_dock, false)
	_panel.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_panel.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_native_dock.close()
	_panel.show()
	add_dock(_central_dock)
	return true

func _has_main_screen() -> bool:
	# Main workspace is registered explicitly through 4.8's EditorDock API.
	return false

func _get_plugin_name() -> String:
	return "Ziva AI"

func _get_plugin_icon() -> Texture2D:
	return load("res://addons/ziva_agent/icon.svg") as Texture2D

func _exit_tree() -> void:
	set_process(false)
	if is_instance_valid(_panel) and is_instance_valid(_native_dock):
		_panel.reparent(_native_dock, false)
		_panel.show()
		_native_dock.open()
		_native_dock.make_visible()
	if is_instance_valid(_central_dock):
		remove_dock(_central_dock)
		_central_dock.queue_free()
	_central_dock = null
	_panel = null
