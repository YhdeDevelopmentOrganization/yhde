@tool
extends EditorPlugin
## Resilience driver (see run.sh): edits made while the server is down must
## survive reconnects and even an editor restart, then converge.
##   YHDE_ROLE a|b, YHDE_SHARED dir, YHDE_PHASE 1|2 (a restarts for phase 2)

const LIVE := 3
var role := ""
var shared := ""
var session
var failures: PackedStringArray = []


func _enter_tree() -> void:
	role = OS.get_environment("YHDE_ROLE")
	shared = OS.get_environment("YHDE_SHARED")
	if role != "":
		_run.call_deferred()


func log_line(t: String) -> void:
	print("[res %s %.1f] %s" % [role, Time.get_ticks_msec() / 1000.0, t])


func wait(s: float) -> void:
	await get_tree().create_timer(s).timeout


func mark(n: String) -> void:
	FileAccess.open(shared.path_join(role + "." + n), FileAccess.WRITE).store_string("1")
	log_line("mark " + n)


func has(n: String) -> bool:
	return FileAccess.file_exists(shared.path_join(n))


func wait_for(n: String, timeout := 300.0) -> void:
	var t := 0.0
	while not has(n) and t < timeout:
		await wait(0.1)
		t += 0.1
	if not has(n):
		failures.append("timeout waiting for " + n)


func settle() -> void:
	var stable := 0.0
	var last := -1
	var t := 0.0
	while stable < 2.0 and t < 120.0:
		await wait(0.1)
		t += 0.1
		var s: Dictionary = session.get_status()
		var quiet: bool = s.state == LIVE and s.pending == 0 and s.seq >= s.head
		stable = stable + 0.1 if quiet and s.seq == last else 0.0
		last = s.seq
	if stable < 2.0:
		failures.append("never settled: %s" % session.get_status())


func edit(obj: Object, props: Dictionary) -> void:
	var ur := get_undo_redo()
	ur.create_action("edit")
	for k in props:
		ur.add_do_property(obj, k, props[k])
		ur.add_undo_property(obj, k, obj.get(k))
	ur.commit_action()


func add(parent: Node, node_name: String) -> void:
	var ur := get_undo_redo()
	var root := EditorInterface.get_edited_scene_root()
	var n := Node2D.new()
	n.name = node_name
	ur.create_action("add")
	ur.add_do_method(parent, "add_child", n, true)
	ur.add_do_method(n, "set_owner", root)
	ur.add_do_reference(n)
	ur.add_undo_method(parent, "remove_child", n)
	ur.commit_action()


func _run() -> void:
	await wait(1.0)
	session = get_tree().root.find_children("YhdeSession", "", true, false)[0]
	EditorInterface.open_scene_from_path("res://main2d.tscn")
	await wait(0.5)
	var d: Dictionary = session.get_defaults()
	if OS.get_environment("YHDE_KEY") != "":
		session.set_access_key(OS.get_environment("YHDE_URL"), OS.get_environment("YHDE_KEY"))
	session.start(OS.get_environment("YHDE_URL"), d.project_id, d.branch_id, "Res " + role)
	var phase := OS.get_environment("YHDE_PHASE")
	var root := EditorInterface.get_edited_scene_root()

	if phase == "2":
		# Restarted editor: the unsent edits live only in the persisted queue.
		log_line("restarted with %d pending" % session.get_status().pending)
		if session.get_status().pending == 0:
			failures.append("queue was not restored after restart")
	else:
		var t := 0.0
		while session.get_state() != LIVE and t < 60.0:
			await wait(0.1)
			t += 0.1
		mark("live")
		await wait_for("harness.down")
		await wait(1.0)
		if session.get_state() == LIVE:
			failures.append("still LIVE after the server went down")
		# Edit while offline.
		if role == "a":
			edit(root.get_node("Sprite"), {"position": Vector2(111, 222), "rotation": 0.75})
			add(root.get_node("Group"), "OfflineA")
		else:
			edit(root.get_node("Label"), {"text": "offline b"})
			add(root.get_node("Group"), "OfflineB")
		await wait(0.5)
		log_line("pending while offline: %d" % session.get_status().pending)
		mark("edited")
		if role == "a":
			# Simulate a crash/quit without saving: no stop(), no save.
			await wait(1.0)
			get_tree().quit()
			return

	await wait_for("harness.up")
	await settle()
	mark("settled")
	await wait_for(("b" if role == "a" else "a") + ".settled")
	await settle()
	var s := root.get_node("Sprite")
	var state := {
		"sprite_position": str(s.position), "sprite_rotation": snappedf(s.rotation, 0.0001),
		"label": root.get_node("Label").text,
		"group_children": Array(root.get_node("Group").get_children().map(func(c): return String(c.name))),
	}
	FileAccess.open(shared.path_join(role + ".state.json"), FileAccess.WRITE).store_string(JSON.stringify(state))
	FileAccess.open(shared.path_join(role + ".failures.json"), FileAccess.WRITE).store_string(JSON.stringify(failures))
	log_line("state %s failures %s" % [state, failures])
	session.stop()
	get_tree().quit(1 if failures.size() > 0 else 0)
