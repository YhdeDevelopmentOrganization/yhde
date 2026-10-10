@tool
extends RefCounted
## YHDE editor plugin: the thin, UI-only layer (started by plugin.gd).
##
## All synchronization (protocol, operations, scene diffing and application,
## files, identity) lives in the native YhdeSession class. This script builds
## the UI, forwards editor events to the session, draws other people and
## comment pins in the 2D and 3D viewports, and keeps chat and comments.

const Style := preload("res://addons/yhde/ui/style.gd")
const TopBar := preload("res://addons/yhde/ui/top_bar.gd")
const DockPanel := preload("res://addons/yhde/ui/dock.gd")
const Overlays := preload("res://addons/yhde/ui/overlays.gd")
const Follow := preload("res://addons/yhde/ui/follow.gd")
const Social := preload("res://addons/yhde/ui/social.gd")
const Pins := preload("res://addons/yhde/ui/comment_pins.gd")
const CommentPopup := preload("res://addons/yhde/ui/comment_popup.gd")
const Smooth := preload("res://addons/yhde/ui/smooth.gd")
const PresenceMarks := preload("res://addons/yhde/ui/presence_marks.gd")
const CodeCarets := preload("res://addons/yhde/ui/code_carets.gd")
const Updater := preload("res://addons/yhde/ui/updater.gd")
const Account := preload("res://addons/yhde/ui/account.gd")

## YHDE's server. Everyone uses it; there is nothing to type. YHDE_SERVER
## (an environment variable) points the editor at another one, for development.
const DEFAULT_SERVER := "wss://yhde.frostinteractive.fi/ws"
const JOIN_FILE := "res://addons/yhde/join.cfg" # from a starter project

## What each person chooses to see (the dock's options menu). Stored per project.
const OPTIONS := {
	"cursors": true,       # others' cursors, selections and drags in 2D/3D
	"scene_tabs": true,    # avatars on the scene tabs
	"scene_tree": true,    # avatars next to nodes others have selected
	"filesystem": true,    # avatars next to files others have open
	"script_list": true,   # avatars in the script editor's file list
	"code_carets": true,   # others' carets and selections in shared scripts
	"smooth": true,        # smooth motion (off: show updates as they arrive)
	"share_cursor": true,  # others see your cursor in 2D/3D
	# Notifications (warnings and errors always show).
	"notify_dm": true,
	"notify_mention": true,
	"notify_comment": true,
	"notify_summon": true,
	"notify_sync": true,
}

const META := "yhde"

var plugin: EditorPlugin
var session # YhdeSession (native); untyped so its methods resolve at runtime
var top_bar: HBoxContainer
var dock: EditorDock
var panel: MarginContainer
var follow := Follow.new()
var social := Social.new()
var popup: PopupPanel

var _main_screen := "2D"
var _overlays_dirty := false
var _ui_dirty := false
# update_overlays() redraws the whole 2D view: at most 60 times a second,
# 30 with more than CROWD people online.
const OVERLAY_GAP := 1.0 / 60.0
const OVERLAY_GAP_CROWD := 1.0 / 30.0
const CROWD := 16
var _overlay_clock := 0.0
var _peer_count := 0
# Summons are checked at most 10 times a second, not on every presence update.
var _summons_dirty := false
var _summon_clock := 0.0
var _people_clock := 0.5
var _footer_timer := 0.0
var _pointer_hooks: Array[Control] = []
var _shader_tabs_id := 0 # the Shader editor's TabContainer, once found
var _script_tabs_id := 0
var _script_search_after := 0
var _shader_search_after := 0
var _summons_seen := {} # peer session id -> the last summon id we saw from them
var _pointer_in_2d := false # the mouse is over the 2D view (keys go to YHDE's 2D shortcuts)
var _pointer_2d_at := Vector2.ZERO
var _was_live := false
var _last_problem := ""

# Comments in the viewports.
var _comment_mode := false
var _cursor_2d = null # Vector2 while placing a comment in 2D
var _cursor_3d = null
var _pins_2d: Array = []
var _pins_3d: Array = []
var _selected_thread := ""
var _pending_focus := "" # a thread to open once its scene is in front
var _pin_refresh := 0.0
var options := OPTIONS.duplicate()
var marks := PresenceMarks.new()
var updater: Node
var account: Node # ui/account.gd
var _view_only := false # connected to a project we may only look at
var _marks_refresh := 0.0
var _script_scan := 0.0
var _bound_editors := {} # CodeEdit instance id -> path


func _init(p: EditorPlugin) -> void:
	plugin = p


