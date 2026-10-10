@tool
extends EditorPlugin
## Instance stress test: a teammate (A) edits part.tscn over
## and over, while the other editor (B) has host.tscn in front with a node
## inside the P1 instance selected and in the inspector, makes its own edits
## in the host, undoes and redoes them, and keeps working past the 5 s grace
## period. Covers both ways a teammate's edit reaches an instance: part.tscn
## closed in B (the file is rewritten) and open in a background tab.
## B ends with a disconnect right after a refresh, then reconnects.
## Env: YHDE_ROLE (a|b), YHDE_SHARED, YHDE_URL, YHDE_KEY, ROUNDS (default 40),
## GAP (seconds between A's edits, default 0.4). Run through run.sh.

const LIVE := 3

var role := ""
var shared := ""
var session
var ur: EditorUndoRedoManager
var failures: PackedStringArray = []


func _enter_tree() -> void:
	role = OS.get_environment("YHDE_ROLE")
	shared = OS.get_environment("YHDE_SHARED")
	if role != "":
		_run.call_deferred()


func log_line(text: String) -> void:
	print("[inst %s %.1f] %s" % [role, Time.get_ticks_msec() / 1000.0, text])


func fail(text: String) -> void:
	failures.append(text)
	log_line("FAIL " + text)


func wait(seconds: float) -> void:
	await get_tree().create_timer(seconds).timeout


func mark(name: String) -> void:
	var f := FileAccess.open(shared.path_join(role + "." + name), FileAccess.WRITE)
	f.close()
	log_line("mark " + name)


func marked(who: String, name: String) -> bool:
	return FileAccess.file_exists(shared.path_join(who + "." + name))


func wait_marker(who: String, name: String, timeout := 600.0) -> void:
	var t := 0.0
	while not marked(who, name):
		await wait(0.1)
		t += 0.1
		if t > timeout:
			fail("timeout waiting for %s.%s" % [who, name])
			return


func until_live(timeout := 60.0) -> bool:
	var t := 0.0
	while session.get_state() != LIVE:
		await wait(0.1)
		t += 0.1
		if t > timeout:
			fail("never live (state %d)" % session.get_state())
			return false
	return true


func settle(timeout := 120.0) -> void:
	var stable := 0.0
	var t := 0.0
	while stable < 1.5 and t < timeout:
		await wait(0.1)
		t += 0.1
		var s: Dictionary = session.get_status()
		stable = stable + 0.1 if s.pending == 0 and s.seq >= s.head and s.state == LIVE else 0.0


func open_scene(path: String) -> Node:
	EditorInterface.open_scene_from_path(path)
	await wait(0.6)
	return EditorInterface.get_edited_scene_root()


func set_prop(obj: Object, prop: String, value: Variant, context: Object, label: String) -> void:
	ur.create_action(label, UndoRedo.MERGE_DISABLE, context)
	ur.add_do_property(obj, prop, value)
	ur.add_undo_property(obj, prop, obj.get(prop))
	ur.commit_action()


func _run() -> void:
	await wait(1.0)
	var found := get_tree().root.find_children("YhdeSession", "", true, false)
	if found.is_empty():
		fail("no YhdeSession")
		_finish()
		return
	session = found[0]
	ur = get_undo_redo()
	session.notice.connect(func(text: String, level: int) -> void:
		if level >= 1:
			log_line("notice: " + text))
	var d: Dictionary = session.get_defaults()
	var url := OS.get_environment("YHDE_URL")
	if OS.get_environment("YHDE_KEY") != "":
		session.set_access_key(url, OS.get_environment("YHDE_KEY"))
	session.start(url, d.project_id, d.branch_id, "Tester " + role.to_upper())
	if not await until_live():
		_finish()
		return
	mark("live")
	await wait_marker("b" if role == "a" else "a", "live")
	if role == "a":
		await teammate()
	else:
		await inspector_holder()
	_finish()


## A: the teammate. Edits part.tscn: values, new nodes, removals, renames.
func teammate() -> void:
	var rounds := int(OS.get_environment("ROUNDS")) if OS.get_environment("ROUNDS") != "" else 40
	var gap := float(OS.get_environment("GAP")) if OS.get_environment("GAP") != "" else 0.4
	var part := await open_scene("res://part.tscn")
	for phase in ["closed", "open", "background"]:
		await wait_marker("b", "ready_" + phase)
		var added: Array[String] = []
		for i in rounds:
			var body: Node = part.get_node("Body")
			set_prop(body, "modulate", Color(0.1 + 0.02 * (i % 40), 0.2, 0.3), part, "body %d" % i)
			set_prop(part.get_node("Tip"), "position", Vector2(10 + i, 0), part, "tip %d" % i)
			if i % 3 == 0:
				var hat := Sprite2D.new()
				hat.name = "Hat%d" % i
				ur.create_action("add hat %d" % i, UndoRedo.MERGE_DISABLE, part)
				ur.add_do_method(part, "add_child", hat, true)
				ur.add_do_property(hat, "owner", part)
				ur.add_do_reference(hat)
				ur.add_undo_method(part, "remove_child", hat)
				ur.commit_action()
				added.append(hat.name)
			var gone: Node = part.get_node_or_null(added.pop_front()) if i % 5 == 4 and not added.is_empty() else null
			if gone != null:
				ur.create_action("remove %s" % gone.name, UndoRedo.MERGE_DISABLE, part)
				ur.add_do_method(part, "remove_child", gone)
				ur.add_undo_method(part, "add_child", gone, true)
				ur.add_undo_property(gone, "owner", part)
				ur.add_undo_reference(gone)
				ur.commit_action()
			if i % 7 == 6:
				# Undo the last change: the teammate's undo also reaches B.
				ur.get_history_undo_redo(ur.get_object_history_id(part)).undo()
			await wait(gap)
		await settle()
		mark("done_" + phase)
	await wait_marker("b", "finished", 900)


