@tool
extends Node
## Your YHDE account in the editor: signing in through the website (device
## flow) and your team's projects.
##
## Signing in opens the website in your browser; you press Allow there and
## the editor gets a sign-in token. No password ever goes through Godot. The
## token is kept in Godot's own settings folder (never in the project), one
## per server, and is sent as `Authorization: Bearer <token>`.

signal changed                               # signed in or out, projects updated
signal waiting(user_code: String, url: String) # approve this code in the browser
signal problem(text: String)
signal message(text: String)                   # something worked (for a toast)

const FILE := "yhde_account.cfg"

var http_base := ""   # https://yhde.example.com
var token := ""
var user := {}        # {id, name, email}
var team = null       # {id, name, plan, role} when you can make projects, else null
var projects: Array = []
var loaded := false   # projects fetched at least once since signing in
# What the website's dashboard sees: your plan, and each project's people,
# invitations and who is connected.
var dashboard := {}

var _device_code := ""
var _user_code := ""
var _url := ""
var _poll: Timer
var _polling := false
var _refreshing := false


func setup(base: String) -> void:
	http_base = base.trim_suffix("/")
	_poll = Timer.new()
	_poll.one_shot = false
	_poll.timeout.connect(_on_poll)
	add_child(_poll)
	var cfg := ConfigFile.new()
	if cfg.load(_path()) == OK:
		token = str(cfg.get_value(http_base, "token", ""))
		user = cfg.get_value(http_base, "user", {})


func signed_in() -> bool:
	return token != ""


func waiting_for_browser() -> bool:
	return _device_code != ""


## Starts signing in: gets a code, opens the approval page in the browser.
func sign_in() -> void:
	cancel()
	var device := "Godot on %s" % OS.get_name()
	var r := await _request(HTTPClient.METHOD_POST, "/api/device/start", {"device": device}, false)
	if r.code != 200:
		problem.emit(_error_text(r, "Couldn't start signing in."))
		return
	_device_code = str(r.data.get("deviceCode", ""))
	_user_code = str(r.data.get("userCode", ""))
	_url = str(r.data.get("url", ""))
	# YHDE_NO_BROWSER: automated tests approve without a browser.
	if OS.get_environment("YHDE_NO_BROWSER") == "":
		OS.shell_open(_url)
	waiting.emit(_user_code, _url)
	_poll.wait_time = maxf(2.0, float(r.data.get("interval", 3)))
	_poll.start()


func open_page_again() -> void:
	if _url != "":
		OS.shell_open(_url)


func cancel() -> void:
	_device_code = ""
	_user_code = ""
	_url = ""
	if _poll:
		_poll.stop()
	changed.emit()


func _on_poll() -> void:
	if _polling or _device_code == "":
		return
	_polling = true
	var r := await _request(HTTPClient.METHOD_POST, "/api/device/poll", {"deviceCode": _device_code}, false)
	_polling = false
	if _device_code == "":
		return # cancelled meanwhile
	if r.code != 200:
		return # try again next tick
	match str(r.data.get("status", "")):
		"approved":
			_poll.stop()
			_device_code = ""
			token = str(r.data.get("token", ""))
			user = {"name": str(r.data.get("name", "")), "email": str(r.data.get("email", ""))}
			_save()
			await refresh()
		"denied":
			cancel()
			problem.emit("Signing in wasn't allowed in the browser.")
		"expired":
			cancel()
			problem.emit("The sign-in code expired. Press Sign in again.")


## Fetches who you are and the projects you can open: yours and the ones
## you were invited to.
func refresh() -> void:
	if token == "" or _refreshing:
		return
	_refreshing = true
	var r := await _request(HTTPClient.METHOD_GET, "/api/editor/projects", null, true)
	_refreshing = false
	if r.code == 401:
		_forget()
		problem.emit("Your sign-in has ended. Sign in again.")
		return
	if r.code != 200:
		problem.emit(_error_text(r, "Couldn't load your projects."))
		changed.emit()
		return
	user = r.data.get("user", {})
	team = r.data.get("team")
	projects = r.data.get("projects", [])
	var d := await _request(HTTPClient.METHOD_GET, "/api/dashboard", null, true)
	dashboard = d.data if d.code == 200 else {}
	loaded = true
	_save()
	changed.emit()