func enter() -> void:
	panel = DockPanel.new()
	dock = EditorDock.new()
	dock.title = "YHDE"
	dock.dock_icon = Style.logo()
	dock.force_show_icon = true
	dock.theme_changed.connect(func() -> void:
		var logo := Style.logo()
		if dock.dock_icon != logo:
			dock.dock_icon = logo)
	dock.layout_key = "yhde"
	dock.default_slot = EditorDock.DOCK_SLOT_RIGHT_UL
	dock.add_child(panel)
	plugin.add_dock(dock)

	top_bar = TopBar.new()
	plugin.add_control_to_container(EditorPlugin.CONTAINER_TOOLBAR, top_bar)
	top_bar.status_pressed.connect(func() -> void: dock.make_visible())

	if not ClassDB.class_exists("YhdeSession"):
		var file := "libyhde.%s.editor.%s" % [OS.get_name().to_lower(), Engine.get_architecture_name()]
		panel.set_native_missing(
			"YHDE's native core is missing for this computer (%s, %s)." % [OS.get_name(), Engine.get_architecture_name()],
			"Put the file for your system (%s…) into addons/yhde/bin/ and restart the editor. Get it by downloading the YHDE add-on again." % file)
		return

	session = ClassDB.instantiate("YhdeSession")
	session.name = "YhdeSession"
	plugin.add_child(session)

	session.state_changed.connect(_on_state_changed)
	session.peers_changed.connect(func() -> void: _ui_dirty = true)
	session.presence_changed.connect(func() -> void:
		_overlays_dirty = true
		_summons_dirty = true)
	session.activity.connect(panel.add_activity)
	session.notice.connect(_on_notice)
	session.social_event.connect(social.handle)
	session.project_joined.connect(_on_project_joined)
	session.join_check.connect(func(info: Dictionary) -> void:
		dock.make_visible()
		panel.set_join_check(info))
	marks.session = session

	social.session = session
	social.chat_changed.connect(func(_c: String) -> void: _refresh_badges())
	social.comments_changed.connect(_on_comments_changed)
	social.unread_changed.connect(_refresh_badges)
	social.error.connect(func(text: String) -> void: _toast(text, 1))
	social.pinged.connect(func(text: String, kind: String) -> void:
		if options.get("notify_" + kind, true):
			_toast(text, 0))
	panel.setup_social(social)

	popup = CommentPopup.new()
	popup.social = social
	popup.closed_thread.connect(func() -> void:
		_selected_thread = ""
		_overlays_dirty = true)
	EditorInterface.get_base_control().add_child(popup)

	panel.disconnect_requested.connect(_on_disconnect_requested)

	account = Account.new()
	plugin.add_child(account)
	account.setup(http_base(server_url()))
	account.changed.connect(_refresh_account)
	account.waiting.connect(func(code: String, _url: String) -> void:
		panel.set_problem("")
		panel.set_waiting(code))
	account.problem.connect(func(text: String) -> void:
		panel.set_waiting("")
		panel.set_problem(text))
	panel.sign_in_requested.connect(func() -> void: account.sign_in())
	panel.sign_in_cancelled.connect(func() -> void:
		account.cancel()
		panel.set_waiting(""))
	panel.sign_in_page_requested.connect(func() -> void: account.open_page_again())
	panel.sign_out_requested.connect(_on_sign_out)
	panel.project_chosen.connect(_on_project_chosen)
	panel.projects_refresh_requested.connect(func() -> void: account.refresh())
	panel.site_requested.connect(func(path: String) -> void: OS.shell_open(account.http_base + path))
	panel.invite_connect_requested.connect(_connect_invite)
	account.message.connect(func(text: String) -> void: _toast(text, 0))
	panel.project_create_requested.connect(_create_project)
	panel.project_link_requested.connect(func(project_id: String) -> void:
		var url: String = await account.make_link(project_id)
		if url != "":
			DisplayServer.clipboard_set(url)
			_toast("Invite link copied. Send it to your teammate: it works for 7 days, 5 downloads.", 0))
	panel.invite_requested.connect(func(project_id: String, email: String) -> void: account.invite(project_id, email))
	panel.invite_cancel_requested.connect(func(project_id: String, id: String) -> void: account.cancel_invite(project_id, id))
	panel.follow_requested.connect(_toggle_follow)
	updater = Updater.new()
	plugin.add_child(updater)
	updater.available.connect(func(version: String) -> void:
		panel.set_update("available", "YHDE %s is available (you have %s). It is installed only if it is signed as a YHDE release." % [version, Updater.installed_version()]))
	updater.progress.connect(func(state: String, text: String) -> void: panel.set_update(state, text))
	panel.update_requested.connect(func() -> void: updater.install())
	panel.restart_requested.connect(func() -> void: EditorInterface.restart_editor(true))
	panel.summon_requested.connect(func() -> void:
		session.summon()
		_toast("Everyone is now following your view.", 0))
	panel.jump_requested.connect(_jump_to_scene)
	panel.builtin_scripts_toggled.connect(func(on: bool) -> void:
		session.set_allow_builtin_scripts(on)
		_save_setting("allow_builtin_scripts", on))
	panel.comment_mode_requested.connect(func() -> void: set_comment_mode(not _comment_mode))
	panel.comment_focus_requested.connect(focus_thread)
	panel.code_accepted.connect(func() -> void: session.accept_held_code())
	panel.trust_code_toggled.connect(func(on: bool) -> void:
		session.set_trust_code(on)
		_save_setting("trust_code", on))
	panel.code_trusted.connect(func() -> void:
		session.set_trust_code(true)
		_save_setting("trust_code", true))
	panel.deletions_confirmed.connect(func() -> void:
		session.confirm_deletions()
		_refresh_footer())
	panel.deletions_restored.connect(func() -> void:
		session.restore_deletions()
		_refresh_footer())
	panel.join_confirmed.connect(func() -> void:
		session.confirm_join()
		panel.set_join_check({}))
	panel.join_declined.connect(func() -> void:
		panel.set_join_check({})
		_on_disconnect_requested())
	panel.options_changed.connect(_on_options_changed)
	top_bar.follow_requested.connect(_toggle_follow)
	top_bar.comment_mode_requested.connect(func() -> void: set_comment_mode(not _comment_mode))
	top_bar.message_requested.connect(func(member: String) -> void:
		dock.make_visible()
		panel.open_chat(member))

	# The editor can still save things while YHDE shuts down: check the session.
	plugin.scene_saved.connect(func(path: String) -> void: if session: session.notify_scene_saved(path))
	plugin.scene_closed.connect(func(path: String) -> void: if session: session.notify_scene_closed(path))
	plugin.scene_changed.connect(func(_root: Node) -> void:
		if session == null:
			return
		session.notify_scene_changed()
		panel.set_current_scene(_current_scene())
		_open_pending_focus.call_deferred())
	plugin.resource_saved.connect(func(res: Resource) -> void: if session: session.notify_resource_saved(res))
	plugin.main_screen_changed.connect(_on_main_screen_changed)

	plugin.set_input_event_forwarding_always_enabled()
	plugin.set_force_draw_over_forwarding_enabled()
	follow.session = session

	_take_join_file()
	var settings := _load_settings()
	for key in OPTIONS:
		options[key] = EditorInterface.get_editor_settings().get_project_metadata(META, "show_" + key, OPTIONS[key])
	panel.set_options(options)
	_apply_options()
	panel.set_settings(settings)
	_refresh_account()
	if account.signed_in():
		account.refresh()
	panel.set_current_scene(_current_scene())
	session.set_allow_builtin_scripts(settings.allow_builtin_scripts)
	session.set_trust_code(settings.trust_code)
	_on_state_changed(session.get_state(), session.get_status_text())
	_detect_main_screen.call_deferred()
	_hook_pointer_exit.call_deferred()
	if settings.auto_connect:
		_auto_connect.call_deferred()


