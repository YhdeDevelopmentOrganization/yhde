@tool
extends VBoxContainer
## The panel's first-run view: one next step until this folder is live for
## the first time. It is shown in place of the panel's usual content (the
## header, sign-in card, tabs and footer are hidden meanwhile); messages,
## the right-folder question and file cards still show below it.
##
##   invite    "Alex invited you to Skyhop"            Join Skyhop
##   waiting   "Finish signing in in your browser"     Open the browser again
##   sign in   "Sign in to YHDE"                       Sign in
##   pick      your projects; "Share this game"        (pick one)
##   joining   "Getting Skyhop" and a bar              -
##   done      "You're in."                            Invite people / OK
##   problem   what is wrong, in one sentence          the one thing to do
##
## A folder that was connected before never shows it. Written to run on
## Godot 4.6 and later.

const Style := preload("style.gd")
const InviteDialog := preload("invite_dialog.gd")

var host: Object # main.gd: account, session, panel and its connect functions
var active := false

var _join := {}        # from the starter project: kind (ticket/code), project, from
var _waiting_join := false # Join was pressed before signing in
var _problem := ""
var _problem_action := ""   # "retry", "sign_in", "switch", "website", "download"
var _live_since := -1.0
var _most_files := 0
var _last := ""             # what was drawn, so it is only rebuilt on a change
var _clock := 0.0
var _busy := false
var _sharing := false       # "Share this game" was pressed: files go out, not in


func _init() -> void:
	add_theme_constant_override("separation", int(Style.px(8)))
	visible = false


## Decides whether this folder needs the first-run view.
func setup(main: Object) -> void:
	host = main
	var folder_known: bool = str(host._folder_project()) != ""
	active = active or (not folder_known and not bool(host._meta("first_run_done", false)))
	var invite: Dictionary = host.account.invite_for(host.folder_key())
	if not invite.is_empty() and not folder_known:
		_join = invite
	_apply()


## The native core is missing or won't load: say so, with what to do.
func show_broken(main: Object, text: String) -> void:
	host = main
	active = true
	_problem = text
	_problem_action = "download"
	_apply()


## Something the account or server refused, while this view is up.
func show_problem(text: String) -> void:
	if not active:
		return
	_problem = text
	var signed_in: bool = host.account != null and host.account.signed_in()
	_problem_action = "retry" if signed_in else "sign_in"
	_apply()


func _process(delta: float) -> void:
	if not active or host == null:
		return
	_clock -= delta
	if _clock > 0.0:
		return
	_clock = 0.25
	_apply()


# What to show

func _state() -> String:
	if _problem != "":
		return "problem"
	var account = host.account
	var session = host.session
	var state: int = session.get_state() if session else Style.STATE_OFFLINE
	if state != Style.STATE_OFFLINE:
		return "done" if _settled() else "joining"
	_live_since = -1.0
	if account.waiting_for_browser():
		return "waiting"
	if not _join.is_empty():
		return "invite"
	if not account.signed_in():
		return "signin"
	return "pick"


## Live, and nothing more is arriving or going (a moment after going live,
## so the first files have had time to start).
func _settled() -> bool:
	var status: Dictionary = host.session.get_status()
	if status.state != Style.STATE_LIVE:
		return false
	var now := Time.get_ticks_msec() / 1000.0
	if _live_since < 0.0:
		_live_since = now
	var a: Dictionary = status.get("assets", {})
	var busy: int = int(a.get("downloads", 0)) + int(a.get("uploads", 0)) + int(a.get("importing", 0))
	return busy == 0 and now - _live_since > 1.5


func _apply() -> void:
	if host == null:
		return
	visible = active
	host.panel.show_first_run(active)
	if not active:
		return
	var state := _state()
	var key := state + "|" + _key_detail(state)
	if key == _last:
		if state == "joining":
			_update_progress()
		return
	_last = key
	for c in get_children():
		c.queue_free()
	match state:
		"problem": _draw_problem()
		"invite": _draw_invite()
		"waiting": _draw_waiting()
		"signin": _draw_sign_in()
		"pick": _draw_pick()
		"joining": _draw_joining()
		"done": _draw_done()


