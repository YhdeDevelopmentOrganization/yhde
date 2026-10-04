@tool
extends MarginContainer
## The YHDE dock.
##
##   header      you, where you are connected, one button to leave
##   messages    what is wrong and what to do (only when something is)
##   sign-in     your account, or an invite code (only while offline)
##   files       transfers in progress, deletions waiting for a decision
##   tabs        People · Chat · Comments · Activity

signal sign_in_requested
signal sign_in_cancelled
signal sign_in_page_requested
signal sign_out_requested
signal project_chosen(project: Dictionary)
signal projects_refresh_requested
signal site_requested(path: String)
signal invite_connect_requested
signal project_create_requested(project_name: String)
signal project_link_requested(project_id: String)
signal invite_requested(project_id: String, email: String)
signal invite_cancel_requested(project_id: String, invite_id: String)
signal disconnect_requested
signal follow_requested(peer_id: String)
signal summon_requested
signal update_requested
signal restart_requested
signal jump_requested(scene_path: String)
signal builtin_scripts_toggled(enabled: bool)
signal trust_code_toggled(enabled: bool)
signal comment_mode_requested
signal comment_focus_requested(thread_id: String)
signal deletions_confirmed
signal deletions_restored
signal code_accepted
signal code_trusted
signal join_confirmed
signal join_declined
signal options_changed(changed: Dictionary)

const Style := preload("style.gd")
const Avatar := preload("avatar.gd")
const ChatPanel := preload("chat_panel.gd")
const CommentsPanel := preload("comments_panel.gd")
const MAX_ACTIVITY := 40
## The options menu: what you want to see of others.
const OPTION_ITEMS := [
	["cursors", "Cursors and selections in 2D / 3D"],
	["scene_tabs", "Who is in which scene (scene tabs)"],
	["scene_tree", "What others selected (Scene tree)"],
	["filesystem", "Who has which file open (FileSystem)"],
	["script_list", "Who is in which script (script list)"],
	["code_carets", "Others' carets while typing (scripts)"],
	["smooth", "Smooth motion"],
]
## What others see of you (same menu, own section).
const SHARE_ITEMS := [
	["share_cursor", "Show my cursor to others"],
]
## Which notifications pop up (same menu, own section). Warnings and errors
## always show.
const NOTIFY_ITEMS := [
	["notify_dm", "Direct messages"],
	["notify_mention", "Mentions in the chat"],
	["notify_comment", "Mentions in comments"],
	["notify_summon", "Someone brings everyone to their view"],
	["notify_sync", "Sync info (files, saves)"],
]
const TABS := ["People", "Chat", "Comments", "Activity", "Team"]

var _you_avatar
var _you_name: Label
var _status_detail: Label
var _state_chip: Label
var _leave_button: Button
var _options_button: MenuButton
var _options := {}
var _project := ""
var _join: PanelContainer
var _join_text: Label
var _problem: PanelContainer
var _problem_text: Label
var _notice: PanelContainer
var _update: PanelContainer
var _update_text: Label
var _update_button: Button
var _restart_button: Button
var _notice_text: Label
var _hint: Label
var _signin: PanelContainer
var _signed_out: VBoxContainer
var _signin_button: Button
var _waiting: VBoxContainer
var _waiting_code: Label
var _invite_button: Button
var _signed_in: VBoxContainer
var _projects_title: Label
var _grid: HFlowContainer
var _grid_empty: Label
var _grid_action: Button
var _account_line: Label
var _account := {}
var _projects: Array = []
var _folder_project := ""
var _owner := false # can make projects (a beta access code)
var _dashboard := {}
var _storage_note: Label # warns before the owner's storage runs out
var _new_button: Button
var _new_row: HBoxContainer
var _new_name: LineEdit
var _team_box: VBoxContainer
var _team_page: VBoxContainer # the Team tab; the projects and team live here while connected
var _signin_col: VBoxContainer
var _team_title: Label
var _team_sub: Label
var _members: VBoxContainer
var _invite_row: HBoxContainer
var _invite_email: LineEdit
var _pending: VBoxContainer
# The project and branch this folder belongs to. Not shown: the folder
# remembers them after its first connection.
var _project_id := ""
var _branch_id := ""
var _scripts_check: CheckBox
var _trust_check: CheckBox
var _advanced_button: Button
var _advanced: VBoxContainer
var _files: PanelContainer
var _files_text: Label
var _held: VBoxContainer
var _held_text: Label
var _held_list: Label
var _code: VBoxContainer
var _code_text: Label
var _code_list: Label
var _tabs: TabBar
var _pages: Array[Control] = []
var _people: VBoxContainer
var _people_empty: Label
var _summon: Button
var _chat
var _comments
var _activity: VBoxContainer
var _activity_empty: Label
var _footer: Label
var _state := 0
var _unread_chat := 0
var _unread_comments := 0
var _activity_entries: Array[Dictionary] = []
var _activity_dirty := false
var _since_render := 0.0
var _time_refresh := 0.0


