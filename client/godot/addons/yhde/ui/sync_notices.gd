@tool
extends VBoxContainer
## Panel notices about this computer's copy of the project: files that could
## not be written here, and a report after Godot closed unexpectedly.
## dock.gd places it; main.gd calls refresh() with the session.

const Style := preload("res://addons/yhde/ui/style.gd")
const SHOWN := 5

var _session
var _unwritten: PanelContainer
var _unwritten_text: Label
var _unwritten_rows: VBoxContainer
var _crash: PanelContainer
var _crash_report := ""
var _last := ""


func _init() -> void:
	add_theme_constant_override("separation", int(Style.px(8)))
	visible = false

	var col := VBoxContainer.new()
	col.add_theme_constant_override("separation", int(Style.px(4)))
	_unwritten_text = Style.label("", false, Style.small_size())
	_unwritten_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	col.add_child(_unwritten_text)
	_unwritten_rows = VBoxContainer.new()
	_unwritten_rows.add_theme_constant_override("separation", int(Style.px(2)))
	col.add_child(_unwritten_rows)
	_unwritten = Style.panel(col, Style.card(0.05, 6, Vector4(10, 7, 10, 7), Style.warning()))
	_unwritten.visible = false
	add_child(_unwritten)

	var crash_col := VBoxContainer.new()
	crash_col.add_theme_constant_override("separation", int(Style.px(6)))
	var crash_text := Style.label("Godot closed unexpectedly last time. The report says what YHDE and Godot were doing just before.", false, Style.small_size())
	crash_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	crash_col.add_child(crash_text)
	var buttons := HFlowContainer.new()
	buttons.add_theme_constant_override("h_separation", int(Style.px(6)))
	var copy := Button.new()
	copy.text = "Copy report"
	Style.primary_button(copy)
	copy.pressed.connect(func() -> void:
		DisplayServer.clipboard_set(_crash_report)
		copy.text = "Copied")
	buttons.add_child(copy)
	var dismiss := Button.new()
	dismiss.text = "Dismiss"
	Style.secondary_button(dismiss)
	dismiss.pressed.connect(func() -> void:
		if _session:
			_session.clear_crash_report()
		_crash_report = ""
		_crash.visible = false
		_update_visible())
	buttons.add_child(dismiss)
	crash_col.add_child(buttons)
	_crash = Style.panel(crash_col, Style.card(0.05, 6, Vector4(10, 7, 10, 7), Style.danger()))
	_crash.visible = false
	add_child(_crash)


## Called with the session (or null when there is none) a few times a second.
func refresh(session) -> void:
	_session = session
	if session == null:
		_unwritten.visible = false
		_crash.visible = false
		_update_visible()
		return
	var report: String = session.get_crash_report()
	if report != _crash_report:
		_crash_report = report
		_crash.visible = report != ""
	_show_unwritten(session.get_unwritten())
	_update_visible()


func _show_unwritten(files: Array) -> void:
	_unwritten.visible = not files.is_empty()
	var key := JSON.stringify(files)
	if key == _last:
		return
	_last = key
	for c in _unwritten_rows.get_children():
		c.queue_free()
	if files.is_empty():
		return
	_unwritten_text.text = "%d file%s could not be written here. Nothing was changed for your team, and YHDE keeps trying." % [
		files.size(), "" if files.size() == 1 else "s"]
	for i in mini(files.size(), SHOWN):
		var f: Dictionary = files[i]
		var row := HBoxContainer.new()
		var text := Style.label("%s: %s" % [str(f.path).trim_prefix("res://"), f.reason], true, Style.small_size())
		text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		text.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		row.add_child(text)
		if f.get("exists", false):
			var keep := Button.new()
			keep.text = "Keep mine"
			keep.tooltip_text = "Share this computer's copy with your team instead of the shared one."
			Style.secondary_button(keep)
			var path: String = f.path
			keep.pressed.connect(func() -> void:
				if _session:
					_session.keep_local_file(path))
			row.add_child(keep)
		_unwritten_rows.add_child(row)
	if files.size() > SHOWN:
		_unwritten_rows.add_child(Style.label("and %d more" % (files.size() - SHOWN), true, Style.small_size()))


func _update_visible() -> void:
	visible = _unwritten.visible or _crash.visible
