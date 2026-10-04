@tool
extends HBoxContainer
## Top-bar presence strip: overlapping avatars of everyone in the project, the
## comment tool, and (only when something is not right) the connection state.
## Click an avatar to follow that person, right-click to message them.

signal follow_requested(peer_id: String)
signal message_requested(member_id: String)
signal comment_mode_requested
signal status_pressed

const Style := preload("style.gd")
const Avatar := preload("avatar.gd")
const MAX_VISIBLE := 5

var _avatars: HBoxContainer
var _comment: Button
var _status: Button
var _state := Style.STATE_OFFLINE
var _state_text := "Offline"


func _init() -> void:
	name = "YhdeTopBar"
	alignment = BoxContainer.ALIGNMENT_END
	add_theme_constant_override("separation", int(Style.px(8)))

	_avatars = HBoxContainer.new()
	_avatars.add_theme_constant_override("separation", int(Style.px(-6)))
	_avatars.alignment = BoxContainer.ALIGNMENT_END
	add_child(_avatars)

	_comment = Button.new()
	_comment.icon = Style.icon(Style.COMMENT_ICON)
	_comment.tooltip_text = "Comment (C): click in the 2D or 3D view to leave a comment on it"
	_comment.toggle_mode = true
	Style.quiet_button(_comment)
	_comment.add_theme_font_size_override("font_size", Style.small_size())
	_comment.pressed.connect(func() -> void: comment_mode_requested.emit())
	_comment.visible = false
	add_child(_comment)

	# The YHDE mark opens the panel. The connection state is written next to
	# it only when something is not right: being connected needs no label.
	_status = Button.new()
	_status.icon = Style.logo()
	Style.quiet_button(_status)
	Style.untinted_icon(_status)
	_status.add_theme_font_size_override("font_size", Style.small_size())
	_status.pressed.connect(func() -> void: status_pressed.emit())
	add_child(_status)
	set_status(Style.STATE_OFFLINE, "Offline")


func _notification(what: int) -> void:
	# The logo's ring and the state color follow the editor theme.
	if what == NOTIFICATION_THEME_CHANGED and _status:
		_status.icon = Style.logo()
		set_status(_state, _state_text)


func set_status(state: int, text: String) -> void:
	_state = state
	_state_text = text
	var live := state == Style.STATE_LIVE
	_comment.visible = live
	_status.text = "" if live else (text if state != Style.STATE_OFFLINE else "Offline")
	_status.tooltip_text = "YHDE: %s. Open the YHDE panel" % ("connected" if live else _status.text.to_lower())
	_status.add_theme_color_override("font_color", Style.state_color(state) if state != Style.STATE_OFFLINE else Style.muted())


func set_comment_state(on: bool, unread: int) -> void:
	_comment.set_pressed_no_signal(on)
	_comment.text = str(unread) if unread > 0 else ""
	var c := Style.color("accent_color") if on or unread > 0 else Style.muted()
	for key in ["icon_normal_color", "icon_hover_color", "icon_pressed_color"]:
		_comment.add_theme_color_override(key, c)


func set_peers(peers: Array, me: Dictionary, following: String) -> void:
	for child in _avatars.get_children():
		child.queue_free()

	var d := Style.px(24)
	var shown := 0
	for p in peers:
		if shown >= MAX_VISIBLE:
			break
		var a := Avatar.new()
		a.setup(p.initials, p.color, d)
		var where: String = p.scene_name if p.scene_name != "" else "no scene"
		a.tooltip_text = "%s\n%s · %s\nClick to %s · right-click to message" % [p.name, where, p.tool, "stop following" if p.following else "follow"]
		if p.following:
			a.ring = Style.color("accent_color")
		var id: String = p.id
		var member: String = p.get("member", "")
		a.pressed.connect(func() -> void: follow_requested.emit(id))
		a.gui_input.connect(func(e: InputEvent) -> void:
			if e is InputEventMouseButton and e.pressed and e.button_index == MOUSE_BUTTON_RIGHT and member != "":
				message_requested.emit(member))
		_avatars.add_child(a)
		shown += 1

	var hidden := peers.size() - shown
	if hidden > 0:
		var more := Avatar.new()
		more.setup("+%d" % hidden, Style.color("dark_color_3"), d)
		more.tooltip_text = "%d more" % hidden
		more.pressed.connect(func() -> void: status_pressed.emit())
		_avatars.add_child(more)

	if not me.is_empty() and me.get("id", "") != "":
		var you := Avatar.new()
		you.setup(me.initials, me.color, d)
		you.tooltip_text = "%s (you)" % me.name
		you.mouse_default_cursor_shape = Control.CURSOR_ARROW
		you.pressed.connect(func() -> void: status_pressed.emit())
		var spacer := Control.new()
		spacer.custom_minimum_size = Vector2(Style.px(18), 0) # net 6px after the overlap
		if shown > 0:
			_avatars.add_child(spacer)
		_avatars.add_child(you)