func _init() -> void:
	name = "YHDE"
	add_theme_constant_override("margin_left", int(Style.px(10)))
	add_theme_constant_override("margin_right", int(Style.px(10)))
	add_theme_constant_override("margin_top", int(Style.px(8)))
	add_theme_constant_override("margin_bottom", int(Style.px(6)))

	# Everything scrolls inside the dock, so the panel never asks Godot for
	# more height than the dock has. (Wrapped text in a panel that isn't the
	# visible tab is measured at almost no width, one letter per line: the
	# panel then wanted ~1,900 px and pushed the other docks around.)
	var scroll := ScrollContainer.new()
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	add_child(scroll)
	var root := VBoxContainer.new()
	root.add_theme_constant_override("separation", int(Style.px(8)))
	root.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	root.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.add_child(root)

	root.add_child(_build_header())
	_problem = _message_card(Style.danger())
	_problem_text = _problem.get_child(0) as Label
	_problem.tooltip_text = "Click to dismiss"
	_problem.mouse_filter = Control.MOUSE_FILTER_STOP
	_problem.gui_input.connect(func(e: InputEvent) -> void:
		if e is InputEventMouseButton and e.pressed and e.button_index == MOUSE_BUTTON_LEFT:
			_problem.visible = false)
	root.add_child(_problem)
	_notice = _message_card(Style.warning())
	_notice_text = _notice.get_child(0) as Label
	root.add_child(_notice)
	root.add_child(_build_update())
	root.add_child(_build_join_check())
	_hint = Style.label("", true, Style.small_size())
	_hint.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_hint.visible = false
	root.add_child(_hint)
	_signin = _build_signin()
	root.add_child(_signin)
	_files = _build_files()
	root.add_child(_files)

	_tabs = TabBar.new()
	# Tabs scroll rather than making the dock wider than the person wants.
	_tabs.clip_tabs = true
	_tabs.tab_alignment = TabBar.ALIGNMENT_LEFT
	for t in TABS:
		_tabs.add_tab(t)
	_tabs.tab_changed.connect(_on_tab_changed)
	root.add_child(_tabs)

	var pages := Control.new()
	pages.size_flags_vertical = Control.SIZE_EXPAND_FILL
	pages.clip_contents = true
	root.add_child(pages)
	_pages.append(_build_people())
	_chat = ChatPanel.new()
	_pages.append(_chat)
	_comments = CommentsPanel.new()
	_comments.focus_requested.connect(func(id: String) -> void: comment_focus_requested.emit(id))
	_comments.comment_mode_requested.connect(func() -> void: comment_mode_requested.emit())
	_pages.append(_comments)
	_pages.append(_build_activity())
	var team_scroll := ScrollContainer.new()
	team_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_team_page = VBoxContainer.new()
	_team_page.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	team_scroll.add_child(_team_page)
	_pages.append(team_scroll)
	for p in _pages:
		p.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
		pages.add_child(p)
	_on_tab_changed(0)

	_footer = Style.label("", true, Style.small_size())
	_footer.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	_footer.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	root.add_child(_footer)
	set_state(Style.STATE_OFFLINE, "Offline", "Not connected")
	_let_text_wrap(self)


## Button texts wrap onto more lines in a narrow dock, so the panel is never
## wider than the dock Godot gives it (it would push the other docks).
static func _let_text_wrap(node: Node) -> void:
	for b in node.find_children("*", "Button", true, false):
		if b is Button and not (b is MenuButton) and (b as Button).text.contains(" ") and b.get_parent() is VBoxContainer:
			(b as Button).autowrap_mode = TextServer.AUTOWRAP_WORD_SMART


## A row of buttons that wraps onto more lines when the dock is narrow.
static func _button_row() -> HFlowContainer:
	var row := HFlowContainer.new()
	row.add_theme_constant_override("h_separation", int(Style.px(6)))
	row.add_theme_constant_override("v_separation", int(Style.px(4)))
	return row


# Building

func _build_header() -> Control:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", int(Style.px(8)))
	_you_avatar = Avatar.new()
	_you_avatar.setup("?", Style.color("contrast_color_1"), Style.px(28))
	_you_avatar.mouse_default_cursor_shape = Control.CURSOR_ARROW
	_you_avatar.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	row.add_child(_you_avatar)
	var titles := VBoxContainer.new()
	titles.add_theme_constant_override("separation", 0)
	titles.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_you_name = Style.label("")
	_you_name.add_theme_font_override("font", Style.font(true))
	_you_name.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	titles.add_child(_you_name)
	_status_detail = Style.label("Not connected", true, Style.small_size())
	_status_detail.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	titles.add_child(_status_detail)
	row.add_child(titles)
	# Only shown while the connection is not simply working.
	_state_chip = Style.label("", false, Style.small_size())
	_state_chip.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	row.add_child(_state_chip)
	_options_button = MenuButton.new()
	_options_button.icon = Style.icon("GuiTabMenuHl")
	_options_button.tooltip_text = "What you see of others, and what they see of you"
	_options_button.flat = true
	_options_button.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	var popup := _options_button.get_popup()
	popup.add_separator("Show")
	for i in OPTION_ITEMS.size():
		popup.add_check_item(OPTION_ITEMS[i][1], i)
	popup.add_separator("Share")
	for i in SHARE_ITEMS.size():
		popup.add_check_item(SHARE_ITEMS[i][1], OPTION_ITEMS.size() + i)
	popup.add_separator("Notify me about")
	for i in NOTIFY_ITEMS.size():
		popup.add_check_item(NOTIFY_ITEMS[i][1], OPTION_ITEMS.size() + SHARE_ITEMS.size() + i)
	popup.hide_on_checkable_item_selection = false
	popup.id_pressed.connect(func(id: int) -> void:
		var key: String = (OPTION_ITEMS + SHARE_ITEMS + NOTIFY_ITEMS)[id][0]
		var on: bool = not _options.get(key, true)
		_options[key] = on
		popup.set_item_checked(popup.get_item_index(id), on)
		options_changed.emit({key: on}))
	row.add_child(_options_button)
	_leave_button = Style.icon_button("Close", "Disconnect")
	_leave_button.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	_leave_button.pressed.connect(func() -> void: disconnect_requested.emit())
	row.add_child(_leave_button)
	return row


func _build_update() -> PanelContainer:
	var col := VBoxContainer.new()
	col.add_theme_constant_override("separation", int(Style.px(6)))
	_update_text = Style.label("", false, Style.small_size())
	_update_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	col.add_child(_update_text)
	var row := _button_row()
	_update_button = Button.new()
	_update_button.text = "Update"
	Style.primary_button(_update_button)
	_update_button.pressed.connect(func() -> void: update_requested.emit())
	row.add_child(_update_button)
	_restart_button = Button.new()
	_restart_button.text = "Restart Godot now"
	Style.primary_button(_restart_button)
	_restart_button.pressed.connect(func() -> void: restart_requested.emit())
	row.add_child(_restart_button)
	var later := Button.new()
	later.text = "Later"
	Style.quiet_button(later)
	later.pressed.connect(func() -> void: _update.visible = false)
	row.add_child(later)
	col.add_child(row)
	_update = Style.panel(col, Style.card(0.14, 6, Vector4(10, 8, 10, 8), Style.color("accent_color")))
	_update.visible = false
	return _update