func exit() -> void:
	follow.stop()
	if updater:
		updater.queue_free()
		updater = null
	marks.clear()
	_unbind_all_editors()
	for c in _pointer_hooks:
		if is_instance_valid(c) and c.mouse_exited.is_connected(_on_pointer_exit):
			c.mouse_exited.disconnect(_on_pointer_exit)
	_pointer_hooks.clear()
	if popup:
		popup.queue_free()
		popup = null
	if session:
		session.stop()
		session.queue_free()
		session = null
	if top_bar:
		plugin.remove_control_from_container(EditorPlugin.CONTAINER_TOOLBAR, top_bar)
		top_bar.queue_free()
	if dock:
		plugin.remove_dock(dock)
		dock.queue_free()


func process(delta: float) -> void:
	if session == null:
		return
	# The people lists are rebuilt from scratch: at most twice a second.
	_people_clock += delta
	if _ui_dirty and _people_clock >= 0.5:
		_people_clock = 0.0
		_ui_dirty = false
		_refresh_people()
		_overlays_dirty = true
	if session.get_follow_target() != "":
		follow.main_screen = _main_screen
		follow.update()
	# Pins follow their nodes. The viewport redraws itself when nodes move;
	# this slow refresh covers changes that arrive without a redraw.
	_pin_refresh += delta
	if _pin_refresh >= 0.25 and not social.threads.is_empty() and session.get_state() == Style.STATE_LIVE:
		_pin_refresh = 0.0
		_overlays_dirty = true
	if Smooth.moving:
		_overlays_dirty = true # something is still gliding into place
	_summon_clock += delta
	if _summons_dirty and _summon_clock >= 0.1:
		_summon_clock = 0.0
		_summons_dirty = false
		_check_summons(session.get_peers())
	_overlay_clock += delta
	if _overlays_dirty and _overlay_clock >= (OVERLAY_GAP_CROWD if _peer_count > CROWD else OVERLAY_GAP):
		_overlay_clock = 0.0
		_overlays_dirty = false
		Smooth.begin_frame()
		plugin.update_overlays()
		_redraw_carets()
	_marks_refresh += delta
	if _marks_refresh >= 0.25:
		_marks_refresh = 0.0
		marks.refresh(session.get_peers() if session.get_state() == Style.STATE_LIVE else [])
	_script_scan += delta
	if _script_scan >= 0.3:
		_script_scan = 0.0
		_sync_script_editors()
	_footer_timer += delta
	if _footer_timer >= 1.0:
		_footer_timer = 0.0
		_refresh_footer()


# Settings

func _load_settings() -> Dictionary:
	var es := EditorInterface.get_editor_settings()
	var defaults: Dictionary = session.get_defaults()
	var user := OS.get_environment("USER")
	if user == "":
		user = OS.get_environment("USERNAME")
	return {
		"name": es.get_project_metadata(META, "name", user.capitalize()),
		"url": es.get_project_metadata(META, "url", server_url()),
		"project_id": es.get_project_metadata(META, "project_id", defaults.project_id),
		"branch_id": es.get_project_metadata(META, "branch_id", defaults.branch_id),
		"allow_builtin_scripts": es.get_project_metadata(META, "allow_builtin_scripts", false),
		"trust_code": es.get_project_metadata(META, "trust_code", false),
		"auto_connect": es.get_project_metadata(META, "auto_connect", false),
	}


## A starter project from the server's join page carries the server address
## and invite code in addons/yhde/join.cfg: store them (the code outside the
## project, like a sign-in), delete the file and offer to join.
func _take_join_file() -> void:
	if not FileAccess.file_exists(JOIN_FILE):
		return
	var cfg := ConfigFile.new()
	var ok := cfg.load(JOIN_FILE) == OK
	DirAccess.remove_absolute(ProjectSettings.globalize_path(JOIN_FILE))
	if not ok:
		return
	var url := str(cfg.get_value("join", "url", ""))
	var code := str(cfg.get_value("join", "code", ""))
	if url == "" or code == "":
		return
	_save_setting("invite_url", url)
	_save_setting("auto_connect", false)
	session.set_access_key(url, code)
	panel.set_notice("Welcome! Sign in to see your team's projects, or press \"Join with the invite in this project\". The game downloads when you connect.")
	dock.make_visible.call_deferred()


func _save_setting(key: String, value: Variant) -> void:
	EditorInterface.get_editor_settings().set_project_metadata(META, key, value)


# Connection

static func server_url() -> String:
	var env := OS.get_environment("YHDE_SERVER").strip_edges()
	return env if env != "" else DEFAULT_SERVER


## https://host for a wss://host/ws address.
static func http_base(ws: String) -> String:
	return ws.replace("wss://", "https://").replace("ws://", "http://").trim_suffix("/").trim_suffix("/ws")


func _meta(key: String, fallback: Variant = "") -> Variant:
	return EditorInterface.get_editor_settings().get_project_metadata(META, key, fallback)


## The project this folder belongs to ("" until it has connected once).
func _folder_project() -> String:
	return str(_meta("project_id", "")) if str(_meta("project_name", "")) != "" else ""


func _refresh_account() -> void:
	if not account.waiting_for_browser():
		panel.set_waiting("")
	panel.set_account(account.user if account.signed_in() else {}, account.team, account.projects, _folder_project(), account.loaded, account.dashboard)
	panel.set_invite_available(not account.signed_in() and str(_meta("invite_url", "")) != "")


