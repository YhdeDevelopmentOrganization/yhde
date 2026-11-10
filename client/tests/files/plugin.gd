@tool
extends EditorPlugin
## Files test driver (see run.sh). YHDE_ROLE a|b, YHDE_SHARED dir, YHDE_RUN 1|2.
## Failures are kept in <role>.failures.json across restarts.

const LIVE := 3
const LOCKED := "res://locked"
const LOCKED_FILES := ["res://locked/f1.txt", "res://locked/f2.txt", "res://locked/f3.txt"]
const KEEP := "res://lockb/keep.txt"
const PACK := "res://addons/pack_j"
const PACK_FILES := 40
const STOP_AFTER := 12
const SENT := "res://sent_k"
const SENT_FILES := 120

var role := ""
var other := ""
var shared := ""
var session
var diag
var failures: Array = []
var sent: Array = [] # [type, path] of every file operation this editor queued
var notices: PackedStringArray = []


func _enter_tree() -> void:
	role = OS.get_environment("YHDE_ROLE")
	shared = OS.get_environment("YHDE_SHARED")
	if role == "":
		return
	other = "b" if role == "a" else "a"
	var f := shared.path_join(role + ".failures.json")
	if FileAccess.file_exists(f):
		failures = JSON.parse_string(FileAccess.get_file_as_string(f))
	_run.call_deferred()


func log_line(t: String) -> void:
	print("[files %s %.1f] %s" % [role, Time.get_ticks_msec() / 1000.0, t])


func fail(t: String) -> void:
	failures.append("%s: %s" % [role.to_upper(), t])
	log_line("FAIL " + t)
	save_failures()


func save_failures() -> void:
	var f := FileAccess.open(shared.path_join(role + ".failures.json"), FileAccess.WRITE)
	f.store_string(JSON.stringify(failures))
	f.close()


func wait(s: float) -> void:
	await get_tree().create_timer(s).timeout


func until(cond: Callable, timeout: float, what: String) -> bool:
	var t := 0.0
	while not cond.call():
		await wait(0.1)
		t += 0.1
		if t > timeout:
			fail("timeout waiting for " + what)
			return false
	return true


func mark(n: String) -> void:
	var f := FileAccess.open(shared.path_join(role + "." + n), FileAccess.WRITE)
	f.store_string("1")
	f.close()
	log_line("mark " + n)


func marked(who: String, n: String) -> bool:
	return FileAccess.file_exists(shared.path_join(who + "." + n))


func wait_marker(who: String, n: String, timeout := 300.0) -> void:
	await until(func() -> bool: return marked(who, n), timeout, who + "." + n)


func settle() -> void:
	var stable := 0.0
	var last := -1
	var t := 0.0
	while stable < 2.0:
		await wait(0.1)
		t += 0.1
		collect()
		var s: Dictionary = session.get_status()
		var a: Dictionary = s.assets
		var quiet: bool = s.state == LIVE and s.pending == 0 and s.seq >= s.head \
			and a.uploads == 0 and a.downloads == 0 and a.waiting == 0
		stable = stable + 0.1 if quiet and s.seq == last else 0.0
		last = s.seq
		if t > 180.0:
			fail("never settled: %s" % s)
			return


## Two scans of the project folder (one each second) see what changed.
func scans() -> void:
	await wait(2.5)
	await settle()


func collect() -> void:
	for e in session.take_audit():
		if str(e.type).ends_with("Asset"):
			sent.append([str(e.type), str(e.doc)])


func sent_for(prefix: String, types: Array) -> Array:
	collect()
	return sent.filter(func(e: Array) -> bool: return e[1].begins_with(prefix) and e[0] in types)


func unwritten() -> Array:
	return session.get_unwritten() if session.has_method("get_unwritten") else []


func unwritten_paths() -> PackedStringArray:
	var out := PackedStringArray()
	for u in unwritten():
		out.append(u.path)
	return out


func write_text(path: String, text: String) -> void:
	DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	var f := FileAccess.open(path, FileAccess.WRITE)
	f.store_string(text)
	f.close()


func chmod(mode: String, path: String) -> void:
	OS.execute("chmod", [mode, ProjectSettings.globalize_path(path)])


func scan_now() -> void:
	EditorInterface.get_resource_filesystem().scan()


func start() -> bool:
	await wait(1.0)
	session = get_tree().root.find_children("YhdeSession", "", true, false)[0]
	if ClassDB.class_exists("YhdeDiagnostics"):
		diag = ClassDB.instantiate("YhdeDiagnostics")
	session.notice.connect(func(text: String, level: int) -> void:
		notices.append(text)
		if level >= 1:
			log_line("notice: " + text))
	var d: Dictionary = session.get_defaults()
	var url := OS.get_environment("YHDE_URL")
	if OS.get_environment("YHDE_KEY") != "":
		session.set_access_key(url, OS.get_environment("YHDE_KEY"))
	session.start(url, d.project_id, d.branch_id, "Files " + role.to_upper())
	return await until(func() -> bool: return session.get_state() == LIVE, 60, "live")


