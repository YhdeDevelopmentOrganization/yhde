@tool
extends VBoxContainer
## Comments: every thread of the scene in front (or of all scenes), newest
## activity visible at a glance. Click one to jump to its pin.

signal focus_requested(thread_id: String)
signal comment_mode_requested

const Style := preload("style.gd")
const Avatar := preload("avatar.gd")
const CommentPopup := preload("comment_popup.gd")

var social: Object
var show_resolved := false
var _scene := ""
var _all_scenes := false
var _new_button: Button
var _scope: OptionButton
var _resolved_check: CheckBox
var _list: VBoxContainer
var _empty: Label
var _dirty := false


func _init() -> void:
	add_theme_constant_override("separation", int(Style.px(6)))
	var top := HBoxContainer.new()
	top.add_theme_constant_override("separation", int(Style.px(6)))
	_new_button = Button.new()
	_new_button.text = "Comment"
	_new_button.icon = Style.icon("Add")
	_new_button.tooltip_text = "Click in the 2D or 3D view to place a comment (shortcut: C)"
	Style.primary_button(_new_button)
	_new_button.pressed.connect(func() -> void: comment_mode_requested.emit())
	top.add_child(_new_button)
	_scope = OptionButton.new()
	_scope.add_item("This scene")
	_scope.add_item("All scenes")
	_scope.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_scope.item_selected.connect(func(i: int) -> void:
		_all_scenes = i == 1
		refresh())
	top.add_child(_scope)
	add_child(top)
	_resolved_check = CheckBox.new()
	_resolved_check.text = "Show resolved"
	_resolved_check.add_theme_font_size_override("font_size", Style.small_size())
	_resolved_check.toggled.connect(func(on: bool) -> void:
		show_resolved = on
		refresh())
	add_child(_resolved_check)

	var scroll := ScrollContainer.new()
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_list = VBoxContainer.new()
	_list.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_list.add_theme_constant_override("separation", int(Style.px(6)))
	scroll.add_child(_list)
	add_child(scroll)
	_empty = Style.label("", true, Style.small_size())
	_empty.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART


func setup(s: Object) -> void:
	social = s


func shown() -> void:
	refresh()
	social.mark_comments_read()


func set_scene(path: String) -> void:
	_scene = path
	refresh()


func set_comment_mode(on: bool) -> void:
	_new_button.text = "Click in the view..." if on else "Comment"
	if on:
		Style.secondary_button(_new_button)
	else:
		Style.primary_button(_new_button)


func refresh() -> void:
	_dirty = true


func _process(_delta: float) -> void:
	if _dirty and is_visible_in_tree():
		_dirty = false
		_render()
		social.mark_comments_read()


func _render() -> void:
	if social == null:
		return
	for c in _list.get_children():
		c.queue_free()
	var threads: Array = social.thread_list("" if _all_scenes else _scene, show_resolved)
	threads.reverse() # newest first
	if threads.is_empty():
		_empty = Style.label("", true, Style.small_size())
		_empty.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		if _scene == "" and not _all_scenes:
			_empty.text = "Open a scene to see its comments."
		else:
			_empty.text = "No comments here yet. Press C in the 2D or 3D view (or click Comment), then click on what you want to talk about. Comments stick to the node you click."
		_list.add_child(_empty)
		return
	for t in threads:
		_list.add_child(_thread_row(t))


func _thread_row(t: Dictionary) -> Control:
	var messages: Array = t.messages
	var first: Dictionary = messages[0]
	var last: Dictionary = messages[-1]
	var resolved: bool = t.get("resolved") != null

	var col := VBoxContainer.new()
	col.add_theme_constant_override("separation", int(Style.px(3)))
	var head := HBoxContainer.new()
	head.add_theme_constant_override("separation", int(Style.px(6)))
	var number := Avatar.new()
	var author := str(t.author.name)
	number.setup(str(social.number_of(str(t.id))), social.color_of(author), Style.px(20))
	number.mouse_filter = Control.MOUSE_FILTER_IGNORE
	head.add_child(number)
	var who := Style.label(author)
	who.add_theme_font_override("font", Style.font(true))
	who.add_theme_font_size_override("font_size", Style.small_size())
	head.add_child(who)
	var when := Style.label(Style.relative_time(float(last.at) / 1000.0), true, Style.small_size())
	when.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	head.add_child(when)
	if resolved:
		head.add_child(Style.label("Resolved", true, Style.small_size()))
	col.add_child(head)
	var preview := Style.label(str(first.body).replace("\n", " "), resolved, Style.small_size())
	preview.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	preview.max_lines_visible = 3
	col.add_child(preview)
	var meta := PackedStringArray()
	var path: String = t.get("path", "")
	if _all_scenes:
		meta.append(str(t.scene).get_file())
	if path != "" and path != ".":
		meta.append(path)
	if messages.size() > 1:
		meta.append("%d repl%s" % [messages.size() - 1, "y" if messages.size() == 2 else "ies"])
	if not meta.is_empty():
		var m := Style.label("  ·  ".join(meta), true, Style.small_size())
		m.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
		col.add_child(m)

	var card := Style.panel(col, Style.card(0.035 if not resolved else 0.015, 8, Vector4(10, 8, 10, 8)))
	card.mouse_filter = Control.MOUSE_FILTER_STOP
	card.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	card.tooltip_text = "Show in the scene"
	var id := str(t.id)
	card.gui_input.connect(func(e: InputEvent) -> void:
		if e is InputEventMouseButton and e.pressed and e.button_index == MOUSE_BUTTON_LEFT:
			focus_requested.emit(id))
	for c in [col, head, preview]:
		c.mouse_filter = Control.MOUSE_FILTER_PASS
	return card