## The add-on update card. state: "available", "downloading", "installed",
## "failed" (text says what happened), or "" to hide it.
func set_update(state: String, text: String) -> void:
	_update.visible = state != ""
	_update_text.text = text
	_update_button.visible = state == "available" or state == "failed"
	_update_button.text = "Try again" if state == "failed" else "Update"
	_update_button.disabled = false
	_restart_button.visible = state == "installed"
	if state == "downloading":
		_update_button.visible = true
		_update_button.disabled = true
		_update_button.text = "Updating…"


func _message_card(tint: Color) -> PanelContainer:
	var l := Style.label("", false, Style.small_size())
	l.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	var p := Style.panel(l, Style.card(0.14, 6, Vector4(10, 7, 10, 7), tint))
	p.visible = false
	return p


func _build_signin() -> PanelContainer:
	var col := VBoxContainer.new()
	_signin_col = col
	col.add_theme_constant_override("separation", int(Style.px(8)))

	# Signed out: one button that opens the website to sign in.
	_signed_out = VBoxContainer.new()
	_signed_out.add_theme_constant_override("separation", int(Style.px(6)))
	var title := Style.label("Sign in to YHDE")
	title.add_theme_font_override("font", Style.font(true))
	_signed_out.add_child(title)
	var sub := Style.label("Your browser opens: sign in or make an account there, and your team's projects show up here.", true, Style.small_size())
	sub.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_signed_out.add_child(sub)
	_signin_button = Button.new()
	_signin_button.text = "Sign in with your browser"
	Style.primary_button(_signin_button)
	_signin_button.pressed.connect(func() -> void: sign_in_requested.emit())
	_signed_out.add_child(_signin_button)

	_waiting = VBoxContainer.new()
	_waiting.add_theme_constant_override("separation", int(Style.px(4)))
	_waiting.visible = false
	var wait_text := Style.label("Allow it in your browser. Check that it shows this code:", true, Style.small_size())
	wait_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_waiting.add_child(wait_text)
	_waiting_code = Style.label("")
	_waiting_code.add_theme_font_override("font", Style.font(true))
	_waiting_code.add_theme_font_size_override("font_size", int(Style.font_size() * 1.6))
	_waiting_code.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_waiting.add_child(_waiting_code)
	var wait_row := _button_row()
	var again := Button.new()
	again.text = "Open the page again"
	Style.quiet_button(again)
	again.pressed.connect(func() -> void: sign_in_page_requested.emit())
	wait_row.add_child(again)
	var cancel := Button.new()
	cancel.text = "Cancel"
	Style.quiet_button(cancel)
	cancel.pressed.connect(func() -> void: sign_in_cancelled.emit())
	wait_row.add_child(cancel)
	_waiting.add_child(wait_row)
	_signed_out.add_child(_waiting)

	# A starter project from an invite link can still join with its code.
	_invite_button = Button.new()
	_invite_button.text = "Join with the invite in this project"
	Style.secondary_button(_invite_button)
	_invite_button.visible = false
	_invite_button.pressed.connect(func() -> void: invite_connect_requested.emit())
	_signed_out.add_child(_invite_button)
	col.add_child(_signed_out)

	# Signed in: the team's projects as a grid of cards.
	_signed_in = VBoxContainer.new()
	_signed_in.add_theme_constant_override("separation", int(Style.px(8)))
	_signed_in.visible = false
	var head := HBoxContainer.new()
	_projects_title = Style.label("Your projects")
	_projects_title.add_theme_font_override("font", Style.font(true))
	_projects_title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_projects_title.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	head.add_child(_projects_title)
	var reload := Style.icon_button("Reload", "Refresh the list")
	reload.pressed.connect(func() -> void: projects_refresh_requested.emit())
	head.add_child(reload)
	_signed_in.add_child(head)
	_grid = HFlowContainer.new()
	_grid.add_theme_constant_override("h_separation", int(Style.px(8)))
	_grid.add_theme_constant_override("v_separation", int(Style.px(8)))
	_signed_in.add_child(_grid)
	_grid_empty = Style.label("", true, Style.small_size())
	_grid_empty.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_signed_in.add_child(_grid_empty)
	_grid_action = Button.new()
	Style.secondary_button(_grid_action)
	_grid_action.visible = false
	_grid_action.pressed.connect(func() -> void: site_requested.emit(str(_grid_action.get_meta("path", "/app"))))
	_signed_in.add_child(_grid_action)

	# A new project from this folder: made on the server, then this folder
	# connects and its files go up.
	_new_button = Button.new()
	_new_button.text = "New project from this folder"
	_new_button.icon = Style.icon("Add")
	Style.secondary_button(_new_button)
	_new_button.pressed.connect(func() -> void:
		_new_row.visible = true
		_new_button.visible = false
		_new_name.text = str(ProjectSettings.get_setting("application/config/name", ""))
		_new_name.grab_focus()
		_new_name.select_all())
	_signed_in.add_child(_new_button)
	_new_row = HBoxContainer.new()
	_new_row.add_theme_constant_override("separation", int(Style.px(6)))
	_new_row.visible = false
	_new_name = LineEdit.new()
	_new_name.placeholder_text = "Project name"
	_new_name.max_length = 80
	_new_name.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	Style.field(_new_name)
	_new_name.text_submitted.connect(func(_t: String) -> void: _submit_new())
	_new_row.add_child(_new_name)
	var make := Button.new()
	make.text = "Create"
	Style.primary_button(make)
	make.pressed.connect(_submit_new)
	_new_row.add_child(make)
	var close := Style.icon_button("Close", "Cancel")
	close.pressed.connect(func() -> void:
		_new_row.visible = false
		_new_button.visible = _owner)
	_new_row.add_child(close)
	_signed_in.add_child(_new_row)

	# The people in the project this folder is connected to; its owner
	# invites more here.
	_team_box = VBoxContainer.new()
	_team_box.add_theme_constant_override("separation", int(Style.px(4)))
	_team_box.add_child(Style.hairline())
	_team_title = Style.label("People")
	_team_title.add_theme_font_override("font", Style.font(true))
	_team_title.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	_team_box.add_child(_team_title)
	_team_sub = Style.label("", true, Style.small_size())
	_team_sub.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	_team_box.add_child(_team_sub)
	_members = VBoxContainer.new()
	_members.add_theme_constant_override("separation", int(Style.px(3)))
	_team_box.add_child(_members)
	_invite_row = HBoxContainer.new()
	_invite_row.add_theme_constant_override("separation", int(Style.px(6)))
	_invite_email = LineEdit.new()
	_invite_email.placeholder_text = "teammate@email.com"
	_invite_email.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_invite_email.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	Style.field(_invite_email)
	_invite_email.text_submitted.connect(func(_t: String) -> void: _submit_invite())
	_invite_row.add_child(_invite_email)
	var send := Button.new()
	send.text = "Invite"
	Style.secondary_button(send)
	send.pressed.connect(_submit_invite)
	_invite_row.add_child(send)
	_team_box.add_child(_invite_row)
	_pending = VBoxContainer.new()
	_pending.add_theme_constant_override("separation", int(Style.px(2)))
	_team_box.add_child(_pending)
	_signed_in.add_child(_team_box)
	_storage_note = Style.label("", false, Style.small_size())
	_storage_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_storage_note.visible = false
	_signed_in.add_child(_storage_note)
	_account_line = Style.label("", true, Style.small_size())
	_account_line.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	_signed_in.add_child(_account_line)
	var foot := _button_row()
	var site := Button.new()
	site.text = "Website"
	Style.quiet_button(site)
	site.tooltip_text = "More: statistics, activity, invite links and settings"
	site.pressed.connect(func() -> void: site_requested.emit("/app#/projects"))
	foot.add_child(site)
	site = Button.new()
	site.text = "Plan"
	Style.quiet_button(site)
	site.tooltip_text = "Your plan, seats and storage on the website"
	site.pressed.connect(func() -> void: site_requested.emit("/app#/billing"))
	foot.add_child(site)
	var out := Button.new()
	out.text = "Sign out"
	Style.quiet_button(out)
	out.pressed.connect(func() -> void: sign_out_requested.emit())
	foot.add_child(out)
	_signed_in.add_child(foot)
	col.add_child(_signed_in)

	_advanced_button = Button.new()
	_advanced_button.text = "Advanced"
	_advanced_button.alignment = HORIZONTAL_ALIGNMENT_LEFT
	_advanced_button.icon = Style.icon("GuiTreeArrowRight")
	Style.quiet_button(_advanced_button)
	_advanced_button.add_theme_color_override("font_color", Style.muted())
	_advanced_button.pressed.connect(func() -> void:
		_advanced.visible = not _advanced.visible
		_advanced_button.icon = Style.icon("GuiTreeArrowDown" if _advanced.visible else "GuiTreeArrowRight"))
	col.add_child(_advanced_button)
	_advanced = VBoxContainer.new()
	_advanced.visible = false
	_advanced.add_theme_constant_override("separation", int(Style.px(6)))
	_scripts_check = CheckBox.new()
	_scripts_check.text = "Accept built-in scripts from teammates"
	_scripts_check.tooltip_text = "Built-in (embedded) scripts are code. Only enable this if you trust everyone in your team."
	_scripts_check.add_theme_font_size_override("font_size", Style.small_size())
	_scripts_check.toggled.connect(func(on: bool) -> void: builtin_scripts_toggled.emit(on))
	_advanced.add_child(_scripts_check)
	_trust_check = CheckBox.new()
	_trust_check.text = "Accept code files from teammates without asking"
	_trust_check.tooltip_text = "Plugins, native libraries and @tool scripts run on your computer. Without this, YHDE holds them until you accept them."
	_trust_check.add_theme_font_size_override("font_size", Style.small_size())
	_trust_check.toggled.connect(func(on: bool) -> void: trust_code_toggled.emit(on))
	_advanced.add_child(_trust_check)
	col.add_child(_advanced)
	return Style.panel(col, Style.card(0.04, 8, Vector4(12, 10, 12, 12)))


