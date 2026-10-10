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
## Redirects are followed by hand, to the same address only, this many times.
const MAX_REDIRECTS := 3
const REDIRECT_STATUS := [301, 302, 303, 307, 308]

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
	if not may_open(_url, http_base):
		problem.emit("The server's sign-in page is not on %s, so YHDE did not open it: %s. Open it yourself only if you trust it." % [host_of(http_base), _url.left(200)])
		_url = ""
		_poll.wait_time = maxf(2.0, float(r.data.get("interval", 3)))
		_poll.start()
		return
	# YHDE_NO_BROWSER: automated tests approve without a browser.
	if OS.get_environment("YHDE_NO_BROWSER") == "":
		OS.shell_open(_url)
	waiting.emit(_user_code, _url)
	_poll.wait_time = maxf(2.0, float(r.data.get("interval", 3)))
	_poll.start()


func open_page_again() -> void:
	if _url != "" and may_open(_url, http_base):
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
	# The sign-in is readable by this user only (Linux, macOS; Windows keeps
	# the folder per user): written beside the file, restricted, then moved over it.
	var tmp := _path() + ".tmp"
	if cfg.save(tmp) != OK:
		return
	if OS.get_name() != "Windows":
		FileAccess.set_unix_permissions(tmp, FileAccess.UNIX_READ_OWNER | FileAccess.UNIX_WRITE_OWNER)
	if DirAccess.rename_absolute(tmp, _path()) != OK:
		DirAccess.remove_absolute(tmp)


func _error_text(r: Dictionary, fallback: String) -> String:
	if r.code == 0:
		return "Can't reach YHDE (%s). Check your internet connection." % http_base
	if r.code == 429:
		return "Too many tries. Wait a minute and try again."
	var detail := str(r.data.get("detail", "")) if r.data is Dictionary else ""
	return detail if detail != "" else fallback


## Whether the sign-in may be sent to this address: https://, or http:// only
## for this computer (localhost, 127.x.x.x, [::1]).
static func allows_token(base: String) -> bool:
	var b := base.strip_edges().to_lower()
	if b.begins_with("https://"):
		return true
	if not b.begins_with("http://"):
		return false
	var authority := b.trim_prefix("http://")
	for stop in ["/", "?", "#"]:
		authority = authority.get_slice(stop, 0)
	if authority.contains("@"):
		return false # user name and password in the address: not clear which host it is
	var host := authority
	if host.begins_with("["):
		host = host.get_slice("]", 0) + "]"
	else:
		host = host.get_slice(":", 0)
	if host == "localhost" or host == "[::1]":
		return true
	return host.begins_with("127.") and host.is_valid_ip_address()


## The host of an address, in lowercase ("" when there is none): "[::1]",
## "example.com". A user name in the address is dropped.
static func host_of(address: String) -> String:
	var a := address.strip_edges().to_lower()
	if not a.contains("://"):
		return ""
	var authority := a.get_slice("://", 1)
	for stop in ["/", "?", "#"]:
		authority = authority.get_slice(stop, 0)
	authority = authority.get_slice("@", authority.get_slice_count("@") - 1)
	if authority.begins_with("["):
		return authority.get_slice("]", 0) + "]"
	return authority.get_slice(":", 0)


## Whether a page the server names may be opened in the browser: https:// (or
## http:// on this computer) on the same host as the server, never a file or
## another program's link.
static func may_open(page: String, base: String) -> bool:
	var p := page.strip_edges()
	var lower := p.to_lower()
	if not (lower.begins_with("https://") or lower.begins_with("http://")):
		return false
	if not allows_token(p) or p.contains("@"):
		return false
	return host_of(p) != "" and host_of(p) == host_of(base)


## The address a redirect points to, if it stays on this server, else "".
func _same_server_location(headers: PackedStringArray) -> String:
	for h in headers:
		if h.to_lower().begins_with("location:"):
			var loc := h.substr(9).strip_edges()
			if loc.begins_with("/") and not loc.begins_with("//"):
				return http_base + loc
			if loc == http_base or loc.begins_with(http_base + "/") or loc.begins_with(http_base + "?"):
				return loc
			return ""
	return ""


## One HTTP request; returns {code, data}. code 0 = no answer, code -1 = refused
## here (the detail says why). Redirects are never left to Godot: the sign-in
## must not follow one to another server, so only same-server ones are followed.
func _request(method: int, path: String, body, auth: bool) -> Dictionary:
	if not allows_token(http_base):
		return {"code": -1, "data": {"detail": "YHDE will not talk to %s over an unencrypted connection. Use an https:// address." % http_base}}
	var url := http_base + path
	var payload: String = "" if body == null else JSON.stringify(body)
	for hop in MAX_REDIRECTS + 1:
		var req := HTTPRequest.new()
		req.timeout = 20.0
		req.max_redirects = 0
		add_child(req)
		var headers := PackedStringArray(["Content-Type: application/json", "X-YHDE: 1", "Accept: application/json"])
		if auth and token != "":
			headers.append("Authorization: Bearer " + token)
		var err := req.request(url, headers, method, payload)
		if err != OK:
			req.queue_free()
			return {"code": 0, "data": {}}
		var result: Array = await req.request_completed
		req.queue_free()
		# With no redirects allowed Godot reports 301 to 303 as "redirect limit
		# reached" but returns 307 and 308 as a plain success: look at the status.
		var answered: bool = result[0] == HTTPRequest.RESULT_SUCCESS or result[0] == HTTPRequest.RESULT_REDIRECT_LIMIT_REACHED
		if not answered:
			return {"code": 0, "data": {}}
		var status: int = result[1]
		if status in REDIRECT_STATUS:
			var target := _same_server_location(result[2])
			if target == "":
				return {"code": -1, "data": {"detail": "The server sent you to another address. YHDE did not follow it."}}
			if status == 303:
				method = HTTPClient.METHOD_GET
				payload = ""
			elif method != HTTPClient.METHOD_GET and status != 307 and status != 308:
				return {"code": -1, "data": {"detail": "The server moved this address. YHDE did not repeat the request."}}
			url = target
			continue
		var parsed = JSON.parse_string((result[3] as PackedByteArray).get_string_from_utf8())
		return {"code": status, "data": parsed if parsed is Dictionary else {}}
	return {"code": -1, "data": {"detail": "Too many redirects."}}
