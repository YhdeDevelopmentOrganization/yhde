@tool
extends EditorPlugin
## Catch-up driver (see run.sh).
##   YHDE_CU_ROLE writer: makes history (scene edits and many versions of a
##                script and a texture), saves, quits.
##   YHDE_CU_ROLE joiner: joins that history with the scene closed, reports how
##                long it took, how often the files on disk changed and the
##                longest frame.
##   YHDE_CU_OUT  result file
##   YHDE_CU_STAY seconds the joiner stays once caught up (the records get a backup)

const LIVE := 3
const EDITS := 1500
const VERSIONS := 15
const WATCHED: Array[String] = ["res://code.gd", "res://texture.png", "res://big.tscn"]

var session


func _enter_tree() -> void:
	if OS.get_environment("YHDE_CU_ROLE") != "":
		_run.call_deferred()


func log_line(t: String) -> void:
	print("[catchup %.1f] %s" % [Time.get_ticks_msec() / 1000.0, t])


func wait(s: float) -> void:
	await get_tree().create_timer(s).timeout


func _start() -> void:
	session = get_tree().root.find_children("YhdeSession", "", true, false)[0]
	session.join_check.connect(func(_info: Dictionary) -> void: session.confirm_join())
	var d: Dictionary = session.get_defaults()
	session.start(OS.get_environment("YHDE_URL"), d.project_id, d.branch_id, "Catchup " + OS.get_environment("YHDE_CU_ROLE"))


func _settled(seconds: float) -> bool:
	var stable := 0.0
	var last := -1
	var t := 0.0
	while stable < seconds and t < 1800.0:
		await wait(0.1)
		t += 0.1
		var s: Dictionary = session.get_status()
		var quiet: bool = s.state == LIVE and s.pending == 0 and s.seq >= s.head
		stable = stable + 0.1 if quiet and s.seq == last else 0.0
		last = s.seq
	return stable >= seconds


func _write_version(i: int) -> void:
	var f := FileAccess.open("res://code.gd", FileAccess.WRITE)
	f.store_string("extends Node\nconst VERSION := %d\n" % i)
	f.close()
	var image := Image.create(256, 256, false, Image.FORMAT_RGB8)
	image.fill(Color(float(i) / VERSIONS, 0.5, 0.25))
	image.save_png("res://texture.png")
	EditorInterface.get_resource_filesystem().scan()


## Like a builder script: the children are replaced by new nodes of the same
## names, outside the undo history.
func _rebuild(parent: Node) -> void:
	await wait(2.0)
	var root := EditorInterface.get_edited_scene_root()
	var names: Array[String] = []
	for c in parent.get_children():
		names.append(String(c.name))
		parent.remove_child(c)
		c.free()
	for n in names:
		var fresh := Node2D.new()
		fresh.name = n
		parent.add_child(fresh)
		fresh.owner = root
	await wait(3.0)


func _run_writer() -> void:
	_start()
	await wait(1.0)
	while session.get_state() != LIVE:
		await wait(0.1)
	EditorInterface.open_scene_from_path("res://big.tscn")
	await wait(2.0)
	var root := EditorInterface.get_edited_scene_root()
	var children := root.get_children()
	var ur := get_undo_redo()
	for i in EDITS:
		var n: Node2D = children[i % children.size()]
		ur.create_action("edit")
		ur.add_do_property(n, "position", Vector2(i, i))
		ur.add_undo_property(n, "position", n.position)
		ur.commit_action()
		if i % 20 == 19:
			await get_tree().process_frame
		if i % (EDITS / VERSIONS) == 0:
			_write_version(i / (EDITS / VERSIONS))
			await wait(3.0)
	await _rebuild(root.get_node("Rebuilt"))
	EditorInterface.save_scene()
	if not await _settled(5.0):
		log_line("writer never settled: %s" % session.get_status())
	log_line("writer done: %s" % session.get_status())
	session.stop()
	get_tree().quit()


func _run_joiner() -> void:
	var out := OS.get_environment("YHDE_CU_OUT")
	await wait(1.0)
	_start()
	var started := Time.get_ticks_msec()
	var last_frame := started
	var longest := 0
	var hashes := {}
	var changes := {}
	for p in WATCHED:
		hashes[p] = FileAccess.get_sha256(p)
		changes[p] = 0
	var finished := 0
	var quiet_since := -1
	while Time.get_ticks_msec() - started < 1800000:
		await get_tree().process_frame
		var now := Time.get_ticks_msec()
		longest = maxi(longest, now - last_frame)
		last_frame = now
		for p in WATCHED:
			var h := FileAccess.get_sha256(p)
			if h != hashes[p]:
				hashes[p] = h
				changes[p] += 1
		var s: Dictionary = session.get_status()
		if s.state == LIVE and s.head > 0 and s.seq >= s.head:
			if quiet_since < 0:
				quiet_since = now
				finished = now
			if now - quiet_since > 3000 + 1000 * int(OS.get_environment("YHDE_CU_STAY")):
				break
		else:
			quiet_since = -1
	await wait(1.0)
	var result := {
		"seconds": (finished - started) / 1000.0,
		"status": str(session.get_status()),
		"longest_frame_ms": longest,
		"file_changes": changes,
		"hashes": {},
	}
	for p in WATCHED:
		result.hashes[p] = FileAccess.get_sha256(p)
	var scene: PackedScene = ResourceLoader.load("res://big.tscn", "", ResourceLoader.CACHE_MODE_IGNORE)
	var root := scene.instantiate()
	var sum := Vector2.ZERO
	for c in root.get_children():
		sum += c.position
	result["scene_position_sum"] = str(sum)
	var rebuilt: Array[String] = []
	for c in root.get_node("Rebuilt").get_children():
		rebuilt.append(String(c.name))
	rebuilt.sort()
	result["rebuilt_names"] = ",".join(rebuilt)
	root.free()
	FileAccess.open(out, FileAccess.WRITE).store_string(JSON.stringify(result))
	log_line("joiner done: %s" % result)
	session.stop()
	get_tree().quit()


func _run() -> void:
	await wait(1.0)
	if OS.get_environment("YHDE_CU_ROLE") == "writer":
		await _run_writer()
	else:
		await _run_joiner()