## One project in the grid; the whole card is the button (click, or Enter
## when focused). A panel, not a Button, so it grows to fit its lines.
func _project_card(p: Dictionary) -> Control:
	var here: Array = p.get("online", [])
	var mine: bool = str(p.get("id", "")) == _folder_project
	var normal := Style.card(0.07 if not mine else 0.12, 8, Vector4(10, 8, 10, 8), Style.color("accent_color") if mine else Color.TRANSPARENT)
	var hover := Style.card(0.14, 8, Vector4(10, 8, 10, 8), Style.color("accent_color"))
	var card := PanelContainer.new()
	card.custom_minimum_size = Vector2(Style.px(150), 0)
	card.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	card.focus_mode = Control.FOCUS_ALL
	card.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	card.tooltip_text = "Connect this folder to %s" % p.get("name", "")
	card.add_theme_stylebox_override("panel", normal)
	card.mouse_entered.connect(func() -> void: card.add_theme_stylebox_override("panel", hover))
	card.mouse_exited.connect(func() -> void: card.add_theme_stylebox_override("panel", normal))
	card.focus_entered.connect(func() -> void: card.add_theme_stylebox_override("panel", hover))
	card.focus_exited.connect(func() -> void: card.add_theme_stylebox_override("panel", normal))
	card.gui_input.connect(func(e: InputEvent) -> void:
		var click: bool = e is InputEventMouseButton and e.pressed and e.button_index == MOUSE_BUTTON_LEFT
		if click or e.is_action_pressed("ui_accept"):
			card.accept_event()
			project_chosen.emit(p))
	var col := VBoxContainer.new()
	col.mouse_filter = Control.MOUSE_FILTER_IGNORE
	col.add_theme_constant_override("separation", int(Style.px(2)))
	var top := HBoxContainer.new()
	top.mouse_filter = Control.MOUSE_FILTER_IGNORE
	var title := Style.label(str(p.get("name", "")))
	title.add_theme_font_override("font", Style.font(true))
	title.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	top.add_child(title)
	var menu := MenuButton.new()
	menu.icon = Style.icon("GuiTabMenuHl")
	menu.flat = true
	menu.tooltip_text = "More"
	var items := menu.get_popup()
	if str(p.get("role", "owner")) == "owner":
		items.add_item("Copy a view link", 0)
	items.add_item("Open on the website", 1)
	items.id_pressed.connect(func(id: int) -> void:
		if id == 0:
			project_link_requested.emit(str(p.get("id", "")))
		else:
			site_requested.emit("/app#/projects/%s" % p.get("id", "")))
	top.add_child(menu)
	col.add_child(top)
	var who := Style.label(("%d here: %s" % [here.size(), ", ".join(PackedStringArray(here))]) if not here.is_empty() else "Nobody here", here.is_empty(), Style.small_size())
	who.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	if not here.is_empty():
		who.add_theme_color_override("font_color", Style.state_color(Style.STATE_LIVE))
	col.add_child(who)
	var view_only := str(p.get("access", "edit")) == "view"
	var info := Style.label(("This folder · " if mine else "") + ("View only · " if view_only else "") + "%d files · %s" % [int(p.get("files", 0)), String.humanize_size(int(p.get("bytes", 0)))], true, Style.small_size())
	info.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	col.add_child(info)
	for l in [title, who, info]:
		l.mouse_filter = Control.MOUSE_FILTER_IGNORE
	card.add_child(col)
	return card


