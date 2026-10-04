@tool
extends EditorPlugin
## YHDE editor plugin: entry point and version gate.
##
## This file only checks that this editor can run YHDE, then hands over to
## main.gd. It is written to load on any Godot 4.x: if the rest of YHDE (made
## for 4.7) were loaded by an older editor, Godot would only print "Unable to
## load addon script" and disable it. This way people are told what to do.

const REQUIRED := [4, 7]
const TESTED := "4.7.2"
const DOWNLOAD := "https://godotengine.org/download/archive/4.7.2-stable/"

var _main: Object # main.gd, once the version is right
var _problem: Control


func _enter_tree() -> void:
	var v := Engine.get_version_info()
	if v.major != REQUIRED[0] or v.minor < REQUIRED[1]:
		_explain("YHDE needs Godot %s" % TESTED,
			"This editor is Godot %s, but YHDE is made for Godot %d.%d (tested on %s). Open this project with Godot %s; you can download it from %s.\n\nYour project is fine. Nothing was changed." % [
				v.string, REQUIRED[0], REQUIRED[1], TESTED, TESTED, DOWNLOAD])
		return
	var script: Script = load("res://addons/yhde/main.gd")
	if script == null or not script.can_instantiate():
		_explain("YHDE could not start",
			"The add-on's files are incomplete or damaged (addons/yhde/main.gd did not load). Copy the addons/yhde folder again from the YHDE release, then re-enable the plugin in Project > Project Settings > Plugins.")
		return
	_main = script.new(self)
	_main.enter()


func _exit_tree() -> void:
	if _main:
		_main.exit()
		_main = null
	if _problem:
		remove_control_from_docks(_problem)
		_problem.queue_free()
		_problem = null


func _process(delta: float) -> void:
	if _main:
		_main.process(delta)


# The 2D editor only forwards input to plugins that edit the selected node
# (unlike 3D, where forwarding can be always on), so 2D pointer, comment clicks
# and the comment key are read here, before the 2D editor sees them.
func _input(event: InputEvent) -> void:
	if _main and _main.input_2d(event):
		get_viewport().set_input_as_handled()


func _forward_3d_gui_input(camera: Camera3D, event: InputEvent) -> int:
	return _main.forward_3d_gui_input(camera, event) if _main else 0


func _forward_canvas_force_draw_over_viewport(overlay: Control) -> void:
	if _main:
		_main.draw_2d(overlay)


func _forward_3d_force_draw_over_viewport(overlay: Control) -> void:
	if _main:
		_main.draw_3d(overlay)


## Tell the person, in the Output panel, a dialog and a dock, and stay idle.
func _explain(title: String, message: String) -> void:
	push_error("YHDE: %s. %s" % [title, message])
	var label := Label.new()
	label.name = "YHDE"
	label.text = "%s\n\n%s" % [title, message]
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.custom_minimum_size = Vector2(200, 0)
	_problem = label
	# add_control_to_dock works on every 4.x (4.7 prefers add_dock).
	add_control_to_dock(DOCK_SLOT_RIGHT_UL, label)
	var dialog := AcceptDialog.new()
	dialog.title = title
	dialog.dialog_text = message
	dialog.dialog_autowrap = true
	dialog.min_size = Vector2i(520, 0)
	dialog.confirmed.connect(dialog.queue_free)
	dialog.canceled.connect(dialog.queue_free)
	get_editor_interface().get_base_control().add_child(dialog) # works on every 4.x
	dialog.popup_centered.call_deferred()