func _on_project_chosen(p: Dictionary) -> void:
	var folder := _folder_project()
	if folder == "" or folder == str(p.get("id", "")):
		_connect_account(p)
		return
	var ask := ConfirmationDialog.new()
	ask.title = "Connect this folder to %s?" % p.get("name", "")
	ask.dialog_text = "This folder is %s. Connect it to %s instead? Only do this if it's the same game: files that don't match are held back and you are asked first." % [_meta("project_name", "another project"), p.get("name", "")]
	ask.dialog_autowrap = true
	ask.min_size = Vector2i(int(Style.px(420)), 0)
	ask.ok_button_text = "Connect"
	ask.confirmed.connect(func() -> void:
		ask.queue_free()
		_connect_account(p))
	ask.canceled.connect(ask.queue_free)
	EditorInterface.get_base_control().add_child(ask)
	ask.popup_centered()


## Connects with your account to one of your team's projects.
func _connect_account(p: Dictionary) -> void:
	if session.get_state() != Style.STATE_OFFLINE:
		_on_disconnect_requested()
	var url := server_url()
	# The sign-in token is what the server checks, like a code; the native
	# core keeps it outside the project.
	session.set_access_key(url, account.token)
	var settings := _load_settings()
	settings.url = url
	settings.name = str(account.user.get("name", "Me"))
	settings.project_id = str(p.get("id", ""))
	settings.branch_id = str(p.get("branchId", ""))
	for key in ["url", "project_id", "branch_id"]:
		_save_setting(key, settings[key])
	_save_setting("project_name", str(p.get("name", "")))
	_save_setting("via", "account")
	_view_only = str(p.get("access", "edit")) == "view"
	_save_setting("access", str(p.get("access", "edit")))
	panel.set_project(str(p.get("name", "")))
	_connect_with(settings)
	# The server knows us by our account.
	social.me = str(account.user.get("id", social.me))


## Connects with the invite code a starter project came with.
func _connect_invite() -> void:
	var settings := _load_settings()
	var url := str(_meta("invite_url", ""))
	if url == "":
		return
	# A starter project can name any server: the first time it is not the
	# usual one, ask before sending its code there.
	var host := Account.host_of(url)
	if host != Account.host_of(server_url()) and str(_meta("invite_trusted", "")) != url:
		var ask := ConfirmationDialog.new()
		ask.title = "Connect to %s?" % host
		ask.dialog_text = "This project's invite connects to %s, not the usual YHDE server (%s). The game's files will come from there. Only continue if you trust whoever gave you this project." % [host, Account.host_of(server_url())]
		ask.dialog_autowrap = true
		ask.min_size = Vector2i(int(Style.px(420)), 0)
		ask.ok_button_text = "Connect"
		ask.confirmed.connect(func() -> void:
			ask.queue_free()
			_save_setting("invite_trusted", url)
			_connect_invite())
		ask.canceled.connect(ask.queue_free)
		EditorInterface.get_base_control().add_child(ask)
		ask.popup_centered()
		return
	settings.url = url
	_save_setting("url", url)
	_save_setting("via", "invite")
	_connect_with(settings)


## On startup, reconnect the way this folder connected last time.
func _auto_connect() -> void:
	match str(_meta("via", "")):
		"account":
			var folder := _folder_project()
			if not account.signed_in() or folder == "":
				return
			_connect_account({"id": folder, "branchId": str(_meta("branch_id", "")), "name": str(_meta("project_name", "")), "access": str(_meta("access", "edit"))})
		"invite":
			_connect_invite()


## Makes a project in the team and connects this folder to it: its files go up.
func _create_project(project_name: String) -> void:
	var p: Dictionary = await account.create_project(project_name)
	if p.is_empty():
		return
	_toast("%s is ready. Connecting this folder: its files go up to your team." % project_name, 0)
	_connect_account(p)


func _on_sign_out() -> void:
	if session.get_state() != Style.STATE_OFFLINE and str(_meta("via", "")) == "account":
		_on_disconnect_requested()
	account.sign_out()
	session.set_access_key(server_url(), "")
	_save_setting("via", "")


func _connect_with(settings: Dictionary) -> void:
	panel.set_problem("")
	_last_problem = ""
	session.set_allow_builtin_scripts(settings.get("allow_builtin_scripts", false))
	session.set_trust_code(settings.get("trust_code", false))
	if session.start(settings.url, settings.project_id, settings.branch_id, settings.name):
		_save_setting("auto_connect", true)
		panel.set_me(session.get_local_peer())
		_summons_seen.clear()
		social.me = session.get_member_id()
		social.my_name = settings.name
		social.reset()


func _on_disconnect_requested() -> void:
	follow.stop()
	set_comment_mode(false)
	session.stop()
	_save_setting("auto_connect", false)
	social.reset()
	marks.clear()
	_unbind_all_editors()
	panel.set_project("")
	if account and account.signed_in():
		account.refresh()


func _on_state_changed(state: int, text: String) -> void:
	top_bar.set_status(state, text)
	var status: Dictionary = session.get_status()
	panel.set_state(state, text, _detail(status))
	if state == Style.STATE_LIVE and not _was_live:
		_on_live(status)
	_was_live = state == Style.STATE_LIVE
	var problem := explain_error(status)
	if problem != _last_problem:
		_last_problem = problem
		panel.set_problem(problem)
	_ui_dirty = true


func _on_live(status: Dictionary) -> void:
	panel.set_problem("")
	updater.check(_url())
	var server: String = status.get("server_version", "")
	var mine: String = session.get_version()
	if server == "":
		panel.set_notice("The server runs an older YHDE (before 0.2). Editing works, but chat and comments need the server updated to %s." % mine)
	elif server.get_slice(".", 0) != mine.get_slice(".", 0) or server.get_slice(".", 1) != mine.get_slice(".", 1):
		panel.set_notice("The server runs YHDE %s and your add-on is %s. Update whichever is older so everyone has the same features." % [server, mine])
	else:
		panel.set_notice("")
	if _view_only:
		panel.set_notice("View only: you can look around, chat and comment in this project, but the server keeps your changes out. Ask your team's owner for edit access.")