func _build_join_check() -> PanelContainer:
	var col := VBoxContainer.new()
	col.add_theme_constant_override("separation", int(Style.px(8)))
	_join_text = Style.label("", false, Style.small_size())
	_join_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	col.add_child(_join_text)
	var buttons := _button_row()
	var yes := Button.new()
	yes.text = "It's the right folder"
	Style.secondary_button(yes)
	yes.pressed.connect(func() -> void: join_confirmed.emit())
	buttons.add_child(yes)
	var no := Button.new()
	no.text = "Disconnect"
	Style.primary_button(no)
	no.pressed.connect(func() -> void: join_declined.emit())
	buttons.add_child(no)
	col.add_child(buttons)
	_join = Style.panel(col, Style.card(0.14, 6, Vector4(10, 8, 10, 10), Style.warning()))
	_join.visible = false
	return _join


## A first connection whose folder looks like a different game ({} hides it).
func set_join_check(info: Dictionary) -> void:
	_join.visible = not info.is_empty()
	if info.is_empty():
		return
	var project: String = info.get("project", "")
	_join_text.text = "Is this the right folder for %s?\n\nThis folder has %d files, and only %d of them are in the project (which has %d). Nothing has been sent or received yet. If this is a different game, disconnect and open the right folder (or an empty one to download the project)." % [
		project if project != "" else "this project", info.local_files, info.shared_files, info.project_files]


func set_options(options: Dictionary) -> void:
	_options = options.duplicate()
	var popup := _options_button.get_popup()
	var items: Array = OPTION_ITEMS + SHARE_ITEMS + NOTIFY_ITEMS
	for i in items.size():
		popup.set_item_checked(popup.get_item_index(i), _options.get(items[i][0], true))


func set_project(name: String) -> void:
	if name == _project:
		return
	_project = name
	_refresh_detail()


var _detail_base := "Not connected"


func _refresh_detail() -> void:
	var project := _project if _state != Style.STATE_OFFLINE else ""
	_status_detail.text = (project + "  ·  " if project != "" else "") + _detail_base


func _build_files() -> PanelContainer:
	var col := VBoxContainer.new()
	col.add_theme_constant_override("separation", int(Style.px(6)))
	_files_text = Style.label("", true, Style.small_size())
	_files_text.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	col.add_child(_files_text)
	_held = VBoxContainer.new()
	_held.add_theme_constant_override("separation", int(Style.px(6)))
	_held_text = Style.label("", false, Style.small_size())
	_held_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_held.add_child(_held_text)
	_held_list = Style.label("", true, Style.small_size())
	_held_list.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_held.add_child(_held_list)
	var buttons := _button_row()
	var restore := Button.new()
	restore.text = "Restore them"
	Style.primary_button(restore)
	restore.pressed.connect(func() -> void: deletions_restored.emit())
	buttons.add_child(restore)
	var confirm := Button.new()
	confirm.text = "Delete for everyone"
	Style.secondary_button(confirm)
	confirm.add_theme_color_override("font_color", Style.danger())
	confirm.pressed.connect(func() -> void: deletions_confirmed.emit())
	buttons.add_child(confirm)
	_held.add_child(buttons)
	col.add_child(_held)
	# Code from teammates that would run in this editor waits for a yes.
	_code = VBoxContainer.new()
	_code.add_theme_constant_override("separation", int(Style.px(6)))
	_code_text = Style.label("", false, Style.small_size())
	_code_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_code.add_child(_code_text)
	_code_list = Style.label("", true, Style.small_size())
	_code_list.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_code.add_child(_code_list)
	var code_buttons := _button_row()
	var accept := Button.new()
	accept.text = "Accept"
	Style.primary_button(accept)
	accept.pressed.connect(func() -> void: code_accepted.emit())
	code_buttons.add_child(accept)
	var trust := Button.new()
	trust.text = "Always trust this project"
	Style.secondary_button(trust)
	trust.tooltip_text = "Accept code from this project's teammates from now on, without asking."
	trust.pressed.connect(func() -> void:
		_trust_check.set_pressed_no_signal(true)
		code_trusted.emit())
	code_buttons.add_child(trust)
	_code.add_child(code_buttons)
	col.add_child(_code)
	var p := Style.panel(col, Style.card(0.05, 6, Vector4(10, 7, 10, 7)))
	p.visible = false
	return p


func _build_people() -> Control:
	var scroll := ScrollContainer.new()
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	var col := VBoxContainer.new()
	col.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	col.add_theme_constant_override("separation", int(Style.px(4)))
	_summon = Button.new()
	_summon.text = "Bring everyone here"
	_summon.icon = Style.icon("Groups")
	_summon.tooltip_text = "Everyone in the project follows your view: your scene, screen and camera. Anyone can look around on their own again by clicking or pressing a key."
	_summon.alignment = HORIZONTAL_ALIGNMENT_LEFT
	_summon.visible = false
	_summon.pressed.connect(func() -> void: summon_requested.emit())
	col.add_child(_summon)
	_people = VBoxContainer.new()
	_people.add_theme_constant_override("separation", int(Style.px(2)))
	col.add_child(_people)
	_people_empty = Style.label("Nobody else is here yet. Invite your team on the website (Team page), then they sign in here.", true, Style.small_size())
	_people_empty.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	col.add_child(_people_empty)
	scroll.add_child(col)
	return scroll