# Parts of the state that change what is drawn.
func _key_detail(state: String) -> String:
	var account = host.account
	match state:
		"pick":
			return "%d|%s|%s|%s" % [account.projects.size(), account.loaded, account.can_make_projects(), _busy]
		"invite":
			return "%s|%s" % [account.signed_in(), _busy]
		"waiting":
			return account.user_code()
		"done":
			return str(_owner_alone())
		"problem":
			return _problem
	return ""


# Drawing

func _title(text: String) -> Label:
	var l := Style.label(text)
	l.add_theme_font_override("font", Style.font(true))
	l.add_theme_font_size_override("font_size", int(Style.font_size() * 1.15))
	l.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	add_child(l)
	return l


func _line(text: String, muted := true) -> Label:
	var l := Style.label(text, muted, Style.small_size())
	l.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	add_child(l)
	return l


func _button(text: String, kind: String, action: Callable) -> Button:
	var b := Button.new()
	b.text = text
	b.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	match kind:
		"primary": Style.primary_button(b)
		"secondary": Style.secondary_button(b)
		_: Style.quiet_button(b)
	b.disabled = _busy and kind == "primary"
	b.pressed.connect(action)
	add_child(b)
	return b


func _game() -> String:
	var name := str(_join.get("project", ""))
	return name if name != "" else "the project"


func _draw_invite() -> void:
	var from := str(_join.get("from", ""))
	var watch: bool = _join.get("kind", "") == "code"
	_title("%s invited you to %s" % [from, _game()] if from != "" else _game())
	if watch:
		_line("You can watch it being made. You can't change anything.")
	elif not host.account.signed_in():
		_line("Press Join, then sign in or make an account in your browser.")
	_button("Join %s" % _game(), "primary", _on_join)


func _draw_waiting() -> void:
	_title("Finish signing in in your browser")
	_line("Check that the page shows %s." % host.account.user_code())
	_button("Open the browser again", "primary", func() -> void: host.account.open_page_again())
	_button("Cancel", "quiet", func() -> void:
		_waiting_join = false
		host.account.cancel())


func _draw_sign_in() -> void:
	_title("Sign in to YHDE")
	_line("Your browser opens. Sign in there, or make an account.")
	_button("Sign in", "primary", func() -> void: host.account.sign_in())


func _draw_pick() -> void:
	var account = host.account
	if not account.loaded:
		_title("Your projects")
		_line("Loading…")
		return
	var live: Array = account.projects.filter(func(p) -> bool: return not p.get("archived", false))
	var files := _folder_has_files()
	if not live.is_empty():
		_title("Your projects")
		_line("Pick one to open it in this folder.")
		for p in live:
			var project: Dictionary = p
			_button(str(project.get("name", "")), "secondary", func() -> void: host._on_project_chosen(project))
	if files and account.can_make_projects():
		if live.is_empty():
			_title("Share this game")
		_line("Your team gets this game and edits it with you, live.")
		_button("Share this game", "primary", _on_share)
	elif files:
		if live.is_empty():
			_title("Share this game")
		_line("During the beta, sharing a game needs an access code. Enter it on the YHDE website.")
		_button("Open the website", "secondary", func() -> void: OS.shell_open(account.http_base + "/app#/projects"))
	elif live.is_empty():
		_title("You're not in a project yet")
		_line("Open the invite link you were sent, or ask for one.")
	_button("Sign out", "quiet", func() -> void: host._on_sign_out())


var _bar: ProgressBar
var _bar_text: Label


func _draw_joining() -> void:
	var name := str(host._meta("project_name", ""))
	if name == "":
		name = _game()
	var a: Dictionary = host.session.get_status().get("assets", {})
	var sending := _sharing or int(a.get("uploads", 0)) > 0
	_title(("Sending %s" if sending else "Getting %s") % name)
	_bar = ProgressBar.new()
	_bar.show_percentage = false
	_bar.custom_minimum_size = Vector2(0, Style.px(6))
	add_child(_bar)
	_bar_text = _line("")
	_most_files = 0
	_update_progress()