## A person-readable explanation of why we are not connected, or "".
func explain_error(status: Dictionary) -> String:
	var error: String = status.get("error", "")
	var state: int = status.state
	if error == "" or state == Style.STATE_LIVE:
		return ""
	var url := _url()
	if error.contains("access key") or error.contains("Access key") or error.contains("invite code"):
		if str(_meta("via", "")) == "account":
			# Re-check: a sign-in that ended signs you out; otherwise the
			# project is just no longer yours (removed from the team).
			account.refresh()
			return "This project isn't open to you any more: you were removed from the team, or signed out on the website. Your projects are listed above."
		return "The invite code isn't accepted any more. Ask your team for a new invite link, or sign in with your account."
	if error.begins_with("Server unreachable") or error.begins_with("Could not open"):
		var local := url.contains("127.0.0.1") or url.contains("localhost")
		if local:
			return "No YHDE server is running on this computer (%s). Start it, or unset YHDE_SERVER to use YHDE's server." % url
		return "Can't reach YHDE (%s). Check your internet connection. YHDE keeps retrying; your edits are kept and sent when it's back." % url
	if error.begins_with("Connection closed"):
		return "The server closed the connection (%s). YHDE reconnects on its own; if it keeps happening, the server may be restarting or out of date." % error
	if error.begins_with("Malformed frame"):
		return "The server speaks a different YHDE version. Make sure the server and this add-on (%s) are the same release." % session.get_version()
	return error


func _detail(status: Dictionary) -> String:
	match int(status.state):
		Style.STATE_LIVE:
			var n: int = status.peers
			return "Just you here" if n == 0 else ("1 other person here" if n == 1 else "%d other people here" % n)
		Style.STATE_CONNECTING, Style.STATE_SYNCING:
			return _url()
		Style.STATE_RECONNECTING:
			return _url()
	return "Not connected"


func _url() -> String:
	return str(_meta("url", server_url()))


func _on_notice(text: String, level: int) -> void:
	# The native core's refusal speaks of codes; signed in, explain_error says
	# what actually happened (the sign-in ended).
	if text.begins_with("The server did not accept your code") and str(_meta("via", "")) == "account":
		return
	# View only: the panel already says changes are kept out; one warning
	# per file you touch would only be noise.
	if _view_only and text.begins_with("The server refused"):
		return
	if level > 0 or options.notify_sync:
		_toast(text, level)
	if level >= 2:
		panel.set_problem(text)


func _toast(text: String, level: int) -> void:
	var toaster := EditorInterface.get_editor_toaster()
	if toaster:
		var severity := EditorToaster.SEVERITY_INFO
		if level == 1:
			severity = EditorToaster.SEVERITY_WARNING
		elif level >= 2:
			severity = EditorToaster.SEVERITY_ERROR
		toaster.push_toast("YHDE: " + text, severity)


func _on_project_joined(project_id: String, branch_id: String, name: String) -> void:
	# The invite code decided the project: this folder belongs to it from now on.
	_save_setting("project_id", project_id)
	_save_setting("branch_id", branch_id)
	_save_setting("project_name", name)
	panel.set_project(name)


# Options

func _on_options_changed(changed: Dictionary) -> void:
	for key in changed:
		options[key] = changed[key]
		EditorInterface.get_editor_settings().set_project_metadata(META, "show_" + key, changed[key])
	_apply_options()


func _apply_options() -> void:
	Smooth.enabled = options.smooth
	if session and not options.share_cursor:
		session.clear_pointer()
	marks.options = options
	marks.refresh(session.get_peers() if session and session.get_state() == Style.STATE_LIVE else [])
	_overlays_dirty = true
	for id in _bound_editors:
		var editor = instance_from_id(id)
		if editor:
			var layer = editor.get_node_or_null("YhdeCodeCarets")
			if layer:
				layer.visible = options.code_carets


# Shared scripts (live co-editing)

## Script editor tabs -> files (editor internals: the file list's tooltips are
## paths and its metadata the tab index). Shared text files are bound to the
## native core, which keeps them in sync as people type.
func _sync_script_editors() -> void:
	if session.get_state() != Style.STATE_LIVE:
		if not _bound_editors.is_empty():
			_unbind_all_editors()
		return
	var seen := {}
	for pair in _script_editor_tabs() + _shader_editor_tabs():
		var editor: CodeEdit = pair[0]
		var path: String = pair[1]
		var id: int = editor.get_instance_id()
		seen[id] = true
		session.text_bind(editor, path)
		if not session.is_text_live(path):
			continue
		_bound_editors[id] = path
		var layer = editor.get_node_or_null("YhdeCodeCarets")
		if layer == null:
			layer = CodeCarets.new()
			layer.session = session
			editor.add_child(layer, false, Node.INTERNAL_MODE_BACK)
		layer.path = path
		layer.visible = options.code_carets
	for id in _bound_editors.keys():
		if not seen.has(id):
			var editor = instance_from_id(id)
			if editor:
				session.text_unbind(editor)
				var layer = editor.get_node_or_null("YhdeCodeCarets")
				if layer:
					layer.queue_free()
			_bound_editors.erase(id)


## [CodeEdit, path] of each open Script editor tab.
func _script_editor_tabs() -> Array:
	var out: Array = []
	var se := EditorInterface.get_script_editor()
	var list := marks.script_list()
	if se == null or list == null:
		return out
	var tabs := instance_from_id(_script_tabs_id) as TabContainer if _script_tabs_id != 0 else null
	if tabs == null or not tabs.is_inside_tree():
		tabs = null
		_script_tabs_id = 0
		# Walking the script editor is slow: look again at most every 2 s.
		var now := Time.get_ticks_msec()
		if now < _script_search_after:
			return out
		_script_search_after = now + 2000
		for t in se.find_children("*", "TabContainer", true, false):
			if t.get_child_count() > 0 and t.get_child(0).has_method("get_base_editor"):
				tabs = t
				_script_tabs_id = t.get_instance_id()
				break
	if tabs == null:
		return out
	for i in list.item_count:
		var path := str(list.get_item_tooltip(i))
		var index = list.get_item_metadata(i)
		if not path.begins_with("res://") or path.contains("::") or typeof(index) != TYPE_INT or index >= tabs.get_child_count():
			continue
		var tab := tabs.get_child(index)
		var editor = tab.get_base_editor() if tab.has_method("get_base_editor") else null
		if editor is CodeEdit:
			out.append([editor, path])
	return out


