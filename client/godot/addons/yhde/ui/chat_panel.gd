@tool
extends VBoxContainer
## Chat: the project channel and direct messages. Enter sends, Shift+Enter
## makes a new line, @name mentions someone.

const Style := preload("style.gd")
const Avatar := preload("avatar.gd")
const CommentPopup := preload("comment_popup.gd")
const PROJECT := "project"

var social: Object
var _conversation := PROJECT
var _picker: OptionButton
var _older: Button
var _scroll: ScrollContainer
var _list: VBoxContainer
var _empty: Label
var _input: TextEdit
var _dirty := false
var _stick_to_bottom := true


func _init() -> void:
	add_theme_constant_override("separation", int(Style.px(6)))

	var top := HBoxContainer.new()
	top.add_theme_constant_override("separation", int(Style.px(4)))
	_picker = OptionButton.new()
	_picker.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_picker.fit_to_longest_item = false
	_picker.item_selected.connect(func(i: int) -> void: open(str(_picker.get_item_metadata(i))))
	top.add_child(_picker)
	_older = Style.icon_button("ArrowUp", "Load older messages")
	_older.pressed.connect(func() -> void:
		_stick_to_bottom = false
		social.load_older(_conversation))
	top.add_child(_older)
	add_child(top)

	_scroll = ScrollContainer.new()
	_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_list = VBoxContainer.new()
	_list.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_list.add_theme_constant_override("separation", int(Style.px(2)))
	_scroll.add_child(_list)
	add_child(_scroll)
	_empty = Style.label("", true, Style.small_size())
	_empty.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_list.add_child(_empty)

	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", int(Style.px(6)))
	_input = Style.text_input("Message the team")
	_input.gui_input.connect(_on_key)
	row.add_child(_input)
	var send := Button.new()
	send.icon = Style.icon("ArrowUp")
	send.tooltip_text = "Send (Enter)"
	send.size_flags_vertical = Control.SIZE_SHRINK_END
	Style.primary_button(send)
	send.pressed.connect(_send)
	row.add_child(send)
	add_child(row)


func setup(s: Object) -> void:
	social = s
	social.chat_changed.connect(func(c: String) -> void:
		if c == _conversation or c == PROJECT:
			_dirty = true
		_rebuild_picker())
	_rebuild_picker()


## The tab became visible.
func shown() -> void:
	_rebuild_picker()
	_render()
	social.mark_read(_conversation)
	_input.grab_focus.call_deferred()


func open(conversation: String) -> void:
	_conversation = conversation
	_stick_to_bottom = true
	_rebuild_picker()
	_render()
	if is_visible_in_tree():
		social.mark_read(_conversation)
		_input.grab_focus.call_deferred()


func _process(_delta: float) -> void:
	if _dirty and is_visible_in_tree():
		_dirty = false
		_render()
		social.mark_read(_conversation)


func _rebuild_picker() -> void:
	if social == null:
		return
	_picker.clear()
	_add_choice("# Project", PROJECT)
	var seen := {PROJECT: true}
	for c in social.conversations():
		_add_choice("@ " + str(c.name), str(c.id))
		seen[str(c.id)] = true
	if not seen.has(_conversation):
		_add_choice("@ " + social.name_of(_conversation), _conversation)
	for i in _picker.item_count:
		if str(_picker.get_item_metadata(i)) == _conversation:
			_picker.select(i)


func _add_choice(label: String, key: String) -> void:
	var n: int = social.unread(key) if key != _conversation else 0
	_picker.add_item(label + Style.badge_text(n))
	_picker.set_item_metadata(_picker.item_count - 1, key)


func _render() -> void:
	if social == null:
		return
	for c in _list.get_children():
		if c != _empty:
			c.queue_free()
	var messages: Array = social.messages(_conversation)
	_older.visible = social.has_more_history(_conversation) and not messages.is_empty()
	_empty.visible = messages.is_empty()
	_empty.text = "No messages yet. Say hi to your team!" if _conversation == PROJECT \
		else "This is the start of your conversation with %s. Only the two of you see it." % social.name_of(_conversation)
	_input.placeholder_text = "Message the team" if _conversation == PROJECT else "Message %s" % social.name_of(_conversation)
	var last_author := ""
	var last_at := 0.0
	for m in messages:
		var author := str(m.from.id)
		var at := float(m.at) / 1000.0
		# A new block when someone else speaks, or after a pause.
		if author != last_author or at - last_at > 300.0:
			_list.add_child(_header(m))
		last_author = author
		last_at = at
		var body := CommentPopup.body_label(str(m.body), social)
		body.add_theme_constant_override("margin_left", 0)
		var indent := MarginContainer.new()
		indent.add_theme_constant_override("margin_left", int(Style.px(30)))
		indent.add_child(body)
		_list.add_child(indent)
	if _stick_to_bottom:
		_scroll_to_end.call_deferred()
	_stick_to_bottom = true


func _header(m: Dictionary) -> Control:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", int(Style.px(8)))
	var spacer := Control.new()
	spacer.custom_minimum_size = Vector2(0, Style.px(4))
	var name := str(m.from.name)
	var a := Avatar.new()
	a.setup(social.initials_of(name), social.color_of(name), Style.px(22))
	a.mouse_default_cursor_shape = Control.CURSOR_ARROW
	row.add_child(a)
	var who := Style.label(name)
	who.add_theme_font_override("font", Style.font(true))
	who.add_theme_font_size_override("font_size", Style.small_size())
	row.add_child(who)
	row.add_child(Style.label(Style.clock(float(m.at) / 1000.0), true, Style.small_size()))
	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", 0)
	box.add_child(spacer)
	box.add_child(row)
	return box


func _scroll_to_end() -> void:
	await get_tree().process_frame
	_scroll.scroll_vertical = int(_scroll.get_v_scroll_bar().max_value)


func _on_key(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo:
		if (event.keycode == KEY_ENTER or event.keycode == KEY_KP_ENTER) and not event.shift_pressed:
			_input.accept_event()
			_send()


func _send() -> void:
	var text := _input.text.strip_edges()
	if text == "" or social == null:
		return
	if social.send_chat(_conversation, text):
		_input.text = ""
		_stick_to_bottom = true