## Whether you can make projects (a beta access code on the website).
func can_make_projects() -> bool:
	return dashboard.get("plan") is Dictionary


## The dashboard's entry for one project: members, invites, your role.
func project_info(project_id: String) -> Dictionary:
	for p in dashboard.get("projects", []):
		if str(p.get("id", "")) == project_id:
			return p
	return {}


## Makes a project of your own. Returns it (with branchId) or {} on failure.
func create_project(project_name: String) -> Dictionary:
	var r := await _request(HTTPClient.METHOD_POST, "/api/team/projects", {"name": project_name}, true)
	if r.code != 200:
		problem.emit(_error_text(r, "Couldn't make the project."))
		return {}
	var id := str(r.data.get("id", ""))
	await refresh()
	for p in projects:
		if str(p.get("id", "")) == id:
			return p
	return {}


## A view link for one person to watch the project (7 days). "" on failure.
func make_link(project_id: String) -> String:
	var r := await _request(HTTPClient.METHOD_POST, "/api/team/projects/%s/links" % project_id, {"label": "From Godot", "hours": 168, "maxUses": 1}, true)
	if r.code != 200:
		problem.emit(_error_text(r, "Couldn't make a view link."))
		return ""
	return str(r.data.get("url", ""))


## Invites someone into one of your projects by email.
func invite(project_id: String, email: String) -> bool:
	var r := await _request(HTTPClient.METHOD_POST, "/api/team/projects/%s/invites" % project_id, {"email": email}, true)
	if r.code != 200:
		problem.emit(_error_text(r, "Couldn't send the invitation."))
		return false
	message.emit("Invitation sent to %s." % email)
	await refresh()
	return true


func cancel_invite(project_id: String, id: String) -> void:
	var r := await _request(HTTPClient.METHOD_POST, "/api/team/projects/%s/invites/%s/cancel" % [project_id, id], {}, true)
	if r.code != 200:
		problem.emit(_error_text(r, "Couldn't cancel the invitation."))
		return
	await refresh()


func sign_out() -> void:
	if token != "":
		_request(HTTPClient.METHOD_POST, "/api/editor/sign-out", {}, true) # fire and forget
	_forget()


## Forgets the sign-in on this computer (the server already refused it).
func forget() -> void:
	_forget()


func _forget() -> void:
	token = ""
	user = {}
	team = null
	projects = []
	dashboard = {}
	loaded = false
	_save()
	changed.emit()


func _path() -> String:
	return EditorInterface.get_editor_paths().get_config_dir().path_join(FILE)


func _save() -> void:
	var cfg := ConfigFile.new()
	cfg.load(_path())
	if token == "":
		if cfg.has_section(http_base):
			cfg.erase_section(http_base)
	else:
		cfg.set_value(http_base, "token", token)
		cfg.set_value(http_base, "user", user)
	cfg.save(_path())


func _error_text(r: Dictionary, fallback: String) -> String:
	if r.code == 0:
		return "Can't reach YHDE (%s). Check your internet connection." % http_base
	if r.code == 429:
		return "Too many tries. Wait a minute and try again."
	var detail := str(r.data.get("detail", "")) if r.data is Dictionary else ""
	return detail if detail != "" else fallback


## One HTTP request; returns {code, data}. code 0 = no answer.
func _request(method: int, path: String, body, auth: bool) -> Dictionary:
	var req := HTTPRequest.new()
	req.timeout = 20.0
	add_child(req)
	var headers := PackedStringArray(["Content-Type: application/json", "X-YHDE: 1", "Accept: application/json"])
	if auth and token != "":
		headers.append("Authorization: Bearer " + token)
	var err := req.request(http_base + path, headers, method, "" if body == null else JSON.stringify(body))
	if err != OK:
		req.queue_free()
		return {"code": 0, "data": {}}
	var result: Array = await req.request_completed
	req.queue_free()
	var code: int = result[1] if result[0] == HTTPRequest.RESULT_SUCCESS else 0
	var parsed = JSON.parse_string((result[3] as PackedByteArray).get_string_from_utf8()) if code != 0 else null
	return {"code": code, "data": parsed if parsed is Dictionary else {}}