## [CodeEdit, path] of each open Shader editor tab (editor internals, 4.7: the
## Shader Editor dock holds a file list, tooltip = path, beside a TabContainer
## with one TextShaderEditor per file in the same order). Visual shaders have
## no CodeEdit and are skipped.
func _shader_editor_tabs() -> Array:
	var out: Array = []
	var tabs := instance_from_id(_shader_tabs_id) as TabContainer if _shader_tabs_id != 0 else null
	if tabs == null or not tabs.is_inside_tree():
		tabs = null
		_shader_tabs_id = 0
		# Walking the whole editor is slow: look again at most every 3 s.
		var now := Time.get_ticks_msec()
		if now < _shader_search_after:
			return out
		_shader_search_after = now + 3000
		for t in EditorInterface.get_base_control().find_children("*", "TabContainer", true, false):
			if t.get_child_count() > 0 and t.get_child(0).get_class() in ["TextShaderEditor", "VisualShaderEditor"]:
				tabs = t
				_shader_tabs_id = t.get_instance_id()
				break
	if tabs == null:
		return out
	var list: ItemList = null
	for c in tabs.get_parent().get_children():
		if c is ItemList:
			list = c
	if list == null or list.item_count != tabs.get_child_count():
		return out
	for i in list.item_count:
		var path := str(list.get_item_tooltip(i))
		var tab := tabs.get_child(i)
		if not path.begins_with("res://") or path.contains("::") or tab.get_class() != "TextShaderEditor":
			continue
		var edits := tab.find_children("*", "CodeEdit", true, false)
		if not edits.is_empty():
			out.append([edits[0], path])
	return out


func _unbind_all_editors() -> void:
	for id in _bound_editors.keys():
		var editor = instance_from_id(id)
		if editor:
			if session:
				session.text_unbind(editor)
			var layer = editor.get_node_or_null("YhdeCodeCarets")
			if layer:
				layer.queue_free()
	_bound_editors.clear()


func _redraw_carets() -> void:
	for id in _bound_editors:
		var editor = instance_from_id(id)
		var layer = editor.get_node_or_null("YhdeCodeCarets") if editor else null
		if layer and layer.visible:
			layer.refresh()


# People

func _refresh_people() -> void:
	var peers: Array = session.get_peers()
	_peer_count = peers.size()
	peers.sort_custom(func(a: Dictionary, b: Dictionary) -> bool:
		if a.same_scene != b.same_scene:
			return a.same_scene
		return a.name.naturalnocasecmp_to(b.name) < 0)
	for p in peers:
		social.remember_name(p.member, p.name)
	var me: Dictionary = session.get_local_peer()
	# Signed-in connections pick the project themselves; its name is saved.
	var project_name: String = session.get_project_name()
	panel.set_project(project_name if project_name != "" else str(_meta("project_name", "")))
	_check_summons(peers)
	top_bar.set_peers(peers, me, session.get_follow_target())
	panel.set_peers(peers)
	panel.set_me(me)
	var status: Dictionary = session.get_status()
	panel.set_state(status.state, status.text, _detail(status))


func _refresh_footer() -> void:
	var s: Dictionary = session.get_status()
	if s.state == Style.STATE_OFFLINE:
		panel.set_footer("")
		panel.set_hint("")
		panel.set_files({}, PackedStringArray())
		return
	var parts := PackedStringArray()
	parts.append("#%d" % s.seq)
	if s.pending > 0:
		parts.append("%d to send" % s.pending)
	if s.rtt_ms >= 0.0:
		parts.append("%d ms" % int(s.rtt_ms))
	panel.set_footer("  ·  ".join(parts))
	panel.set_hint("Save this scene to start collaborating on it." if s.scene_unsaved else "")
	panel.set_files(s.get("assets", {}), session.get_held_deletions(), session.get_held_code())


func _refresh_badges() -> void:
	panel.set_badges(social.unread_chat_total(), social.unread_comments())
	top_bar.set_comment_state(_comment_mode, social.unread_comments())


## Someone pressed "Bring everyone here": follow them. Each summon has an id,
## acted on once; a summon already running when we first see a person (we
## just joined) is left alone.
func _check_summons(peers: Array) -> void:
	for p in peers:
		var id: String = p.get("summon", "")
		var seen: String = _summons_seen.get(p.id, "")
		var first_sight := not _summons_seen.has(p.id)
		_summons_seen[p.id] = id
		if id == "" or id == seen or first_sight:
			continue
		if session.get_follow_target() != p.id:
			session.follow(p.id)
		if options.notify_summon:
			_toast("%s brought everyone to their view. Click or press a key to look around on your own." % p.name, 0)
		_ui_dirty = true


func _toggle_follow(peer_id: String) -> void:
	if session.get_follow_target() == peer_id:
		session.unfollow()
		follow.stop()
	else:
		session.follow(peer_id)
	_ui_dirty = true
	_overlays_dirty = true


func _jump_to_scene(path: String) -> void:
	if path != "" and ResourceLoader.exists(path):
		EditorInterface.open_scene_from_path(path)


# Comments

func set_comment_mode(on: bool) -> void:
	if on and (session == null or session.get_state() != Style.STATE_LIVE):
		_toast("Connect to your team's server to comment.", 1)
		on = false
	if on and _current_scene() == "":
		_toast("Open a saved scene to comment on it.", 1)
		on = false
	_comment_mode = on
	_cursor_2d = null
	_cursor_3d = null
	top_bar.set_comment_state(_comment_mode, social.unread_comments())
	panel.set_comment_mode(_comment_mode)
	_overlays_dirty = true


func _current_scene() -> String:
	var root := EditorInterface.get_edited_scene_root()
	return root.scene_file_path if root else ""


func _on_comments_changed() -> void:
	panel.refresh_comments()
	_refresh_badges()
	if popup and popup.visible:
		popup.refresh()
	_overlays_dirty = true


