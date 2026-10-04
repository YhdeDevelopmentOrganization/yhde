@tool
extends PopupPanel
## The floating comment card next to a pin: a thread with its replies, or the
## composer for a new comment. Enter sends, Shift+Enter makes a new line.

signal closed_thread

const Style := preload("style.gd")
const Avatar := preload("avatar.gd")
const WIDTH := 320.0

var social: Object
var thread_id := ""
var _draft: Dictionary = {} # a new comment's anchor while composing
var _title: Label
var _resolve: Button
var _list: VBoxContainer
var _scroll: ScrollContainer
var _input: TextEdit
var _send: Button
var _editing := "" # message id being edited


func _init() -> void:
	transient = true
	exclusive = false
	wrap_controls = true
	add_theme_stylebox_override("panel", Style.box(Style.color("base_color"), 10, Vector4(12, 10, 12, 12),
			Style.color("dark_color_3"), 1))
	var root := VBoxContainer.new()
	root.custom_minimum_size = Vector2(Style.px(WIDTH), 0)
	root.add_theme_constant_override("separation", int(Style.px(8)))
	add_child(root)

	var header := HBoxContainer.new()
	header.add_theme_constant_override("separation", int(Style.px(4)))
	_title = Style.label("", true, Style.small_size())
	_title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_title.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	header.add_child(_title)
	_resolve = Style.icon_button("ImportCheck", "Resolve")
	_resolve.pressed.connect(_on_resolve)
	header.add_child(_resolve)
	var close := Style.icon_button("Close", "Close")
	close.pressed.connect(func() -> void: hide())
	header.add_child(close)
	root.add_child(header)

	_scroll = ScrollContainer.new()
	_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_list = VBoxContainer.new()
	_list.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_list.add_theme_constant_override("separation", int(Style.px(10)))
	_scroll.add_child(_list)
	root.add_child(_scroll)

	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", int(Style.px(6)))
	_input = Style.text_input("Reply")
	_input.gui_input.connect(_on_input_key)
	row.add_child(_input)
	_send = Button.new()
	_send.icon = Style.icon("ArrowUp")
	_send.tooltip_text = "Send (Enter)"
	Style.primary_button(_send)
	_send.size_flags_vertical = Control.SIZE_SHRINK_END
	_send.pressed.connect(_submit)
	row.add_child(_send)
	root.add_child(row)

	popup_hide.connect(func() -> void:
		_editing = ""
		closed_thread.emit())
	# The text box only knows its height once it has wrapped at its width
	# (and it grows while typing): the card grows with it instead of cutting
	# off its bottom edge.
	root.minimum_size_changed.connect(_fit_height)


## Show an existing thread next to `at` (a position in the editor window).
func show_thread(id: String, at: Vector2) -> void:
	thread_id = id
	_draft = {}
	_editing = ""
	_input.text = ""
	_input.placeholder_text = "Reply"
	refresh()
	_open_at(at)


## Start a new comment at an anchor (from comment_pins.anchor_2d/3d).
func compose(anchor: Dictionary, at: Vector2) -> void:
	thread_id = ""
	_draft = anchor
	_editing = ""
	_input.text = ""
	_input.placeholder_text = "Add a comment. @name to mention someone"
	refresh()
	_open_at(at)


func refresh() -> void:
	for c in _list.get_children():
		c.queue_free()
	if thread_id == "":
		var where: String = _draft.get("path", "")
		_title.text = "New comment" + ("  ·  " + where if where != "" and where != "." else "")
		_resolve.visible = false
		_scroll.visible = false
		_send.tooltip_text = "Post (Enter)"
		return
	var t: Dictionary = social.threads.get(thread_id, {})
	if t.is_empty():
		hide()
		return
	var resolved: bool = t.get("resolved") != null
	var path: String = t.get("path", "")
	_title.text = "#%d  %s" % [social.number_of(thread_id), path if path != "" and path != "." else str(t.scene).get_file()]
	_resolve.visible = true
	_resolve.icon = Style.icon("Reload" if resolved else "ImportCheck")
	_resolve.tooltip_text = "Reopen" if resolved else "Resolve"
	_scroll.visible = true
	_send.tooltip_text = "Send (Enter)"
	if resolved:
		var note := Style.label("Resolved by %s" % t.resolved.by, true, Style.small_size())
		_list.add_child(note)
	for m in t.messages:
		_list.add_child(_message_row(m))
	_fit.call_deferred()


## Grow with the thread (once its text has wrapped) up to a point, then scroll.
func _fit() -> void:
	await get_tree().process_frame
	_scroll.custom_minimum_size = Vector2(0, minf(Style.px(360), _list.get_combined_minimum_size().y))
	reset_size()
	await get_tree().process_frame
	_scroll.scroll_vertical = int(_scroll.get_v_scroll_bar().max_value)