func _build_activity() -> Control:
	var scroll := ScrollContainer.new()
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	var col := VBoxContainer.new()
	col.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_activity = VBoxContainer.new()
	_activity.add_theme_constant_override("separation", int(Style.px(4)))
	col.add_child(_activity)
	_activity_empty = Style.label("Changes from others show up here.", true, Style.small_size())
	col.add_child(_activity_empty)
	scroll.add_child(col)
	return scroll


func _process(delta: float) -> void:
	_since_render += delta
	_time_refresh += delta
	if _time_refresh >= 5.0:
		_time_refresh = 0.0
		_activity_dirty = true
	# Render at most a few times per second, however busy the stream is.
	if _activity_dirty and _since_render >= 0.25 and is_visible_in_tree():
		_activity_dirty = false
		_since_render = 0.0
		_render_activity()


# Tabs

func _on_tab_changed(index: int) -> void:
	for i in _pages.size():
		_pages[i].visible = i == index
	if index == 1 and _chat:
		_chat.shown()
	elif index == 2 and _comments:
		_comments.shown()
	_update_tab_titles()


func _update_tab_titles() -> void:
	_tabs.set_tab_title(1, "Chat" + Style.badge_text(_unread_chat if _tabs.current_tab != 1 else 0))
	_tabs.set_tab_title(2, "Comments" + Style.badge_text(_unread_comments if _tabs.current_tab != 2 else 0))


func set_badges(chat: int, comments: int) -> void:
	_unread_chat = chat
	_unread_comments = comments
	_update_tab_titles()


func setup_social(social: Object) -> void:
	_chat.setup(social)
	_comments.setup(social)


func open_chat(member: String) -> void:
	_tabs.current_tab = 1
	_chat.open(member)


func refresh_comments() -> void:
	_comments.refresh()


func set_current_scene(path: String) -> void:
	_comments.set_scene(path)


func set_comment_mode(on: bool) -> void:
	_comments.set_comment_mode(on)
	if on:
		_tabs.current_tab = 2


func show_resolved() -> bool:
	return _comments.show_resolved


# Settings

func set_settings(settings: Dictionary) -> void:
	_project_id = settings.get("project_id", "")
	_branch_id = settings.get("branch_id", "")
	_fill_team(_dashboard)
	_fill_storage()
	_scripts_check.set_pressed_no_signal(settings.get("allow_builtin_scripts", false))
	_trust_check.set_pressed_no_signal(settings.get("trust_code", false))
	_refresh_you_preview()


## Signed in or not, the projects to show, and which one this folder is.
func set_account(account: Dictionary, team, projects: Array, folder_project: String, loaded: bool, dashboard := {}) -> void:
	_account = account
	_projects = projects
	_folder_project = folder_project
	# Someone with a beta access code makes projects; each project's owner
	# decides who is in it.
	_owner = team is Dictionary
	_dashboard = dashboard
	_fill_team(dashboard)
	_fill_storage()
	var signed_in := not account.is_empty() and account.has("name")
	_signed_out.visible = not signed_in
	_signed_in.visible = signed_in
	for c in _grid.get_children():
		c.queue_free()
	_grid_action.visible = false
	_grid_empty.visible = false
	if signed_in:
		_account_line.text = str(account.get("name", ""))
		_projects_title.text = "Your projects"
		var live: Array = projects.filter(func(p) -> bool: return not p.get("archived", false))
		for p in live:
			_grid.add_child(_project_card(p))
		if not loaded:
			_grid_empty.text = "Loading your projects…"
			_grid_empty.visible = true
		elif live.is_empty() and not _owner:
			_grid_empty.text = "No projects yet. Projects you make, are invited to or join with a join code on the YHDE website show up here."
			_grid_empty.visible = true
			_show_action("Open the website", "/app#/projects")
		elif live.is_empty():
			_grid_empty.text = "No projects yet. Make one from this folder below, or on the website."
			_grid_empty.visible = true
	_refresh_you_preview()


# The project this folder is connected to (or belongs to).
func _current_project() -> String:
	return _project_id if _project_id != "" else _folder_project


# Before an upload is refused: the project owner's storage from 80 % on.
func _fill_storage() -> void:
	if _storage_note == null:
		return
	_storage_note.visible = false
	var pid := _current_project()
	for p in _projects:
		if str(p.get("id", "")) != pid:
			continue
		var used := float(p.get("storageUsed", 0))
		var limit := float(p.get("storageLimit", 0))
		if limit <= 0.0 or used < limit * 0.8:
			return
		var whose: String = "Your" if str(p.get("role", "owner")) == "owner" else "%s's" % str(p.get("owner", "The owner"))
		if used >= limit:
			_storage_note.text = "%s storage is full: new files you add won't reach the others. Free space on the YHDE website." % whose
			_storage_note.add_theme_color_override("font_color", Style.state_color(Style.STATE_OFFLINE))
		else:
			_storage_note.text = "%s storage is %d %% used. When it's full, new files you add won't reach the others." % [whose, int(round(used / limit * 100.0))]
			_storage_note.add_theme_color_override("font_color", Style.state_color(Style.STATE_RECONNECTING))
		_storage_note.visible = true
		return


func _fill_team(d: Dictionary) -> void:
	if _members == null:
		return
	for c in _members.get_children() + _pending.get_children():
		c.queue_free()
	_new_button.visible = _owner and not _new_row.visible
	var project := {}
	var pid := _current_project()
	for p in d.get("projects", []):
		if str(p.get("id", "")) == pid:
			project = p
	_team_box.visible = not project.is_empty()
	var owns: bool = str(project.get("role", "")) == "owner"
	_invite_row.visible = owns and not project.get("archived", false)
	if project.is_empty():
		return
	var members: Array = project.get("members", [])
	var invites: Array = (project.get("invites", []) as Array).filter(func(i) -> bool: return not i.get("expired", false))
	var online := {}
	for p in d.get("presence", []):
		online[str(p.get("name", ""))] = true
	var others := members.size() - 1 + invites.size()
	_team_title.text = "People in %s" % str(project.get("name", "this project"))
	_team_sub.text = "%d of %d invited" % [others, int(project.get("peopleLimit", 0))] if owns else "%s decides who is in it" % str((project.get("owner", {}) as Dictionary).get("name", "The owner"))
	for m in members:
		var row := HBoxContainer.new()
		row.add_theme_constant_override("separation", int(Style.px(6)))
		var who := Style.label(str(m.get("name", "")), false, Style.small_size())
		who.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		who.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
		row.add_child(who)
		if online.has(str(m.get("name", ""))):
			var here := Style.label("here", false, Style.small_size())
			here.add_theme_color_override("font_color", Style.state_color(Style.STATE_LIVE))
			row.add_child(here)
		if str(m.get("role", "")) == "owner":
			row.add_child(Style.label("owner", true, Style.small_size()))
		elif str(m.get("access", "edit")) == "view":
			row.add_child(Style.label("views", true, Style.small_size()))
		_members.add_child(row)
	for i in invites:
		var row := HBoxContainer.new()
		var who := Style.label("Invited: %s" % i.get("email", ""), true, Style.small_size())
		who.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		who.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
		row.add_child(who)
		if owns:
			var withdraw := Button.new()
			withdraw.text = "Withdraw"
			Style.quiet_button(withdraw)
			var id := str(i.get("id", ""))
			withdraw.pressed.connect(func() -> void: invite_cancel_requested.emit(pid, id))
			row.add_child(withdraw)
		_pending.add_child(row)


