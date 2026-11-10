@tool
extends EditorPlugin
## Mixed-version driver (see run.sh). YHDE_ROLE a (newer Godot) | b (older).

const LIVE := 3
## Properties the newer Godot has and the older one does not, with the value
## A sets (the default is different).
const NEW_PROPS := {"Sun": {"shadow_contact_shadows_allow": false}, "Ui": {"resize_font_to_fit": true}}

var role := ""
var other := ""
var shared := ""
var session
var failures: Array = []
var sent: Array = []


func _enter_tree() -> void:
	role = OS.get_environment("YHDE_ROLE")
	shared = OS.get_environment("YHDE_SHARED")
	if role != "":
		other = "b" if role == "a" else "a"
		_run.call_deferred()


func log_line(t: String) -> void:
	print("[mixed %s %.1f] %s" % [role, Time.get_ticks_msec() / 1000.0, t])


func fail(t: String) -> void:
	failures.append(t)
	log_line("FAIL " + t)


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


func wait_marker(who: String, n: String) -> void:
	await until(func() -> bool: return FileAccess.file_exists(shared.path_join(who + "." + n)), 300, who + "." + n)


func collect() -> void:
	for e in session.take_audit():
		sent.append("%s %s %s" % [e.type, str(e.doc).get_file(), e.key])


func settle() -> void:
	var stable := 0.0
	var last := -1
	var t := 0.0
	while stable < 2.0 and t < 120.0:
		await wait(0.1)
		t += 0.1
		collect()
		var s: Dictionary = session.get_status()
		var quiet: bool = s.state == LIVE and s.pending == 0 and s.seq >= s.head
		stable = stable + 0.1 if quiet and s.seq == last else 0.0
		last = s.seq
	await wait(1.0)
	collect()


func edit(node: Node, props: Dictionary) -> void:
	var ur := get_undo_redo()
	ur.create_action("edit " + node.name)
	for k in props:
		ur.add_do_property(node, k, props[k])
		ur.add_undo_property(node, k, node.get(k))
	ur.commit_action()


func open(path: String) -> Node:
	EditorInterface.open_scene_from_path(path)
	await wait(0.5)
	return EditorInterface.get_edited_scene_root()


func root_of(path: String) -> Node:
	for r in EditorInterface.get_open_scene_roots():
		if r.scene_file_path == path:
			return r
	return null


func _run() -> void:
	await wait(1.0)
	session = get_tree().root.find_children("YhdeSession", "", true, false)[0]
	if OS.get_environment("YHDE_KEY") != "":
		session.set_access_key(OS.get_environment("YHDE_URL"), OS.get_environment("YHDE_KEY"))
	var d: Dictionary = session.get_defaults()
	session.start(OS.get_environment("YHDE_URL"), d.project_id, d.branch_id, "Mixed " + role.to_upper())
	if await until(func() -> bool: return session.get_state() == LIVE, 60, "live"):
		log_line(Engine.get_version_info().string)
		mark("live")
		await wait_marker(other, "live")
		await settle()
		sent.clear() # both register the starting files
		if role == "a":
			await run_a()
		else:
			await run_b()
	collect()
	log_line("sent: %s" % [sent])
	log_line("done, failures=%d" % failures.size())
	session.stop()
	get_tree().quit(1 if failures.size() > 0 else 0)


func run_a() -> void:
	var closed := await open("res://closed.tscn")
	var scene := await open("res://open.tscn")
	await wait_marker("b", "opened")
	for root in [scene, closed]:
		for n in NEW_PROPS:
			for k in NEW_PROPS[n]:
				if not (k in root.get_node(n)):
					fail("test setup: this Godot has no %s.%s" % [n, k])
			edit(root.get_node(n), NEW_PROPS[n])
		edit(root.get_node("Ui"), {"text": "from A"})
	await settle()
	var before := FileAccess.get_sha256("res://open.tscn")
	mark("a1")
	await wait_marker("b", "b1")
	await settle()
	await wait(2.0)
	for path in ["res://open.tscn", "res://closed.tscn"]:
		var root := root_of(path)
		if root.get_node("Text").text != "from B":
			fail("%s: B's text change did not arrive (%s)" % [path.get_file(), root.get_node("Text").text])
		for n in NEW_PROPS:
			for k in NEW_PROPS[n]:
				if root.get_node(n).get(k) != NEW_PROPS[n][k]:
					fail("%s: %s.%s went back to %s after B edited and saved" % [path.get_file(), n, k, root.get_node(n).get(k)])
	if root_of("res://open.tscn").get_node("Sun").light_energy != 2.0:
		fail("B's light energy did not arrive")
	if FileAccess.get_sha256("res://open.tscn") != before:
		fail("open.tscn on disk was replaced by B's copy")
	# Saved here, the file keeps the newer properties.
	for path in ["res://closed.tscn", "res://open.tscn"]:
		await open(path)
		EditorInterface.save_scene()
	await settle()
	if not FileAccess.get_file_as_string("res://closed.tscn").contains("resize_font_to_fit = true"):
		fail("closed.tscn saved here lost resize_font_to_fit")
	mark("a2")
	await wait_marker("b", "b2")


func run_b() -> void:
	var scene := await open("res://open.tscn")
	mark("opened")
	await wait_marker("a", "a1")
	await settle()
	await wait(2.0)
	if scene.get_node("Ui").text != "from A":
		fail("A's text change did not arrive in the open scene")
	var closed: PackedScene = ResourceLoader.load("res://closed.tscn", "", ResourceLoader.CACHE_MODE_IGNORE)
	var c := closed.instantiate() if closed else null
	if c == null or c.get_node("Ui").text != "from A":
		fail("A's text change did not reach closed.tscn")
	if c:
		c.free()
	var echoed := sent.duplicate()
	if not echoed.is_empty():
		fail("sent operations while only receiving: %s" % [echoed])
	# B edits both scenes and saves them.
	edit(scene.get_node("Sun"), {"light_energy": 2.0})
	edit(scene.get_node("Text"), {"text": "from B"})
	EditorInterface.save_scene()
	var other_scene := await open("res://closed.tscn")
	edit(other_scene.get_node("Text"), {"text": "from B"})
	EditorInterface.save_scene()
	await settle()
	for e in sent:
		for n in NEW_PROPS:
			for k in NEW_PROPS[n]:
				if str(e).contains(k):
					fail("sent the newer property %s: %s" % [k, e])
	mark("b1")
	await wait_marker("a", "a2")
	await settle()
	mark("b2")