func _message_row(m: Dictionary) -> Control:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", int(Style.px(8)))
	var name := str(m.author.name)
	var a := Avatar.new()
	a.setup(social.initials_of(name), social.color_of(name), Style.px(22))
	a.mouse_default_cursor_shape = Control.CURSOR_ARROW
	a.size_flags_vertical = Control.SIZE_SHRINK_BEGIN
	row.add_child(a)
	var col := VBoxContainer.new()
	col.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	col.add_theme_constant_override("separation", int(Style.px(2)))
	var head := HBoxContainer.new()
	var who := Style.label(name)
	who.add_theme_font_override("font", Style.font(true))
	who.add_theme_font_size_override("font_size", Style.small_size())
	head.add_child(who)
	var when := Style.label("  " + Style.clock(float(m.at) / 1000.0) + ("  (edited)" if m.get("edited", false) else ""), true, Style.small_size())
	when.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	head.add_child(when)
	if str(m.author.id) == social.me:
		var more := MenuButton.new()
		more.icon = Style.icon("GuiTabMenuHl")
		more.flat = true
		more.tooltip_text = "Edit or delete"
		more.get_popup().add_item("Edit", 0)
		more.get_popup().add_item("Delete", 1)
		var id := str(m.id)
		var body := str(m.body)
		more.get_popup().id_pressed.connect(func(which: int) -> void:
			if which == 0:
				_editing = id
				_input.text = body
				_input.placeholder_text = "Edit your comment"
				_input.grab_focus()
			else:
				social.delete_comment(id))
		head.add_child(more)
	col.add_child(head)
	col.add_child(body_label(str(m.body), social))
	row.add_child(col)
	return row


static func body_label(text: String, social_state: Object) -> RichTextLabel:
	var l := RichTextLabel.new()
	l.bbcode_enabled = true
	l.fit_content = true
	l.scroll_active = false
	l.selection_enabled = true
	l.context_menu_enabled = true
	l.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	l.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	l.text = highlight_mentions(text, social_state)
	return l


## @mentions in bold accent color (yours stand out more); everything else escaped.
static func highlight_mentions(text: String, social_state: Object) -> String:
	var out := ""
	var accent := Style.color("accent_color").to_html(false)
	for word in text.replace("[", "[lb]").split(" "):
		if word.begins_with("@") and word.length() > 1:
			var mine: bool = social_state != null and social_state.mentions_me(word)
			out += "[b][color=#%s]%s[/color][/b] " % [Style.warning().to_html(false) if mine else accent, word]
		else:
			out += word + " "
	return out.strip_edges(false, true)


func _open_at(window_pos: Vector2) -> void:
	reset_size()
	var size := get_contents_minimum_size() + Vector2(Style.px(24), Style.px(22))
	var at := window_pos + Vector2(Style.px(RADIUS_GAP), -Style.px(12))
	# Embedded (single-window mode) popups are placed in the editor window;
	# separate ones on the screen, next to the same spot. Either way the card
	# stays fully visible.
	var editor_window := EditorInterface.get_base_control().get_window()
	var bounds := Rect2(Vector2.ZERO, Vector2(editor_window.size))
	if not is_embedded():
		at += Vector2(editor_window.position)
		bounds = Rect2(DisplayServer.screen_get_usable_rect(editor_window.current_screen))
	at.x = clampf(at.x, bounds.position.x, bounds.end.x - size.x)
	at.y = clampf(at.y, bounds.position.y, bounds.end.y - size.y - Style.px(200))
	popup(Rect2i(Vector2i(at), Vector2i(size)))
	_input.grab_focus()


func _fit_height() -> void:
	if not visible:
		return
	var want := int(ceilf(get_contents_minimum_size().y + Style.px(22)))
	if size.y != want:
		size = Vector2i(size.x, want)


const RADIUS_GAP := 34.0


func _on_input_key(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo:
		if event.keycode == KEY_ENTER or event.keycode == KEY_KP_ENTER:
			if not event.shift_pressed:
				_input.accept_event()
				_submit()
		elif event.keycode == KEY_ESCAPE:
			_input.accept_event()
			hide()


func _submit() -> void:
	var text := _input.text.strip_edges()
	if text == "":
		return
	var ok := false
	if _editing != "":
		ok = social.edit_comment(_editing, text)
		_editing = ""
	elif thread_id == "":
		ok = social.create_comment(_draft.scene, _draft.node_id, _draft.path, _draft.anchor, text)
		if ok:
			hide() # the new thread arrives from the server and shows its pin
	else:
		ok = social.reply(thread_id, text)
	if ok:
		_input.text = ""
		_input.placeholder_text = "Reply"


func _on_resolve() -> void:
	var t: Dictionary = social.threads.get(thread_id, {})
	if t.is_empty():
		return
	var resolving: bool = t.get("resolved") == null
	social.set_resolved(thread_id, resolving)
	if resolving:
		hide()