func _submit_new() -> void:
	var n := _new_name.text.strip_edges()
	if n == "":
		return
	_new_row.visible = false
	project_create_requested.emit(n)


func _submit_invite() -> void:
	var e := _invite_email.text.strip_edges()
	if e == "":
		return
	_invite_email.text = ""
	invite_requested.emit(_current_project(), e)


func _show_action(text: String, path: String) -> void:
	_grid_action.text = text
	_grid_action.set_meta("path", path)
	_grid_action.visible = true


func set_waiting(code: String) -> void:
	_waiting.visible = code != ""
	_waiting_code.text = code
	_signin_button.visible = code == ""


func set_invite_available(on: bool) -> void:
	_invite_button.visible = on


func get_settings() -> Dictionary:
	return {
		"project_id": _project_id,
		"branch_id": _branch_id,
		"allow_builtin_scripts": _scripts_check.button_pressed,
		"trust_code": _trust_check.button_pressed,
	}


func _refresh_you_preview() -> void:
	if _state != Style.STATE_OFFLINE:
		return
	var n: String = str(_account.get("name", ""))
	_you_name.text = n if n != "" else "Not signed in"
	_you_avatar.setup(_initials(n) if n != "" else "?", Style.color("contrast_color_1"), Style.px(28))


func set_native_missing(title: String, message: String) -> void:
	_you_name.text = "YHDE can't start"
	_status_detail.text = title
	set_problem(title + "\n" + message)
	_signin_button.disabled = true
	_tabs.visible = false
	for p in _pages:
		p.visible = false


# Live state

func set_state(state: int, text: String, detail: String) -> void:
	_state = state
	var offline := state == Style.STATE_OFFLINE
	_detail_base = detail
	_refresh_detail()
	_options_button.visible = not offline
	_signin.visible = offline
	var home: Container = _signin_col if offline else _team_page
	if _signed_in.get_parent() != home:
		_signed_in.get_parent().remove_child(_signed_in)
		home.add_child(_signed_in)
		if offline:
			_signin_col.move_child(_signed_in, 1)
	_leave_button.visible = not offline
	_tabs.visible = not offline
	for i in _pages.size():
		_pages[i].visible = not offline and i == _tabs.current_tab
	# Live is the normal case and needs no label; anything else says what is going on.
	_state_chip.visible = state != Style.STATE_LIVE and not offline
	_state_chip.text = text
	_state_chip.add_theme_color_override("font_color", Style.state_color(state))
	if offline:
		_refresh_you_preview()


func set_problem(text: String) -> void:
	_problem_text.text = text
	_problem.visible = text != ""


func set_notice(text: String) -> void:
	_notice_text.text = text
	_notice.visible = text != ""


func set_hint(text: String) -> void:
	_hint.text = text
	_hint.visible = text != ""


func set_me(me: Dictionary) -> void:
	if me.is_empty():
		return
	_you_avatar.setup(me.initials, me.color, Style.px(28))
	_you_name.text = me.name


func set_footer(text: String) -> void:
	_footer.text = text


## Transfers in progress and deletions waiting for a decision.
func set_files(a: Dictionary, held: PackedStringArray, code: Array = []) -> void:
	var parts := PackedStringArray()
	var up: int = a.get("uploads", 0)
	var down: int = a.get("downloads", 0)
	var importing: int = a.get("importing", 0)
	if up > 0:
		parts.append("Sending %d file%s" % [up, "" if up == 1 else "s"])
	if down > 0:
		parts.append("Receiving %d file%s" % [down, "" if down == 1 else "s"])
	if importing > 0:
		parts.append("Waiting for %d import%s" % [importing, "" if importing == 1 else "s"])
	var total: int = a.get("bytes_total", 0)
	if total > 0 and (up > 0 or down > 0):
		parts.append("%d %%" % int(100.0 * float(a.get("bytes_done", 0)) / float(total)))
	_files_text.text = "  ·  ".join(parts)
	_files_text.visible = not parts.is_empty()
	_held.visible = not held.is_empty()
	if not held.is_empty():
		_held_text.text = "%d files disappeared from your project at once. They are still there for everyone else. Was that on purpose?" % held.size()
		var shown := PackedStringArray()
		for i in mini(held.size(), 4):
			shown.append(held[i].trim_prefix("res://"))
		_held_list.text = ", ".join(shown) + (" and %d more" % (held.size() - shown.size()) if held.size() > shown.size() else "")
	_code.visible = not code.is_empty()
	if not code.is_empty():
		var senders := PackedStringArray()
		var names := PackedStringArray()
		for c in code:
			var by := str(c.get("by", ""))
			if by != "" and not senders.has(by):
				senders.append(by)
			if names.size() < 4:
				names.append(str(c.get("path", "")).trim_prefix("res://"))
		var who := ", ".join(senders) if not senders.is_empty() else "A teammate"
		_code_text.text = "%s sent %d file%s that can run code on your computer (plugins, native libraries, @tool scripts). They are not in your project yet. Only accept if you trust everyone in this project." % [who, code.size(), "" if code.size() == 1 else "s"]
		_code_list.text = ", ".join(names) + (" and %d more" % (code.size() - names.size()) if code.size() > names.size() else "")
	_files.visible = _files_text.visible or _held.visible or _code.visible