## Show a thread: bring its scene to the front, select its node, open the card.
func focus_thread(thread_id: String) -> void:
	var t: Dictionary = social.threads.get(thread_id, {})
	if t.is_empty():
		return
	if t.scene != _current_scene():
		if not ResourceLoader.exists(t.scene):
			_toast("%s is not in this project." % t.scene, 1)
			return
		_pending_focus = thread_id
		EditorInterface.open_scene_from_path(t.scene)
		return
	var anchor: Dictionary = t.get("anchor", {})
	var screen := "2D" if anchor.get("space") == "2d" else "3D"
	if screen != _main_screen:
		EditorInterface.set_main_screen_editor(screen)
	var node = session.find_node_by_id(t.scene, str(t.node)) if t.get("node") != null else null
	if node:
		EditorInterface.get_selection().clear()
		EditorInterface.get_selection().add_node(node)
	_selected_thread = thread_id
	_overlays_dirty = true
	if screen == "2D":
		_center_2d_on(t)
	# Let the view move and redraw before placing the card next to the pin.
	await plugin.get_tree().process_frame
	await plugin.get_tree().process_frame
	_open_popup_for(thread_id)


func _center_2d_on(t: Dictionary) -> void:
	var vp := EditorInterface.get_editor_viewport_2d()
	var at = Pins.screen_2d(session, t)
	if vp == null or at == null:
		return
	var world: Vector2 = vp.global_canvas_transform.affine_inverse() * (at as Vector2)
	if Rect2(Vector2.ZERO, Vector2(vp.size)).grow(-60).has_point(at):
		return # already in view
	var editor: Node = vp
	while editor and editor.get_class() != "CanvasItemEditor": # editor-internal class
		editor = editor.get_parent()
	if editor and editor.has_method("center_at"):
		editor.center_at(world)


func _open_pending_focus() -> void:
	if _pending_focus != "" and social.threads.has(_pending_focus) and social.threads[_pending_focus].scene == _current_scene():
		var id := _pending_focus
		_pending_focus = ""
		focus_thread(id)


func _open_popup_for(thread_id: String) -> void:
	var t: Dictionary = social.threads.get(thread_id, {})
	var main_screen := EditorInterface.get_editor_main_screen()
	var pos := main_screen.get_global_rect().get_center() if main_screen else Vector2.ZERO
	var anchor: Dictionary = t.get("anchor", {})
	if anchor.get("space") == "2d":
		var at = Pins.screen_2d(session, t)
		var vp := EditorInterface.get_editor_viewport_2d()
		if at != null and vp and vp.get_parent() is Control:
			pos = (vp.get_parent() as Control).get_global_position() + at
	else:
		var vp := EditorInterface.get_editor_viewport_3d(0)
		if vp and vp.get_parent() is Control:
			var holder := vp.get_parent() as Control
			var k := holder.size / Vector2(vp.size) if vp.size.x > 0 else Vector2.ONE
			var at = Pins.screen_3d(session, t, vp.get_camera_3d(), k)
			if at != null:
				pos = holder.get_global_position() + at
	popup.show_thread(thread_id, pos)


func _pins_for(overlay: Control, space: String, camera: Camera3D = null, k := Vector2.ONE) -> Array:
	var out: Array = []
	if session.get_state() != Style.STATE_LIVE:
		return out
	var scene := _current_scene()
	var show_resolved: bool = panel.show_resolved()
	for t in social.thread_list(scene, show_resolved):
		if (t.anchor as Dictionary).get("space") != space:
			continue
		var at = Pins.screen_2d(session, t) if space == "2d" else Pins.screen_3d(session, t, camera, k)
		if at == null or not Rect2(Vector2.ZERO, overlay.size).grow(20).has_point(at):
			continue
		var author: String = t.author.name
		out.append({
			"id": str(t.id),
			"pos": at,
			"color": _color_for(author),
			"label": str(social.number_of(str(t.id))),
			"selected": str(t.id) == _selected_thread,
			"resolved": t.get("resolved") != null,
		})
	return out


func _color_for(name: String) -> Color:
	return session.get_color_for(name)


func _handle_comment_click(overlay_pos: Vector2, pins: Array, anchor_fn: Callable, holder: Control) -> bool:
	var hit := Pins.hit(pins, overlay_pos)
	if hit != "":
		_selected_thread = hit
		_overlays_dirty = true
		popup.show_thread(hit, holder.get_global_position() + overlay_pos)
		return true
	if not _comment_mode:
		return false
	var anchor: Dictionary = anchor_fn.call()
	if anchor.is_empty() or anchor.get("scene", "") == "":
		_toast("Save this scene first: comments belong to a saved scene.", 1)
		return true
	set_comment_mode(false)
	popup.compose(anchor, holder.get_global_position() + overlay_pos)
	return true


func _is_comment_key(event: InputEvent) -> bool:
	return event is InputEventKey and event.pressed and not event.echo and event.keycode == KEY_C \
		and not (event.ctrl_pressed or event.alt_pressed or event.meta_pressed or event.shift_pressed)


# Editor screens and pointer

func _detect_main_screen() -> void:
	for child in EditorInterface.get_editor_main_screen().get_children():
		if child is Control and child.visible:
			match child.get_class():
				"CanvasItemEditor":
					_on_main_screen_changed("2D")
				"Node3DEditor":
					_on_main_screen_changed("3D")
			return


func _on_main_screen_changed(screen: String) -> void:
	_main_screen = screen
	if session:
		session.set_main_screen(screen)
	if screen != "2D" and screen != "3D":
		set_comment_mode(false)


func _hook_pointer_exit() -> void:
	var containers: Array[Control] = []
	var vp2 := EditorInterface.get_editor_viewport_2d()
	if vp2 and vp2.get_parent() is Control:
		containers.append(vp2.get_parent())
	for i in 4:
		var vp3 := EditorInterface.get_editor_viewport_3d(i)
		if vp3 and vp3.get_parent() is Control:
			containers.append(vp3.get_parent())
	for c in containers:
		if not c.mouse_exited.is_connected(_on_pointer_exit):
			c.mouse_exited.connect(_on_pointer_exit)
			_pointer_hooks.append(c)