## B: holds a node inside the refreshed instance, in the selection and the
## inspector, and keeps working in the host while A's edits arrive.
func inspector_holder() -> void:
	await open_scene("res://sub.tscn")
	var host := await open_scene("res://host.tscn")
	for phase in ["closed", "open"]:
		if phase == "open":
			await open_scene("res://part.tscn")
			host = await open_scene("res://host.tscn")
		mark("ready_" + phase)
		var k := 0
		while not marked("a", "done_" + phase):
			host = EditorInterface.get_edited_scene_root()
			var body: Node = host.get_node_or_null("P1/Body") if host else null
			if body == null:
				fail("no P1/Body in the host")
				break
			EditorInterface.get_selection().clear()
			EditorInterface.get_selection().add_node(body)
			EditorInterface.edit_node(body)
			# Read what the inspector shows, as a person looking at it would.
			var _shown := [body.get("modulate"), body.get("position"), body.get_parent().get("position")]
			# Own edits on the instance and inside it, then undo and redo some.
			set_prop(host.get_node("P1"), "position", Vector2(100 + k % 5, 0), host, "p1 %d" % k)
			if host.get_node_or_null("P2/Body"):
				set_prop(host.get_node("P2/Body"), "rotation", 0.01 * (k % 10), host, "p2 body %d" % k)
			if k % 4 == 3:
				var h: UndoRedo = ur.get_history_undo_redo(ur.get_object_history_id(host))
				h.undo()
				await wait(0.05)
				h.redo()
			k += 1
			await wait(0.25)
		await settle()
		# Past the grace period, with the old nodes freed under the selection.
		await wait(6.0)
		var h2: UndoRedo = ur.get_history_undo_redo(ur.get_object_history_id(host))
		for i in 6:
			h2.undo()
			await wait(0.05)
		for i in 6:
			h2.redo()
			await wait(0.05)
		log_line("phase %s: %d own edits while A edited the part" % [phase, k])
	# Background tab: the host keeps a node inside the instance selected, then
	# another tab is shown while A edits the part. The editor saved the host's
	# selection as it switched; switching back and saving every scene (what
	# pressing Play does) use it.
	host = await open_scene("res://host.tscn")
	var held: Node = host.get_node("P1/Body")
	EditorInterface.get_selection().clear()
	EditorInterface.get_selection().add_node(held)
	EditorInterface.edit_node(held)
	await wait(0.3)
	await open_scene("res://sub.tscn")
	mark("ready_background")
	await wait_marker("a", "done_background")
	await settle()
	await wait(7.0) # past the grace period: the old nodes are freed
	EditorInterface.save_all_scenes()
	log_line("saved all scenes with the host in the background")
	host = await open_scene("res://host.tscn")
	await wait(1.0)
	for n in EditorInterface.get_selection().get_selected_nodes():
		if not is_instance_valid(n) or not n.is_inside_tree():
			fail("a selected node is not in the scene after switching back")
	var now_selected := EditorInterface.get_selection().get_selected_nodes()
	if now_selected.size() != 1 or now_selected[0] != host.get_node_or_null("P1/Body"):
		fail("switching back did not select P1/Body again: %s" % [now_selected])
	var shown: Object = EditorInterface.get_inspector().get_edited_object()
	if shown is Node and not (shown as Node).is_inside_tree():
		fail("the inspector shows a node that left the scene")
	log_line("switched back to the host: %d selected, inspector on %s" % [EditorInterface.get_selection().get_selected_nodes().size(),
			(host.get_path_to(shown) if shown is Node and (shown as Node).is_inside_tree() else str(shown))])
	# Leave right after a refresh (the graveyard is swept at once), then come back.
	mark("finished")
	await wait(0.35)
	session.stop()
	await wait(1.0)
	var d: Dictionary = session.get_defaults()
	session.start(OS.get_environment("YHDE_URL"), d.project_id, d.branch_id, "Tester B")
	await until_live()
	await settle()
	await wait(6.0)
	host = EditorInterface.get_edited_scene_root()
	var p1 := host.get_node_or_null("P1") if host else null
	if p1 == null or p1.get_node_or_null("Body") == null:
		fail("the host lost P1 after the run")


func _finish() -> void:
	log_line("done, failures=%d" % failures.size())
	get_tree().quit(1 if failures.size() > 0 else 0)