func _update_progress() -> void:
	if _bar == null or not is_instance_valid(_bar):
		return
	var a: Dictionary = host.session.get_status().get("assets", {})
	var left := int(a.get("downloads", 0)) + int(a.get("uploads", 0))
	_most_files = maxi(_most_files, left)
	if _most_files == 0:
		_bar.value = 0.0
		_bar_text.text = "Connecting…"
		return
	var done := _most_files - left
	_bar.max_value = float(_most_files)
	_bar.value = float(done)
	_bar_text.text = "%d of %d files" % [done, _most_files]


func _draw_done() -> void:
	_title("You're in.")
	if _join.get("kind", "") == "code" or str(host._meta("access", "edit")) == "view":
		_line("You can watch and chat. You can't change anything.")
	if _owner_alone():
		_line("Nobody else is in this project yet.")
		_button("Invite people", "primary", func() -> void:
			var id := str(host._folder_project())
			InviteDialog.new().open(host.account, id, str(host._meta("project_name", "")))
			_finish())
		_button("Later", "quiet", _finish)
	else:
		_button("OK", "primary", _finish)


func _draw_problem() -> void:
	_title("Something went wrong")
	_line(_problem, false)
	match _problem_action:
		"download":
			_button("Get YHDE again", "primary", func() -> void: OS.shell_open(host.account_base() + "/addon/yhde-addon.zip"))
		"sign_in":
			_button("Try again", "primary", func() -> void:
				_problem = ""
				host.account.sign_in())
		"switch":
			_button("Sign in with another account", "primary", func() -> void:
				_problem = ""
				host._on_sign_out()
				_waiting_join = true
				host.account.sign_in())
		_:
			_button("Try again", "primary", func() -> void:
				_problem = ""
				_last = ""
				if not _join.is_empty():
					_on_join()
				else:
					host.account.refresh())


# Actions

func _on_join() -> void:
	if _join.get("kind", "") == "code":
		host._connect_invite()
		return
	if not host.account.signed_in():
		_waiting_join = true
		host.account.sign_in()
		return
	_redeem()


## Called when the account changes: finish a Join that waited for signing in.
func account_changed() -> void:
	if not active:
		return
	if _waiting_join and host.account.signed_in() and not host.account.waiting_for_browser():
		_waiting_join = false
		_redeem()
	_last = ""
	_apply()


func _redeem() -> void:
	var ticket: String = host.account.ticket_for(host.folder_key())
	if ticket == "":
		_join = {}
		_apply()
		return
	_busy = true
	_apply()
	var r: Dictionary = await host.account.redeem_ticket(ticket)
	_busy = false
	if r.has("error"):
		_problem = str(r.error)
		_problem_action = "switch" if int(r.get("status", 0)) == 403 else "retry"
		_last = ""
		_apply()
		return
	host.account.save_ticket(host.folder_key(), "")
	host.account.save_invite(host.folder_key(), {})
	_join = {}
	host._connect_account({"id": str(r.get("id", "")), "branchId": str(r.get("branchId", "")),
		"name": str(r.get("name", "")), "access": str(r.get("access", "edit"))})


func _on_share() -> void:
	var name := str(ProjectSettings.get_setting("application/config/name", "")).strip_edges()
	if name == "":
		name = ProjectSettings.globalize_path("res://").trim_suffix("/").get_file()
	_busy = true
	_sharing = true
	_apply()
	await host._create_project(name)
	_busy = false
	_last = ""
	_apply()


func _finish() -> void:
	active = false
	host._save_setting("first_run_done", true)
	host.account.save_invite(host.folder_key(), {})
	_apply()


func _owner_alone() -> bool:
	var info: Dictionary = host.account.project_info(str(host._folder_project()))
	if info.is_empty() or str(info.get("role", "")) != "owner":
		return false
	return (info.get("members", []) as Array).size() <= 1 and (info.get("invites", []) as Array).is_empty()


## Whether this folder has anything of its own (more than YHDE and Godot's
## own files): then it can be shared as a new project.
func _folder_has_files() -> bool:
	var dir := DirAccess.open("res://")
	if dir == null:
		return false
	for f in dir.get_files():
		if f != "project.godot" and not f.begins_with(".") and f != "icon.svg" and not f.ends_with(".import") and not f.ends_with(".uid"):
			return true
	for d in dir.get_directories():
		if d != "addons" and not d.begins_with("."):
			return true
	var addons := DirAccess.open("res://addons")
	return addons != null and addons.get_directories().size() > 1