func finish(code := -1) -> void:
	collect()
	save_failures()
	log_line("done, failures=%d" % failures.size())
	session.stop()
	get_tree().quit(code if code >= 0 else (1 if failures.size() > 0 else 0))


func _run() -> void:
	var restarted := role == "a" and marked("a", "k_restarting")
	if not await start():
		finish()
		return
	if role == "b" and OS.get_environment("YHDE_RUN") == "2":
		await b_after_stop()
		return
	if restarted:
		await a_after_restart()
		return
	mark("live")
	await wait_marker(other, "live")
	await phase_a()
	await phase_b()
	await phase_names()
	await phase_j()
	if role == "a":
		await phase_k_a()


# Phase a: B cannot write a folder A adds files to.

func phase_a() -> void:
	if role == "a":
		for p in LOCKED_FILES:
			write_text(p, "from A: " + p)
		scan_now()
		await scans()
		mark("a1")
		await wait_marker("b", "a1")
		await scans()
		for p in LOCKED_FILES:
			if not FileAccess.file_exists(p):
				fail("%s was deleted after B could not write it" % p)
		mark("a2")
		await wait_marker("b", "a3")
		return
	await wait_marker("a", "a1")
	await settle()
	await scans()
	await scans()
	for p in LOCKED_FILES:
		if FileAccess.file_exists(p):
			fail("test setup: %s was written into the read-only folder" % p)
	if not sent_for(LOCKED, ["DeleteAsset", "UpdateAsset", "RegisterAsset"]).is_empty():
		fail("sent %s for files it could not write" % [sent_for(LOCKED, ["DeleteAsset", "UpdateAsset", "RegisterAsset"])])
	var missing := LOCKED_FILES.filter(func(p: String) -> bool: return not unwritten_paths().has(p))
	if not missing.is_empty():
		fail("does not report %s as not written here (it reports %s)" % [missing, unwritten_paths()])
	mark("a1")
	await wait_marker("a", "a2")
	# Once the folder can be written, the files arrive by themselves.
	chmod("755", LOCKED)
	await until(func() -> bool: return LOCKED_FILES.all(func(p: String) -> bool:
		return FileAccess.get_file_as_string(p) == "from A: " + p), 90, "the files once the folder is writable")
	await settle()
	if not unwritten().is_empty():
		fail("still reports files as not written: %s" % [unwritten_paths()])
	mark("a3")


# Phase b: B cannot overwrite a file it has, and its copy is touched.

func phase_b() -> void:
	if role == "a":
		write_text(KEEP, "v1")
		scan_now()
		await scans()
		mark("b1")
		await wait_marker("b", "b2")
		write_text(KEEP, "v2")
		scan_now()
		await scans()
		mark("b3")
		await wait_marker("b", "b4")
		await scans()
		if FileAccess.get_file_as_string(KEEP) != "v2":
			fail("keep.txt went back to %s after B could not write it" % FileAccess.get_file_as_string(KEEP))
		mark("b5")
		await wait_marker("b", "b6")
		return
	await wait_marker("a", "b1")
	await until(func() -> bool: return FileAccess.get_file_as_string(KEEP) == "v1", 60, "keep.txt v1")
	await settle()
	chmod("555", KEEP.get_base_dir())
	mark("b2")
	await wait_marker("a", "b3")
	await settle()
	await scans()
	if FileAccess.get_file_as_string(KEEP) != "v1":
		fail("test setup: keep.txt was overwritten in the read-only folder")
	# Something touches the old copy (an import, a save, a sync tool).
	OS.execute("touch", [ProjectSettings.globalize_path(KEEP)])
	await scans()
	await scans()
	var back := sent_for(KEEP, ["UpdateAsset", "DeleteAsset"])
	if not back.is_empty():
		fail("sent its old keep.txt back: %s" % [back])
	if not unwritten_paths().has(KEEP):
		fail("does not report keep.txt as not written here")
	mark("b4")
	await wait_marker("a", "b5")
	chmod("755", KEEP.get_base_dir())
	await until(func() -> bool: return FileAccess.get_file_as_string(KEEP) == "v2", 90, "keep.txt v2 once writable")
	await settle()
	mark("b6")


# Names the server refuses: the sender is told why, and nothing is lost.

const CAPITALS := "res://base/B07.txt" # base/b07.txt exists
const WINDOWS_NAME := "res://base/what?.txt"