func _on_pointer_exit() -> void:
	if session:
		session.clear_pointer()
	_cursor_2d = null
	_cursor_3d = null
	_overlays_dirty = true


func _interrupts_follow(event: InputEvent) -> bool:
	if session.get_follow_target() == "":
		return false
	var interrupt: bool = (event is InputEventMouseButton and event.pressed) or (event is InputEventKey and event.pressed and not event.echo)
	if interrupt:
		session.unfollow()
		follow.stop()
		_ui_dirty = true
	return interrupt


## 2D input, from the plugin's _input (see plugin.gd). Returns true when YHDE
## used the event (a comment click, the comment key).
func input_2d(event: InputEvent) -> bool:
	if session == null or _main_screen != "2D":
		return false
	var vp := EditorInterface.get_editor_viewport_2d()
	var holder: Control = vp.get_parent() as Control if vp else null
	if holder == null or not holder.is_visible_in_tree():
		return false
	var pos := _pointer_2d_at
	if event is InputEventMouse:
		pos = _canvas_position(holder, vp, event)
		var inside := Rect2(Vector2.ZERO, Vector2(vp.size)).has_point(pos) and _canvas_under_mouse(holder)
		if not inside:
			if _pointer_in_2d:
				_pointer_in_2d = false
				_on_pointer_exit()
			return false
		_pointer_in_2d = true
		_pointer_2d_at = pos
		if options.share_cursor:
			session.set_pointer_2d(pos)
		if _comment_mode:
			_cursor_2d = pos
			_overlays_dirty = true
	elif not (event is InputEventKey) or not _pointer_in_2d or _typing_somewhere():
		return false
	_interrupts_follow(event)
	if session.get_state() != Style.STATE_LIVE:
		return false
	if _is_comment_key(event):
		set_comment_mode(not _comment_mode)
		return true
	if _comment_mode and event is InputEventKey and event.pressed and event.keycode == KEY_ESCAPE:
		set_comment_mode(false)
		return true
	if event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		return _handle_comment_click(pos, _pins_2d, func() -> Dictionary: return Pins.anchor_2d(session, pos), holder)
	return false


## The mouse in the 2D viewport's own pixels (what the overlay draws in).
func _canvas_position(holder: Control, vp: SubViewport, event: InputEventMouse) -> Vector2:
	var local := holder.get_global_transform_with_canvas().affine_inverse() * event.position
	if holder.size.x > 0 and holder.size.y > 0:
		local *= Vector2(vp.size) / holder.size
	return local


## True unless something else is on top of the 2D view there (a toolbar
## button, the zoom widget, a scroll bar, a popup).
func _canvas_under_mouse(holder: Control) -> bool:
	var hovered := holder.get_viewport().gui_get_hovered_control()
	if hovered == null:
		return true
	if hovered is BaseButton or hovered is Range or hovered is LineEdit or hovered is TextEdit:
		return false
	var editor := holder.get_parent()
	while editor and editor.get_class() != "CanvasItemEditor":
		editor = editor.get_parent()
	return editor == null or editor.is_ancestor_of(hovered)


func _typing_somewhere() -> bool:
	var focus := EditorInterface.get_base_control().get_viewport().gui_get_focus_owner()
	return focus is LineEdit or focus is TextEdit


func forward_3d_gui_input(camera: Camera3D, event: InputEvent) -> int:
	if session == null:
		return EditorPlugin.AFTER_GUI_INPUT_PASS
	if event is InputEventMouse:
		if options.share_cursor:
			session.set_pointer_3d(camera, event.position)
		if _comment_mode:
			_cursor_3d = event.position
			_overlays_dirty = true
	_interrupts_follow(event)
	if session.get_state() != Style.STATE_LIVE:
		return EditorPlugin.AFTER_GUI_INPUT_PASS
	if _is_comment_key(event):
		set_comment_mode(not _comment_mode)
		return EditorPlugin.AFTER_GUI_INPUT_STOP
	if _comment_mode and event is InputEventKey and event.pressed and event.keycode == KEY_ESCAPE:
		set_comment_mode(false)
		return EditorPlugin.AFTER_GUI_INPUT_STOP
	if event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		var vp := camera.get_viewport() as SubViewport
		var holder: Control = vp.get_parent() if vp else null
		if holder:
			var pos: Vector2 = event.position
			var k := holder.size / Vector2(vp.size) if vp.size.x > 0 else Vector2.ONE
			var handled := _handle_comment_click(pos, _pins_3d,
				func() -> Dictionary: return Pins.anchor_3d(session, camera, pos / k), holder)
			if handled:
				return EditorPlugin.AFTER_GUI_INPUT_STOP
	return EditorPlugin.AFTER_GUI_INPUT_PASS


func draw_2d(overlay: Control) -> void:
	if session and session.get_state() != Style.STATE_OFFLINE:
		if options.cursors:
			Overlays.draw_2d(overlay, session.get_overlay_2d())
		_pins_2d = _pins_for(overlay, "2d")
		Pins.draw(overlay, _pins_2d)
		if _comment_mode:
			Pins.draw_comment_mode(overlay, _cursor_2d)
		Overlays.draw_follow_frame(overlay, session.get_follow_view())


func draw_3d(overlay: Control) -> void:
	if session and session.get_state() != Style.STATE_OFFLINE:
		if options.cursors:
			Overlays.draw_3d(overlay, session.get_overlay_3d())
		var pair := Overlays._viewport_for(overlay)
		if not pair.is_empty() and pair[0] != null and pair[1].size.x > 0:
			_pins_3d = _pins_for(overlay, "3d", pair[0], overlay.size / Vector2(pair[1].size))
			Pins.draw(overlay, _pins_3d)
		if _comment_mode:
			Pins.draw_comment_mode(overlay, _cursor_3d)
		Overlays.draw_follow_frame(overlay, session.get_follow_view())