# Rows are rebuilt on every roster change; past this many, a "+N more" line.
const MAX_PEOPLE_ROWS := 40


func set_peers(peers: Array) -> void:
	for c in _people.get_children():
		c.queue_free()
	_people_empty.visible = peers.is_empty()
	_summon.visible = not peers.is_empty()
	for i in mini(peers.size(), MAX_PEOPLE_ROWS):
		_people.add_child(_person_row(peers[i]))
	if peers.size() > MAX_PEOPLE_ROWS:
		var more := Style.label("+%d more in this project" % (peers.size() - MAX_PEOPLE_ROWS), true, Style.small_size())
		_people.add_child(more)


func _person_row(p: Dictionary) -> Control:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", int(Style.px(8)))
	var a = Avatar.new()
	a.setup(p.initials, p.color, Style.px(26))
	a.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	if p.following:
		a.ring = Style.color("accent_color")
	var id: String = p.id
	a.tooltip_text = "Follow %s" % p.name
	a.pressed.connect(func() -> void: follow_requested.emit(id))
	row.add_child(a)

	var texts := VBoxContainer.new()
	texts.add_theme_constant_override("separation", 0)
	texts.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	var n := Style.label(p.name)
	n.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	texts.add_child(n)
	var where := Button.new()
	var scene_name: String = p.scene_name if p.scene_name != "" else "No scene open"
	where.text = "%s · %s" % [scene_name, p.tool] if p.tool != "" and p.scene_name != "" else scene_name
	where.alignment = HORIZONTAL_ALIGNMENT_LEFT
	where.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	where.tooltip_text = "Open %s" % p.scene if p.scene != "" else ""
	Style.quiet_button(where)
	for state in ["normal", "hover", "pressed", "disabled"]:
		var sb := StyleBoxEmpty.new()
		sb.content_margin_top = 0
		sb.content_margin_bottom = 0
		where.add_theme_stylebox_override(state, sb)
	where.add_theme_color_override("font_hover_color", Style.color("font_color"))
	where.add_theme_font_size_override("font_size", Style.small_size())
	where.add_theme_color_override("font_color", Style.muted())
	var scene: String = p.scene
	where.pressed.connect(func() -> void: jump_requested.emit(scene))
	where.disabled = scene == ""
	texts.add_child(where)
	row.add_child(texts)

	var member: String = p.get("member", "")
	if member != "":
		var dm := Style.icon_button(Style.CHAT_ICON, "Message %s" % p.name)
		dm.size_flags_vertical = Control.SIZE_SHRINK_CENTER
		dm.pressed.connect(func() -> void: open_chat(member))
		row.add_child(dm)

	var follow := Style.icon_button("GuiVisibilityVisible", "Stop following" if p.following else "Follow %s" % p.name)
	follow.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	if p.following:
		for key in ["icon_normal_color", "icon_hover_color", "icon_pressed_color"]:
			follow.add_theme_color_override(key, Style.color("accent_color"))
	follow.pressed.connect(func() -> void: follow_requested.emit(id))
	row.add_child(follow)
	return row


func add_activity(entry: Dictionary) -> void:
	# Consecutive property edits by the same person on the same object within
	# a few seconds read as one line: "changed position, rotation on Sprite".
	var props: PackedStringArray = entry.get("props", PackedStringArray())
	if not _activity_entries.is_empty() and not props.is_empty():
		var last: Dictionary = _activity_entries[0]
		var last_props: PackedStringArray = last.get("props", PackedStringArray())
		if last.name == entry.name and last.get("target", "") == entry.get("target", "") \
				and not last_props.is_empty() and entry.time - last.time < 4.0:
			for p in props:
				if not last_props.has(p):
					last_props.append(p)
			last.props = last_props
			last.time = entry.time
			_activity_dirty = true
			return
	_activity_entries.push_front(entry)
	if _activity_entries.size() > MAX_ACTIVITY:
		_activity_entries.resize(MAX_ACTIVITY)
	_activity_dirty = true


func _render_activity() -> void:
	for c in _activity.get_children():
		c.queue_free()
	_activity_empty.visible = _activity_entries.is_empty()
	for e in _activity_entries.slice(0, 30):
		var row := HBoxContainer.new()
		row.add_theme_constant_override("separation", int(Style.px(6)))
		var dot := Control.new()
		dot.custom_minimum_size = Vector2(Style.px(6), Style.px(6))
		dot.size_flags_vertical = Control.SIZE_SHRINK_CENTER
		var col: Color = e.color
		dot.draw.connect(func() -> void: dot.draw_circle(dot.size * 0.5, dot.size.x * 0.5, col, true, -1.0, true))
		row.add_child(dot)
		var text := RichTextLabel.new()
		text.bbcode_enabled = true
		text.fit_content = true
		text.scroll_active = false
		text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		text.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		text.add_theme_font_size_override("normal_font_size", Style.small_size())
		text.add_theme_font_size_override("bold_font_size", Style.small_size())
		text.add_theme_color_override("default_color", Style.muted())
		text.text = "[b][color=#%s]%s[/color][/b] %s" % [Style.color("font_color").to_html(false), _escape(e.name), _escape(_describe(e))]
		row.add_child(text)
		var t := Style.label(Style.relative_time(e.time), true, Style.small_size())
		row.add_child(t)
		_activity.add_child(row)


static func _describe(e: Dictionary) -> String:
	var props: PackedStringArray = e.get("props", PackedStringArray())
	if props.is_empty():
		return e.text
	var shown := props.slice(0, 3)
	var more := props.size() - shown.size()
	return "changed %s%s%s" % [", ".join(shown), " +%d" % more if more > 0 else "", e.get("where", "")]


static func _escape(s: String) -> String:
	return s.replace("[", "[lb]")


## Same rule as the native core: "Maya Lind" -> ML, "Maya" -> Ma.
static func _initials(name: String) -> String:
	var words := name.strip_edges().split(" ", false)
	var out := ""
	for w in words:
		if out.length() >= 2:
			break
		out += w.substr(0, 1).to_upper()
	if out.length() == 1 and words.size() == 1 and name.strip_edges().length() > 1:
		out += name.strip_edges().substr(1, 1).to_lower()
	return out if out != "" else "?"