func phase_names() -> void:
	if role == "a":
		await wait_marker("b", "n1")
		await scans()
		if FileAccess.file_exists(CAPITALS) or FileAccess.file_exists(WINDOWS_NAME):
			fail("received a file whose name the server should have refused")
		if FileAccess.get_file_as_string("res://base/b07.txt") != "base 07\n":
			fail("base/b07.txt changed after B tried B07.txt")
		return
	notices.clear()
	write_text(CAPITALS, "capitals")
	write_text(WINDOWS_NAME, "question mark")
	scan_now()
	await scans()
	await scans()
	var told := "\n".join(notices)
	if not told.contains("only in capitals"):
		fail("was not told why B07.txt is not shared: %s" % told)
	if not told.contains("what?.txt is not shared"):
		fail("was not told why what?.txt is not shared: %s" % told)
	if FileAccess.get_file_as_string("res://base/b07.txt") != "base 07\n" or not FileAccess.file_exists(CAPITALS):
		fail("lost a file after the server refused B07.txt")
	DirAccess.remove_absolute(CAPITALS)
	DirAccess.remove_absolute(WINDOWS_NAME)
	await scans()
	mark("n1")


# Phase j: B's editor dies while it writes A's add-on.

func pack_file(i: int) -> String:
	return PACK.path_join("part_%02d.txt" % i)


func phase_j() -> void:
	if role == "b":
		if diag == null:
			fail("phase j needs the core built with -DYHDE_DIAGNOSTICS=ON")
			finish()
			return
		diag.stop_after_writes(STOP_AFTER)
		mark("j0")
		# The process ends while it writes; run.sh starts it again.
		await wait(120.0)
		fail("did not stop while writing the add-on")
		finish()
		return
	await wait_marker("b", "j0", 400)
	for i in PACK_FILES:
		write_text(pack_file(i), "part %d\n" % i)
	scan_now()
	await scans()
	mark("j1")
	await wait_marker("b", "j_back", 400)
	await scans()
	var gone := range(PACK_FILES).filter(func(i: int) -> bool: return not FileAccess.file_exists(pack_file(i)))
	if not gone.is_empty():
		fail("lost %d add-on files after B stopped while writing them" % gone.size())
	for p in LOCKED_FILES + [KEEP]:
		if not FileAccess.file_exists(p):
			fail("lost %s after B stopped" % p)


func b_after_stop() -> void:
	log_line("started again after stopping in the middle of a write")
	var report: String = session.get_crash_report() if session.has_method("get_crash_report") else ""
	if report == "":
		fail("does not say that Godot closed unexpectedly")
	elif not report.contains("pack_j"):
		fail("the crash report does not name what YHDE was doing: %s" % report.left(400))
	await settle()
	await until(func() -> bool: return range(PACK_FILES).all(func(i: int) -> bool:
		return FileAccess.get_file_as_string(pack_file(i)) == "part %d\n" % i), 120, "the whole add-on after starting again")
	await scans()
	var deleted := sent_for("res://", ["DeleteAsset"])
	if not deleted.is_empty():
		fail("deleted files for the team after stopping: %s" % [deleted])
	mark("j_back")
	await phase_k_b()


# Phase k: A restarts its editor while B is sending files.

func phase_k_a() -> void:
	await wait_marker("b", "k_sending", 300)
	mark("k_restarting")
	collect()
	save_failures()
	log_line("restarting the editor")
	session.stop()
	EditorInterface.restart_editor(true)


func sent_file(i: int) -> String:
	return SENT.path_join("s_%03d.txt" % i)


func phase_k_b() -> void:
	for i in SENT_FILES:
		write_text(sent_file(i), "sent %d\n" % i)
	scan_now()
	await until(func() -> bool:
		collect()
		return sent_for(SENT, ["RegisterAsset"]).size() > 0, 60, "B sending files")
	mark("k_sending")
	await wait_marker("a", "k_back", 400)
	await scans()
	for i in SENT_FILES:
		if not FileAccess.file_exists(sent_file(i)):
			fail("lost %s while A restarted" % sent_file(i))
	var deleted := sent_for("res://", ["DeleteAsset"])
	if not deleted.is_empty():
		fail("deleted files for the team: %s" % [deleted])
	finish()


func a_after_restart() -> void:
	log_line("started again by the editor's restart")
	await settle()
	await until(func() -> bool: return range(SENT_FILES).all(func(i: int) -> bool:
		return FileAccess.get_file_as_string(sent_file(i)) == "sent %d\n" % i), 180, "B's files after the restart")
	await scans()
	for p in LOCKED_FILES + [KEEP]:
		if not FileAccess.file_exists(p):
			fail("lost %s after restarting" % p)
	var deleted := sent_for("res://", ["DeleteAsset"])
	if not deleted.is_empty():
		fail("deleted files for the team after restarting: %s" % [deleted])
	mark("k_back")
	await wait(3.0)
	mark("done")
	finish()
