@tool
extends EditorPlugin
## Access-key driver (see run.sh): a server started with an access key must
## refuse editors without it (no retry loop) and admit the right one; the key
## must be stored outside the project and readable only by the user.
##   YHDE_URL server websocket url, YHDE_KEY the server's key

const OFFLINE := 0
const LIVE := 3
var session
var failures: PackedStringArray = []


func _enter_tree() -> void:
	if OS.get_environment("YHDE_KEY") != "":
		_run.call_deferred()


func log_line(t: String) -> void:
	print("[access %.1f] %s" % [Time.get_ticks_msec() / 1000.0, t])


func check(ok: bool, what: String) -> void:
	log_line(("ok   " if ok else "FAIL ") + what)
	if not ok:
		failures.append(what)


func wait(s: float) -> void:
	await get_tree().create_timer(s).timeout


func until(cond: Callable, timeout: float) -> bool:
	var t := 0.0
	while not cond.call() and t < timeout:
		await wait(0.1)
		t += 0.1
	return cond.call()


func _refused(url: String, d: Dictionary, what: String) -> void:
	session.start(url, d.project_id, d.branch_id, "Access test")
	var went_offline: bool = await until(func() -> bool: return session.get_state() == OFFLINE, 15.0)
	check(went_offline, what + ": goes offline")
	check(session.get_status().error == "Sign-in or invite code refused", what + ": says the key was refused")
	await wait(3.0)
	check(session.get_state() == OFFLINE, what + ": does not retry")


func _run() -> void:
	await wait(1.0)
	session = get_tree().root.find_children("YhdeSession", "", true, false)[0]
	var url := OS.get_environment("YHDE_URL")
	var key := OS.get_environment("YHDE_KEY")
	var d: Dictionary = session.get_defaults()

	session.set_access_key(url, "")
	check(not session.has_access_key(url), "no key stored")
	await _refused(url, d, "no key")

	session.set_access_key(url, "wrong-key-0123456789abcdef")
	check(session.has_access_key(url), "wrong key stored")
	await _refused(url, d, "wrong key")

	session.set_access_key(url, "has spaces in it")
	session.set_access_key(url, key)
	session.start(url, d.project_id, d.branch_id, "Access test")
	check(await until(func() -> bool: return session.get_state() == LIVE, 30.0), "right key: live")
	check(not session.has_access_key(url + "/other"), "key is tied to its server address")

	var path := EditorInterface.get_editor_paths().get_data_dir().path_join("yhde/access_keys.json")
	check(FileAccess.file_exists(path), "key file in the editor data folder")
	check(not ProjectSettings.globalize_path(path).begins_with(ProjectSettings.globalize_path("res://")), "key file outside the project")
	if OS.get_name() != "Windows":
		check(FileAccess.get_unix_permissions(path) == (FileAccess.UNIX_READ_OWNER | FileAccess.UNIX_WRITE_OWNER), "key file readable by the owner only")
	var meta := ProjectSettings.globalize_path("res://.godot/editor/project_metadata.cfg")
	check(not FileAccess.get_file_as_string(meta).contains(key), "key not in project metadata")

	session.stop()
	session.set_access_key(url, "")
	log_line("done: %d failure(s)" % failures.size())
	get_tree().quit(0 if failures.is_empty() else 1)
