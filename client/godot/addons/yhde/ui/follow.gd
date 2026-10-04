@tool
extends RefCounted
## Follow mode: mirror another person's scene, editor screen and view.
## Any click or key press in the viewport hands control back to you.
## The view glides after theirs instead of jumping with every update.

const Smooth := preload("smooth.gd")

var session: Object
var main_screen := "2D"

var _opened_scene := ""
var _last_2d := Vector3.INF # (center.x, center.y, zoom)
var _camera: Camera3D
var _previous_camera: Camera3D


func update() -> void:
	if session == null:
		return
	var view: Dictionary = session.get_follow_view()
	if view.is_empty():
		stop()
		return

	var scene: String = view.get("scene", "")
	var root := EditorInterface.get_edited_scene_root()
	var current := root.scene_file_path if root else ""
	if scene != "" and scene != current and scene != _opened_scene and ResourceLoader.exists(scene):
		_opened_scene = scene
		EditorInterface.open_scene_from_path(scene)
		return

	var screen: String = view.get("tool", "")
	if (screen == "2D" or screen == "3D") and screen != main_screen:
		EditorInterface.set_main_screen_editor(screen)
		return

	if view.has("center"):
		_release_camera()
		_apply_2d(Smooth.v2("follow2", view.center), Smooth.number("followz", view.zoom))
	elif view.has("camera"):
		_apply_3d(Smooth.xf3("follow3", view.camera), Smooth.number("followf", view.get("fov", 75.0)))


func stop() -> void:
	_release_camera()
	_opened_scene = ""
	_last_2d = Vector3.INF


func _canvas_item_editor() -> Node:
	var n: Node = EditorInterface.get_editor_viewport_2d()
	while n and n.get_class() != "CanvasItemEditor":
		n = n.get_parent()
	return n


func _apply_2d(center: Vector2, zoom: float) -> void:
	var key := Vector3(center.x, center.y, zoom)
	if key.is_equal_approx(_last_2d):
		return
	_last_2d = key
	var editor = _canvas_item_editor() # CanvasItemEditor (editor-internal class)
	if editor == null:
		return
	var widgets: Array = editor.find_children("*", "EditorZoomWidget", true, false)
	if not widgets.is_empty() and zoom > 0.0:
		var w = widgets[0] # EditorZoomWidget
		if not is_equal_approx(w.get_zoom(), zoom):
			w.set_zoom(zoom)
			w.emit_signal("zoom_changed", zoom)
	editor.center_at(center)


func _apply_3d(xf: Transform3D, fov: float) -> void:
	var vp := EditorInterface.get_editor_viewport_3d(0)
	if vp == null:
		return
	if _camera == null or not is_instance_valid(_camera):
		_previous_camera = vp.get_camera_3d()
		_camera = Camera3D.new()
		_camera.name = "YhdeFollowCamera"
		if _previous_camera:
			_camera.near = _previous_camera.near
			_camera.far = _previous_camera.far
		vp.add_child(_camera)
	_camera.fov = fov
	_camera.global_transform = xf
	if not _camera.current:
		_camera.make_current()


func _release_camera() -> void:
	if _camera and is_instance_valid(_camera):
		_camera.queue_free()
	_camera = null
	if _previous_camera and is_instance_valid(_previous_camera):
		_previous_camera.make_current()
	_previous_camera = null
