@tool
extends EditorPlugin
## End-to-end driver: plays one collaborator ("a" or "b") inside a real Godot
## editor, performing edits exactly like a user would (through the editor's
## undo/redo system) and finally dumping the resulting project state so the
## two editors can be compared. Configured through environment variables:
##   YHDE_ROLE    a | b
##   YHDE_SHARED  directory for markers and dumps (shared by both editors)
##   YHDE_URL     server websocket url
##   YHDE_KEY     server access key (optional)

const LIVE := 3
const SCENES := ["res://main2d.tscn", "res://main3d.tscn", "res://ui.tscn", "res://ui_copy.tscn", "res://roots.tscn",
	"res://part.tscn", "res://wrap.tscn", "res://host.tscn", "res://sub.tscn"]
const SKIP_PROPS := ["script", "current", "process_thread_group", "process_thread_group_order",
	"process_thread_messages", "unique_name_in_owner", "editor_description", "physics_interpolation_mode"]

var role := ""
var other := ""
var shared := ""
var session
var ur: EditorUndoRedoManager
var failures: PackedStringArray = []
## Audit (YHDE_AUDIT): what each editor action may send, by action group.
var expected := {}
var audited := 0
var extras: PackedStringArray = []


func _enter_tree() -> void:
	role = OS.get_environment("YHDE_ROLE")
	shared = OS.get_environment("YHDE_SHARED")
	if role == "":
		return
	other = "b" if role == "a" else "a"
	_run.call_deferred()


func log_line(text: String) -> void:
	print("[e2e %s %.1f] %s" % [role, Time.get_ticks_msec() / 1000.0, text])


func fail(text: String) -> void:
	failures.append(text)
	log_line("FAIL " + text)


func wait(seconds: float) -> void:
	await get_tree().create_timer(seconds).timeout


func until(cond: Callable, timeout: float, what: String) -> bool:
	var t := 0.0
	while not cond.call():
		await wait(0.1)
		t += 0.1
		if t > timeout:
			fail("timeout waiting for " + what)
			return false
	return true


func mark(name: String, content := "") -> void:
	var f := FileAccess.open(shared.path_join(role + "." + name), FileAccess.WRITE)
	f.store_string(content)
	f.close()
	log_line("mark " + name)


func wait_marker(who: String, name: String, timeout := 600.0) -> String:
	var p := shared.path_join(who + "." + name)
	await until(func() -> bool: return FileAccess.file_exists(p), timeout, who + "." + name)
	return FileAccess.get_file_as_string(p)


## All local changes committed and every committed op applied, stable for a while.
func settle(timeout := 300.0) -> void:
	var stable := 0.0
	var last_seq := -1
	var t := 0.0
	while stable < 2.0:
		await wait(0.1)
		t += 0.1
		var s: Dictionary = session.get_status()
		var quiet: bool = s.pending == 0 and s.seq >= s.head and s.state == LIVE
		if quiet and s.seq == last_seq:
			stable += 0.1
		else:
			stable = 0.0
		last_seq = s.seq
		if t > timeout:
			fail("settle timeout: %s" % s)
			return


func _run() -> void:
	await wait(1.0)
	var found := get_tree().root.find_children("YhdeSession", "", true, false)
	if found.is_empty():
		fail("no YhdeSession")
		_finish()
		return
	session = found[0]
	ur = get_undo_redo()
	session.activity.connect(func(e: Dictionary) -> void: pass)
	session.notice.connect(func(text: String, level: int) -> void:
		if level >= 1:
			log_line("notice: " + text))
	var d: Dictionary = session.get_defaults()
	var url := OS.get_environment("YHDE_URL")
	if url == "":
		url = d.url
	if OS.get_environment("YHDE_KEY") != "":
		session.set_access_key(url, OS.get_environment("YHDE_KEY"))
	# YHDE_PROJECT / YHDE_BRANCH: a separate test project with an empty branch,
	# so the test can run on a live server without touching real projects.
	var project: String = OS.get_environment("YHDE_PROJECT") if OS.get_environment("YHDE_PROJECT") != "" else d.project_id
	var branch: String = OS.get_environment("YHDE_BRANCH") if OS.get_environment("YHDE_BRANCH") != "" else d.branch_id
	session.start(url, project, branch, "Tester " + role.to_upper())
	if not await until(func() -> bool: return session.get_state() == LIVE, 60, "live"):
		_finish()
		return
	mark("live")
	await wait_marker(other, "live")

	# Phase 1: A edits everything; B only watches.
	if role == "a":
		await phase1_edits()
		await settle()
		mark("phase1", str(session.get_status().seq))
	else:
		await open_scene("res://main2d.tscn")
		var a_seq := int(await wait_marker("a", "phase1", 1800))
		await until(func() -> bool: return session.get_status().seq >= a_seq, 300, "catch up with A")
		await settle()
		var authored: int = session.get_status().authored
		if authored != 0:
			fail("B echoed %d operations while only watching" % authored)
		check_presence()
		mark("phase1")

	# Phase 2: B edits (incl. a scene that was closed on B), A watches.
	if role == "b":
		await phase2_edits()
		await settle()
		mark("phase2", str(session.get_status().seq))
	else:
		await wait_marker("b", "phase1")
		var b_seq := int(await wait_marker("b", "phase2", 900))
		await until(func() -> bool: return session.get_status().seq >= b_seq, 300, "catch up with B")
		await settle()
		mark("phase2")

	# Phase 3: both edit at the same time.
	await wait_marker(other, "phase2")
	mark("ready3")
	await wait_marker(other, "ready3")
	await phase3_concurrent()
	await settle()
	mark("phase3")
	await wait_marker(other, "phase3")
	await settle()

	# Phase 4: undo/redo through the server; A undoes while B interferes.
	if role == "a":
		await phase4_undo_a()
	else:
		await phase4_undo_b()
	await settle()

	# Phase 5: scene root changes (A), on a scene closed and then open on B.
	if role == "a":
		await phase5_roots_a()
	else:
		await phase5_roots_b()
	await settle()

	# Phase 6: instances follow their scene live (A edits part.tscn).
	if role == "a":
		await phase6_instances_a()
	else:
		await phase6_instances_b()
	await settle()

	# Phase 7: project files (assets.md): A adds, changes, moves, deletes.
	if role == "a":
		await phase7_files_a()
	else:
		await phase7_files_b()
	await settle()

	# Phase 8: a .blend made by A (with Blender) opens on B (without).
	if OS.get_environment("YHDE_BLEND_FILE") != "":
		if role == "a":
			await phase8_blend_a()
		else:
			await phase8_blend_b()
		await settle()
	# Phase 9: two people typing in the same script at once.
	await phase9_coediting()
	await settle()
	mark("settled")
	await wait_marker(other, "settled")
	await settle()
	if role == "a":
		await drift_checks()
	audit_check("run")
	# Doing nothing must send nothing (no engine-driven "edits").
	await wait(3.0)
	audit_check("idle")
	if OS.has_environment("YHDE_AUDIT"):
		log_line("audit: %d ops checked, %d unexplained" % [audited, extras.size()])
		for i in mini(extras.size(), 40):
			log_line("  extra: " + extras[i])
		if not extras.is_empty():
			fail("%d operations were not caused by an editor action" % extras.size())

	dump_state()
	_finish()


func _finish() -> void:
	var f := FileAccess.open(shared.path_join(role + ".result.json"), FileAccess.WRITE)
	f.store_string(JSON.stringify({"failures": failures, "status": session.get_status() if session else {}}))
	f.close()
	log_line("done, failures=%d" % failures.size())
	if session:
		session.stop()
	get_tree().quit(1 if failures.size() > 0 else 0)


# Editor actions (all through undo/redo, like a user)

func open_scene(path: String) -> Node:
	EditorInterface.open_scene_from_path(path)
	await wait(0.5)
	var root := EditorInterface.get_edited_scene_root()
	if root == null or root.scene_file_path != path:
		fail("could not open " + path)
	return root


func owned_nodes(root: Node) -> Array[Node]:
	var out: Array[Node] = [root]
	var stack: Array[Node] = [root]
	while not stack.is_empty():
		var n: Node = stack.pop_back()
		for c in n.get_children():
			if c.owner == root:
				out.append(c)
				stack.append(c)
	return out


func set_props(obj: Object, values: Dictionary, label := "set") -> void:
	ur.create_action(label)
	for k in values:
		ur.add_do_property(obj, k, values[k])
		ur.add_undo_property(obj, k, obj.get(k))
	ur.commit_action()
	var e := expectation()
	if obj is Node:
		var root := EditorInterface.get_edited_scene_root()
		var path := str(root.get_path_to(obj))
		e.nodes[path] = true
		for k in values:
			e.props[path + "|" + str(k)] = true
	else:
		for k in values:
			e.res[str(k)] = true


## The action just committed may change structure (and connections/groups).
func expect_structural() -> void:
	expectation().structural = true


func expectation() -> Dictionary:
	var g: int = session.get_last_action_group()
	if not expected.has(g):
		expected[g] = {"props": {}, "nodes": {}, "res": {}, "structural": false}
	return expected[g]


## Everything this editor sent must be explained by an action it performed.
func audit_check(label: String) -> void:
	for entry in session.take_audit():
		audited += 1
		var g: int = entry.group
		var e: Dictionary = expected.get(g, {"props": {}, "nodes": {}, "res": {}, "structural": false})
		var ok := false
		match entry.type:
			"ChangeProperty":
				# The action's own properties, or what the engine changed on the
				# same nodes because of it (derived values, random seeds…).
				var key: String = entry.key
				ok = e.props.has(entry.path + "|" + key) or e.nodes.has(entry.path) or (key.begins_with("@") and e.structural)
			"ChangeResourceProperty":
				ok = e.res.has(entry.key) or e.res.has("*")
			_:
				ok = true # structure: created, deleted, moved, renamed, reordered, announced
		if not ok:
			extras.append("%s: %s %s %s|%s (group %d)" % [label, entry.type, entry.doc.get_file(), entry.path, entry.key, g])


func add_node(parent: Node, node: Node, node_name: String) -> Node:
	var root := EditorInterface.get_edited_scene_root()
	node.name = node_name
	ur.create_action("add " + node_name)
	ur.add_do_method(parent, "add_child", node, true)
	ur.add_do_method(node, "set_owner", root)
	ur.add_do_reference(node)
	ur.add_undo_method(parent, "remove_child", node)
	ur.commit_action()
	expect_structural()
	return node


func do_method(obj: Object, method: String, args: Array, undo_method: String, undo_args: Array) -> void:
	ur.create_action(method)
	ur.add_do_method.callv([obj, method] + args)
	ur.add_undo_method.callv([obj, undo_method] + undo_args)
	ur.commit_action()
	expect_structural()


## A resource method the way the TileSet editor calls it; it may change any
## of the resource's (dynamic) properties.
func res_method(obj: Object, method: String, args: Array, undo_method: String, undo_args: Array) -> void:
	do_method(obj, method, args, undo_method, undo_args)
	expectation().res["*"] = true


## A node method that changes the node's own stored properties (painting cells).
func node_method(node: Node, method: String, args: Array, undo_method: String, undo_args: Array) -> void:
	do_method(node, method, args, undo_method, undo_args)
	expectation().nodes[str(EditorInterface.get_edited_scene_root().get_path_to(node))] = true


func concrete_class(base: String) -> String:
	if ClassDB.class_exists(base) and ClassDB.can_instantiate(base) and not ClassDB.is_parent_class(base, "Script"):
		return base
	var kids := Array(ClassDB.get_inheriters_from_class(base))
	kids.sort()
	for k in kids:
		if ClassDB.can_instantiate(k) and ClassDB.class_get_api_type(k) == ClassDB.API_CORE and not ClassDB.is_parent_class(k, "Script"):
			return k
	return ""


func perturb(v, p: Dictionary):
	match p.type:
		TYPE_BOOL: return not v
		TYPE_INT:
			if p.hint == PROPERTY_HINT_ENUM or p.hint == PROPERTY_HINT_FLAGS:
				return v
			return v + 1
		TYPE_FLOAT: return v + 0.25
		TYPE_STRING: return "e2e ✓ " + role
		TYPE_STRING_NAME: return StringName("e2e_" + role)
		TYPE_VECTOR2: return v + Vector2(1.5, -2.25)
		TYPE_VECTOR2I: return v + Vector2i(3, 4)
		TYPE_VECTOR3: return v + Vector3(0.5, 1.25, -0.75)
		TYPE_VECTOR3I: return v + Vector3i(1, 2, 3)
		TYPE_VECTOR4: return v + Vector4(0.5, 1, 1.5, 2)
		TYPE_RECT2: return Rect2(v.position + Vector2(1, 1), v.size + Vector2(2, 2))
		TYPE_COLOR: return Color(0.3, 0.6, 0.2, 0.9)
		TYPE_TRANSFORM2D: return v.translated(Vector2(7.5, -3.5)).rotated(0.2)
		TYPE_TRANSFORM3D: return v.translated(Vector3(1.5, 0.5, -2)).rotated(Vector3.UP, 0.3)
		TYPE_QUATERNION: return Quaternion(Vector3.UP, 0.4)
		TYPE_AABB: return AABB(v.position - Vector3.ONE, v.size + Vector3(2, 2, 2))
		TYPE_PACKED_VECTOR2_ARRAY:
			var a: PackedVector2Array = v.duplicate()
			a.append(Vector2(12.5, 7.25))
			return a
		TYPE_PACKED_FLOAT32_ARRAY:
			var a: PackedFloat32Array = v.duplicate()
			a.append(0.125)
			return a
		TYPE_PACKED_STRING_ARRAY:
			var a: PackedStringArray = v.duplicate()
			a.append("e2e")
			return a
		TYPE_OBJECT:
			if p.hint == PROPERTY_HINT_RESOURCE_TYPE and v == null:
				var cls := concrete_class(p.hint_string.split(",")[0])
				if cls != "":
					return ClassDB.instantiate(cls)
	return v


## Change every stored property of `obj` in a single undoable action.
func perturb_all(obj: Object) -> int:
	var values := {}
	for p in obj.get_property_list():
		if not (p.usage & PROPERTY_USAGE_STORAGE) or p.name in SKIP_PROPS or p.name.begins_with("metadata/"):
			continue
		var cur = obj.get(p.name)
		var nv = perturb(cur, p)
		if typeof(nv) == typeof(cur) and nv == cur:
			continue
		values[p.name] = nv
	if not values.is_empty():
		set_props(obj, values, "perturb")
	return values.size()


func zoo_classes(base: String) -> PackedStringArray:
	var out := PackedStringArray()
	var all := Array(ClassDB.get_inheriters_from_class(base))
	all.append(base)
	all.sort()
	for cls in all:
		if not ClassDB.can_instantiate(cls) or ClassDB.class_get_api_type(cls) != ClassDB.API_CORE:
			continue
		if cls.begins_with("XR") or cls.begins_with("OpenXR") or cls.contains("Editor"):
			continue
		out.append(cls)
	return out


# Phase 1 (A)

func phase1_edits() -> void:
	var total := 0
	# 2D
	var root := await open_scene("res://main2d.tscn")
	for n in owned_nodes(root):
		total += perturb_all(n)
	await wait(0.3)
	var group := root.get_node("Group")
	add_node(group, Sprite2D.new(), "NewSprite")
	do_method(group.get_node("A"), "set_name", ["Alpha"], "set_name", ["A"])
	do_method(group.get_node("B"), "reparent", [group.get_node("Alpha"), false], "reparent", [group, false])
	do_method(group, "move_child", [group.get_node("C"), 0], "move_child", [group.get_node("C"), 2])
	var line := root.get_node("Line")
	ur.create_action("delete Line")
	ur.add_do_method(root, "remove_child", line)
	ur.add_undo_method(root, "add_child", line)
	ur.add_undo_reference(line)
	ur.commit_action()
	expect_structural()
	var inst := (load("res://enemy.tscn") as PackedScene).instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE)
	add_node(root, inst, "Enemy2")
	set_props(inst, {"position": Vector2(300, 200), "rotation": 0.5})
	do_method(root.get_node("Sprite"), "add_to_group", ["enemies", true], "remove_from_group", ["enemies"])
	do_method(root.get_node("Sprite"), "connect", ["visibility_changed", Callable(root.get_node("Label"), "hide"), CONNECT_PERSIST],
		"disconnect", ["visibility_changed", Callable(root.get_node("Label"), "hide")])
	var shape_node := root.get_node("Shape")
	set_props(shape_node.shape, {"size": Vector2(77, 33)}, "shape size")
	var circle := CircleShape2D.new()
	circle.radius = 19.5
	set_props(shape_node, {"shape": circle}, "new shape")
	set_props(root.get_node("Sprite").texture, {"width": 128}, "texture width")
	# Attach a script, then edit its exported variables.
	var scripted := group.get_node("Alpha/B")
	set_props(scripted, {"script": load("res://mover.gd")}, "attach script")
	var payload := Gradient.new()
	payload.add_point(0.5, Color.ORANGE)
	var typed_points: Array[Vector2] = [Vector2(1, 2), Vector2(3.5, -4)]
	var typed_scores: Dictionary[String, int] = {"alice": 3, "bob": 7}
	set_props(scripted, {"speed": 3.5, "title": "Mover ✓", "tint": Color(0.1, 0.9, 0.4), "path_points": typed_points,
		"scores": typed_scores, "target": root.get_node("Sprite"), "payload": payload, "level": 7, "kind": 2, "flags": 5},
		"script vars")
	var anim_lib: AnimationLibrary = root.get_node("Anim").get_animation_library("")
	set_props(anim_lib.get_animation("move"), {"length": 2.5}, "anim length")
	var zoo := add_node(root, Node2D.new(), "Zoo2D")
	var zoo_nodes: Array[Node] = []
	for cls in zoo_classes("Node2D") + zoo_classes("Control"):
		var n: Node = ClassDB.instantiate(cls)
		zoo_nodes.append(add_node(zoo, n, "Z_" + cls))
	await wait(0.3)
	for n in zoo_nodes:
		total += perturb_all(n)
	log_line("2D: %d nodes in zoo, %d property changes so far" % [zoo_nodes.size(), total])
	await wait(0.5)

	# 3D (closed on B: applied to the file on disk there)
	root = await open_scene("res://main3d.tscn")
	for n in owned_nodes(root):
		total += perturb_all(n)
	set_props(root.get_node("Box").material_override, {"albedo_color": Color(0.9, 0.8, 0.1), "metallic": 0.7}, "inline material")
	set_props(root.get_node("Box").mesh, {"size": Vector3(3, 1, 2)}, "box size")
	var ext: Resource = load("res://mat.tres")
	set_props(ext, {"albedo_color": Color(0.2, 0.3, 0.9), "roughness": 0.85}, "external material")
	var pivot := root.get_node("Pivot")
	do_method(pivot.get_node("P2"), "reparent", [root, false], "reparent", [pivot, false])
	var zoo3 := add_node(root, Node3D.new(), "Zoo3D")
	var zoo3_nodes: Array[Node] = []
	for cls in zoo_classes("Node3D"):
		var n: Node = ClassDB.instantiate(cls)
		zoo3_nodes.append(add_node(zoo3, n, "Z_" + cls))
	await wait(0.3)
	for n in zoo3_nodes:
		total += perturb_all(n)
	log_line("3D: %d nodes in zoo, %d property changes so far" % [zoo3_nodes.size(), total])
	await wait(0.5)

	# UI, then "Save As" into a brand-new file (announced to peers).
	root = await open_scene("res://ui.tscn")
	for n in owned_nodes(root):
		total += perturb_all(n)
	await wait(0.5)
	EditorInterface.save_scene_as("res://ui_copy.tscn")
	await wait(1.0)
	root = EditorInterface.get_edited_scene_root()
	set_props(root.get_node("VBox/Button"), {"text": "Copied"}, "copy edit")
	# Leave main2d open with a node under inspection; B deletes it in phase 2.
	root = await open_scene("res://main2d.tscn")
	EditorInterface.edit_node(root.get_node("Group/NewSprite"))
	EditorInterface.get_selection().add_node(root.get_node("Group/NewSprite"))
	log_line("phase 1 finished: %d property changes" % total)


# Phase 2 (B)

func phase2_edits() -> void:
	var root := EditorInterface.get_edited_scene_root()
	set_props(root.get_node("Sprite"), {"modulate": Color(1, 0.5, 0.5), "position": Vector2(-40, 60)}, "b sprite")
	do_method(root.get_node("Group/C"), "set_name", ["Gamma"], "set_name", ["C"])
	add_node(root, Node2D.new(), "FromB")
	var zoo := root.get_node_or_null("Zoo2D")
	if zoo == null:
		fail("B does not have Zoo2D from A")
	else:
		var victim := zoo.get_child(0)
		ur.create_action("delete zoo child")
		ur.add_do_method(zoo, "remove_child", victim)
		ur.add_undo_method(zoo, "add_child", victim)
		ur.add_undo_reference(victim)
		ur.commit_action()
		expect_structural()
	# Undo / redo through the editor's own history.
	var sprite := root.get_node("Sprite")
	set_props(sprite, {"scale": Vector2(3, 3), "self_modulate": Color.YELLOW}, "b scale")
	await wait(0.3)
	var history := ur.get_history_undo_redo(ur.get_object_history_id(sprite))
	audit_check("before undo")
	history.undo()
	await wait(0.3)
	history.redo()
	await wait(0.3)
	history.undo()
	await wait(0.3)
	undo_audit(session.take_audit())
	# Delete the node A is inspecting right now.
	var inspected := root.get_node_or_null("Group/NewSprite")
	if inspected == null:
		fail("B does not have Group/NewSprite")
	else:
		ur.create_action("delete inspected")
		ur.add_do_method(inspected.get_parent(), "remove_child", inspected)
		ur.add_undo_method(inspected.get_parent(), "add_child", inspected)
		ur.add_undo_reference(inspected)
		ur.commit_action()
		expect_structural()
	var scripted := root.get_node_or_null("Group/Alpha/B")
	if scripted == null or scripted.get_script() == null:
		fail("B did not receive the attached script")
	elif scripted.get("speed") != 3.5 or scripted.get("target") != root.get_node("Sprite") or scripted.get("level") != 7:
		fail("B has wrong exported values: speed=%s target=%s level=%s" % [scripted.get("speed"), scripted.get("target"), scripted.get("level")])
	else:
		set_props(scripted, {"speed": 9.25, "kind": 1}, "b script vars")
	await wait(0.3)
	# A scene that was never open here: A's edits reached it on disk.
	root = await open_scene("res://ui.tscn")
	var button := root.get_node_or_null("VBox/Button")
	if button == null:
		fail("ui.tscn on B lost its Button")
	else:
		set_props(button, {"text": "From B", "custom_minimum_size": Vector2(120, 40)}, "b button")
	root = await open_scene("res://main3d.tscn")
	set_props(root.get_node("Box"), {"position": Vector3(1, 2, 3)}, "b box")
	await wait(0.3)


# Phase 3 (both at once)

func phase3_concurrent() -> void:
	var root := await open_scene("res://main2d.tscn")
	var sprite := root.get_node("Sprite")
	for i in 10:
		set_props(sprite, {"position": Vector2(1000 if role == "a" else -1000, i * 10)}, "race")
		await wait(0.05)
	add_node(root.get_node("Group"), Node2D.new(), "Conc_" + role)
	set_props(root.get_node("Label"), {"text": "winner?"} if role == "a" else {"text": "no, me"}, "race text")
	await wait(0.5)


# Phase 4: server-authoritative undo

func history_of(node: Node) -> UndoRedo:
	return ur.get_history_undo_redo(ur.get_object_history_id(node))


func phase4_undo_a() -> void:
	var root := await open_scene("res://main2d.tscn")
	var h := history_of(root)
	# Create and move a node; B then moves it too and adds a child to it.
	var p := add_node(root, Node2D.new(), "UndoP")
	set_props(p, {"position": Vector2(5, 5)}, "undo p pos")
	await settle()
	mark("u1")
	await wait_marker("b", "u1")
	await settle()
	h.undo() # my move: back to my old value, over B's later change
	await settle()
	if p.position != Vector2.ZERO:
		fail("undo did not restore UndoP.position: %s" % p.position)
	h.undo() # my create: the node goes, B's child with it
	await settle()
	if root.get_node_or_null("UndoP") != null:
		fail("undo of a create left UndoP behind")
	h.redo() # comes back as it was when undone, B's child included
	await settle()
	var back := root.get_node_or_null("UndoP")
	if back == null or back.get_node_or_null("FromB") == null:
		fail("redo lost UndoP or B's child")
	# Redo restores what was there before the undo (B's later move), so an
	# undo followed by a redo never changes the scene.
	h.redo()
	await settle()
	if back != null and back.position != Vector2(9, 9):
		fail("redo of the move gave %s, want B's (9, 9)" % back.position)

	# Delete a subtree holding an embedded resource, then undo the delete.
	var holder := add_node(root, Node2D.new(), "Holder")
	var shape_node := CollisionShape2D.new()
	add_node(holder, shape_node, "Shape")
	var rect := RectangleShape2D.new()
	rect.size = Vector2(10, 20)
	set_props(shape_node, {"shape": rect}, "holder shape")
	var index := holder.get_index()
	ur.create_action("delete holder")
	ur.add_do_method(root, "remove_child", holder)
	ur.add_undo_method(root, "add_child", holder)
	ur.add_undo_method(root, "move_child", holder, index)
	ur.add_undo_reference(holder)
	ur.commit_action()
	expect_structural()
	await settle()
	h.undo()
	await settle()
	var restored := root.get_node_or_null("Holder")
	if restored == null or restored.get_index() != index:
		fail("undo of a delete did not restore Holder at index %d" % index)
	elif not (restored.get_node("Shape").shape is RectangleShape2D) or restored.get_node("Shape").shape.size != Vector2(10, 20):
		fail("undo of a delete lost the embedded shape")

	# Reparent, then undo / redo / undo: back at its old index.
	var label := root.get_node("Label")
	var label_index := label.get_index()
	do_method(label, "reparent", [root.get_node("Group"), false], "reparent", [root, false])
	await settle()
	h.undo()
	await settle()
	h.redo()
	await settle()
	h.undo()
	await settle()
	if label.get_parent() != root or label.get_index() != label_index:
		fail("undo of a reparent left Label at %s #%d (want root #%d)" % [label.get_parent().name, label.get_index(), label_index])

	# An undo the editor itself cannot do: B moved the node away first.
	add_node(root.get_node("Group"), Node2D.new(), "Victim")
	await settle()
	mark("u2")
	await wait_marker("b", "u2")
	await settle()
	h.undo()
	await settle()
	if root.find_child("Victim", true, false) != null:
		fail("undo after a peer moved the node did not delete it")

	# Tiles: an embedded TileSet edited like the TileSet editor does, cells
	# painted, a tile and a layer removed (whole-state replace), then undone.
	var tiles := add_node(root, TileMapLayer.new(), "Tiles") as TileMapLayer
	var ts := TileSet.new()
	set_props(tiles, {"tile_set": ts}, "tile set")
	var atlas_tex := GradientTexture2D.new()
	atlas_tex.width = 64
	atlas_tex.height = 64
	var src := TileSetAtlasSource.new()
	src.texture = atlas_tex
	res_method(ts, "add_source", [src, 0], "remove_source", [0])
	res_method(ts, "add_physics_layer", [-1], "remove_physics_layer", [0])
	res_method(ts, "add_custom_data_layer", [-1], "remove_custom_data_layer", [0])
	for c in [Vector2i(0, 0), Vector2i(1, 0), Vector2i(2, 0)]:
		res_method(src, "create_tile", [c], "remove_tile", [c])
	set_props(src, {"0:0/0/modulate": Color(1, 0.4, 0.4), "1:0/0/z_index": 2}, "tile data")
	set_props(src, {"0:0/0/physics_layer_0/polygons_count": 1}, "tile polygon count")
	set_props(src, {"0:0/0/physics_layer_0/polygon_0/points": PackedVector2Array([Vector2(-8, -8), Vector2(8, -8), Vector2(8, 8)])}, "tile polygon")
	set_props(src, {"1:0/0/custom_data_0": 42}, "tile custom data")
	for x in 4:
		node_method(tiles, "set_cell", [Vector2i(x, 0), 0, Vector2i(x % 3, 0), 0], "erase_cell", [Vector2i(x, 0)])
	node_method(tiles, "erase_cell", [Vector2i(0, 0)], "set_cell", [Vector2i(0, 0), 0, Vector2i(0, 0), 0])
	await settle()
	res_method(src, "remove_tile", [Vector2i(2, 0)], "create_tile", [Vector2i(2, 0)])
	await settle()
	res_method(ts, "remove_custom_data_layer", [0], "add_custom_data_layer", [0])
	await settle()
	h.undo() # custom data layer back
	await settle()
	h.undo() # tile 2:0 back
	await settle()
	if not src.has_tile(Vector2i(2, 0)) or ts.get_custom_data_layers_count() != 1:
		fail("undo of tile edits: has 2:0=%s, custom layers=%d" % [src.has_tile(Vector2i(2, 0)), ts.get_custom_data_layers_count()])
	mark("u3")
	log_line("undo checks done")


func phase4_undo_b() -> void:
	var root := EditorInterface.get_edited_scene_root()
	await wait_marker("a", "u1")
	await settle()
	var p := root.get_node_or_null("UndoP")
	if p == null:
		fail("B does not have UndoP")
	else:
		set_props(p, {"position": Vector2(9, 9)}, "b moves UndoP")
		add_node(p, Node2D.new(), "FromB")
	await settle()
	mark("u1")
	await wait_marker("a", "u2")
	await settle()
	var victim := root.get_node_or_null("Group/Victim")
	if victim == null:
		fail("B does not have Group/Victim")
	else:
		do_method(victim, "reparent", [root, false], "reparent", [root.get_node("Group"), false])
	await settle()
	mark("u2")
	await wait_marker("a", "u3")
	await settle()
	var back := root.get_node_or_null("UndoP")
	if back == null or back.position != Vector2(9, 9) or back.get_node_or_null("FromB") == null:
		fail("B sees UndoP wrong after A's undo/redo")
	var holder := root.get_node_or_null("Holder")
	if holder == null or holder.get_node_or_null("Shape") == null or holder.get_node("Shape").shape.size != Vector2(10, 20):
		fail("B did not get Holder back exactly")
	if root.find_child("Victim", true, false) != null:
		fail("B still has Victim")
	var b_tiles := root.get_node_or_null("Tiles") as TileMapLayer
	if b_tiles == null or b_tiles.tile_set == null or not b_tiles.tile_set.has_source(0):
		fail("B did not get the Tiles layer with its TileSet")
	else:
		var b_src := b_tiles.tile_set.get_source(0) as TileSetAtlasSource
		if not b_src.has_tile(Vector2i(2, 0)) or b_tiles.tile_set.get_custom_data_layers_count() != 1 				or b_tiles.tile_set.get_physics_layers_count() != 1 				or b_src.get_tile_data(Vector2i(0, 0), 0).get_collision_polygons_count(0) != 1 				or b_tiles.get_cell_source_id(Vector2i(0, 0)) != -1 or b_tiles.get_cell_atlas_coords(Vector2i(3, 0)) != Vector2i(0, 0):
			fail("B's tiles differ after A's tile edits and undo")


# Phase 5: scene root changes

func scene_tree_dock() -> Node:
	return EditorInterface.get_base_control().find_children("*", "SceneTreeDock", true, false)[0]


func editor_node() -> Node:
	for c in get_tree().root.get_children():
		if c.get_class() == "EditorNode":
			return c
	return null


## Change Type exactly as the editor does it (SceneTreeDock::replace_node).
func change_type(node: Node, cls: String) -> Node:
	var dock := scene_tree_dock()
	var replacement: Node = ClassDB.instantiate(cls)
	ur.create_action("Change type of node(s)", UndoRedo.MERGE_DISABLE, node)
	ur.add_do_method(dock, "replace_node", node, replacement, true)
	ur.add_do_reference(replacement)
	dock.replace_node(node, replacement, true)
	ur.add_undo_method(dock, "replace_node", replacement, node, false)
	ur.add_undo_reference(node)
	ur.commit_action(false)
	expect_structural()
	return replacement


func owned_by(base: Node, n: Node, out: Array[Node]) -> void:
	if n.owner == base:
		out.append(n)
	for c in n.get_children():
		owned_by(base, c, out)


## Make Scene Root exactly as the editor does it (SceneTreeDock TOOL_MAKE_ROOT).
func make_root(node: Node) -> void:
	var root := EditorInterface.get_edited_scene_root()
	var en := editor_node()
	var owned: Array[Node] = []
	owned_by(root, root, owned)
	ur.create_action("Make node as Root")
	ur.add_do_method(node.get_parent(), "remove_child", node)
	ur.add_do_method(en, "set_edited_scene", node)
	ur.add_do_method(node, "add_child", root, true)
	ur.add_do_method(node, "set_scene_file_path", root.scene_file_path)
	ur.add_do_method(root, "set_scene_file_path", "")
	ur.add_do_method(node, "set_owner", null)
	ur.add_do_method(root, "set_owner", node)
	ur.add_do_method(node, "set_unique_name_in_owner", false)
	for n in owned:
		if n != node:
			ur.add_do_method(n, "set_owner", node)
	ur.add_undo_method(root, "set_scene_file_path", root.scene_file_path)
	ur.add_undo_method(node, "set_scene_file_path", "")
	ur.add_undo_method(node, "remove_child", root)
	ur.add_undo_method(en, "set_edited_scene", root)
	ur.add_undo_method(node.get_parent(), "add_child", node, true)
	ur.add_undo_method(node.get_parent(), "move_child", node, node.get_index(false))
	ur.add_undo_method(root, "set_owner", null)
	ur.add_undo_method(node, "set_owner", root)
	ur.add_undo_method(node, "set_unique_name_in_owner", node.unique_name_in_owner)
	for n in owned:
		if n != root:
			ur.add_undo_method(n, "set_owner", root)
	ur.commit_action()
	expect_structural()


func phase5_roots_a() -> void:
	var root := await open_scene("res://roots.tscn")
	var h := history_of(root)
	change_type(root.get_node("Swap"), "Marker2D")
	change_type(root, "CanvasGroup")
	root = EditorInterface.get_edited_scene_root()
	if root.get_class() != "CanvasGroup" or root.position != Vector2(7, 8):
		fail("A: root is %s at %s after Change Type" % [root.get_class(), root.position])
	await settle()
	mark("r1")
	await wait_marker("b", "r1")
	make_root(root.get_node("Keep"))
	await settle()
	var now := EditorInterface.get_edited_scene_root()
	if now.name != "Keep" or now.get_node_or_null("RootR/Other") == null:
		fail("A: Make Scene Root gave %s" % now.name)
	mark("r2")
	await wait_marker("b", "r2")
	h = history_of(EditorInterface.get_edited_scene_root())
	h.undo() # make root
	await settle()
	h.undo() # root type
	await settle()
	h.redo()
	await settle()
	now = EditorInterface.get_edited_scene_root()
	if now.name != "RootR" or now.get_class() != "CanvasGroup" or now.get_node_or_null("Keep/Leaf") == null:
		fail("A: after undo/redo the root is %s (%s)" % [now.name, now.get_class()])
	mark("r3")
	log_line("root checks done")


func phase5_roots_b() -> void:
	await wait_marker("a", "r1")
	await settle()
	# The scene was closed here: the changes went to the file.
	var root := await open_scene("res://roots.tscn")
	if root.get_class() != "CanvasGroup" or root.get_node_or_null("Swap") == null or root.get_node("Swap").get_class() != "Marker2D":
		fail("B: after Change Type on disk: root %s, Swap %s" % [root.get_class(), root.get_node_or_null("Swap")])
	elif root.get_node("Swap").get_child_count() != 1 or root.get_node("Swap").position != Vector2(-5, 3):
		fail("B: Change Type lost Swap's child or position")
	mark("r1")
	await wait_marker("a", "r2")
	await settle()
	# Open here: the tab reloads with the new root.
	root = EditorInterface.get_edited_scene_root()
	if root == null or root.name != "Keep" or root.get_node_or_null("RootR/Swap") == null:
		fail("B: Make Scene Root on an open scene gave %s" % (root.name if root else "nothing"))
	mark("r2")
	await wait_marker("a", "r3")
	await settle()
	root = EditorInterface.get_edited_scene_root()
	if root == null or root.name != "RootR" or root.get_class() != "CanvasGroup" or root.get_node_or_null("Keep/Leaf") == null:
		fail("B: after A's undo/redo the root is %s (%s)" % [root.name if root else "nothing", root.get_class() if root else ""])


# Phase 6: live instances

func open_root(path: String) -> Node:
	for r in EditorInterface.get_open_scene_roots():
		if r.scene_file_path == path:
			return r
	return null


func phase6_instances_a() -> void:
	# host.tscn open in the background, part.tscn in front: A's own edits of
	# the part reach A's host too.
	await open_scene("res://host.tscn")
	var part := await open_scene("res://part.tscn")
	await wait_marker("b", "i0")
	set_props(part.get_node("Body"), {"modulate": Color(0.9, 0.2, 0.3)}, "part body")
	add_node(part, Node2D.new(), "Hat")
	set_props(part.get_node("Tip"), {"position": Vector2(40, 5)}, "part tip")
	await settle()
	await wait(1.0)
	var host := open_root("res://host.tscn")
	if host == null or host.get_node_or_null("P1/Hat") == null or host.get_node("P1/Body").modulate != Color(0.9, 0.2, 0.3):
		fail("A: its own host did not follow the part")
	mark("i1")
	await wait_marker("b", "i1")
	set_props(part.get_node("Hat"), {"position": Vector2(0, -20)}, "hat")
	do_method(part.get_node("Hat"), "set_name", ["Cap"], "set_name", ["Hat"])
	await settle()
	mark("i2")
	await wait_marker("b", "i2")
	log_line("instance checks done")


func phase6_instances_b() -> void:
	# host.tscn and the inherited sub.tscn open, part.tscn closed.
	await open_scene("res://sub.tscn")
	var host := await open_scene("res://host.tscn")
	mark("i0")
	await wait_marker("a", "i1")
	await settle()
	await wait(1.0)
	host = EditorInterface.get_edited_scene_root()
	var p1 := host.get_node_or_null("P1")
	if p1 == null or p1.get_node_or_null("Hat") == null:
		fail("B: P1 did not get the part's new Hat")
	elif p1.get_node("Body").modulate != Color(0.9, 0.2, 0.3) or p1.get_node("Tip").position != Vector2(40, 5):
		fail("B: P1 did not get the part's new values")
	elif p1.position != Vector2(100, 0):
		fail("B: P1 lost its own position override: %s" % p1.position)
	var p2 := host.get_node_or_null("P2")
	if p2 == null or p2.get_node_or_null("Hat") == null:
		fail("B: P2 (editable children) did not follow the part")
	elif p2.get_node("Body").modulate != Color(0, 1, 0) or not host.is_editable_instance(p2):
		fail("B: P2 lost its editable-children override")
	if host.get_node_or_null("W1/Inner/Hat") == null:
		fail("B: the nested instance W1/Inner did not follow the part")
	var sub := open_root("res://sub.tscn")
	if sub == null or sub.get_node_or_null("Hat") == null or sub.get_node_or_null("Extra") == null:
		fail("B: the inherited scene did not follow the part")
	# The rest of the host keeps its undo history.
	set_props(host.get_node("Marker"), {"position": Vector2(3, 3)}, "marker")
	history_of(host).undo()
	await settle()
	if host.get_node("Marker").position != Vector2.ZERO:
		fail("B: undo in the host stopped working after the refresh")
	# Now with the part open here too (edited in memory, not on disk).
	await open_scene("res://part.tscn")
	host = await open_scene("res://host.tscn")
	mark("i1")
	await wait_marker("a", "i2")
	await settle()
	await wait(1.0)
	host = EditorInterface.get_edited_scene_root()
	var cap := host.get_node_or_null("P1/Cap")
	if cap == null or host.get_node_or_null("P1/Hat") != null or cap.position != Vector2(0, -20):
		fail("B: with the part open, P1 did not follow (Cap=%s)" % cap)
	mark("i2")


# Project files

const BULK := 25
const CODE_HELD := ["res://plugins/evil/build.sh", "res://plugins/evil/tool_thing.gd"]
const CODE_PLAIN := ["res://plugins/evil/plain.gd"]
const CODE_RENAMED := "res://plugins/evil/sneaky.dll"
const CODE_TEXT := {
	"res://plugins/evil/build.sh": "echo not run (e2e)\n",
	"res://plugins/evil/tool_thing.gd": "@tool\nextends Node\n",
	"res://plugins/evil/plain.gd": "extends Node\n",
	"res://plugins/evil/sneaky.dll": "just text, renamed (e2e)\n",
}

func files_quiet() -> bool:
	var a: Dictionary = session.get_status().assets
	return a.uploads == 0 and a.downloads == 0 and a.waiting == 0 and a.importing == 0


## The file scanner has had a pass over what changed, and everything is out.
func files_settle() -> void:
	await wait(2.5)
	await until(files_quiet, 120, "file transfers")
	await settle()


func write_text(path: String, text: String) -> void:
	DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	var f := FileAccess.open(path, FileAccess.WRITE)
	f.store_string(text)
	f.close()


func import_now(path: String) -> void:
	var fs := EditorInterface.get_resource_filesystem()
	fs.scan()
	await until(func() -> bool: return FileAccess.file_exists(path + ".import") and not fs.is_scanning() and not fs.is_importing(), 60, "import of " + path)


func uid_of(path: String) -> String:
	var cf := ConfigFile.new()
	cf.load(path + ".import")
	return str(cf.get_value("remap", "uid", ""))


func phase7_files_a() -> void:
	# A texture dropped into the project and used right away.
	var img := Image.create(16, 16, false, Image.FORMAT_RGBA8)
	img.fill(Color(0.2, 0.6, 1.0))
	DirAccess.make_dir_recursive_absolute("res://art")
	img.save_png("res://art/hero.png")
	await import_now("res://art/hero.png")
	var root := await open_scene("res://main2d.tscn")
	var hero := add_node(root, Sprite2D.new(), "Hero")
	set_props(hero, {"texture": load("res://art/hero.png"), "position": Vector2(64, 32)}, "hero texture")
	# A material that needs the texture (a dependency), a script, data files.
	var mat := StandardMaterial3D.new()
	mat.albedo_texture = load("res://art/hero.png")
	DirAccess.make_dir_recursive_absolute("res://materials")
	ResourceSaver.save(mat, "res://materials/hero_mat.tres")
	write_text("res://scripts/mover2.gd", "extends Node2D

func _process(delta: float) -> void:
	position.x += delta
")
	write_text("res://data/cfg.json", "{\"speed\": 3}
")
	write_text("res://data/old.txt", "remove me
")
	for i in BULK:
		write_text("res://bulk/f%02d.txt" % i, "bulk %d
" % i)
	EditorInterface.get_resource_filesystem().scan()
	await files_settle()
	mark("files1", uid_of("res://art/hero.png"))

	# Change, move and delete; a project setting.
	await wait_marker("b", "files1")
	write_text("res://scripts/mover2.gd", "extends Node2D

func _process(delta: float) -> void:
	position.x += 2.0 * delta
")
	DirAccess.rename_absolute("res://data/cfg.json", "res://data/moved.json")
	DirAccess.remove_absolute("res://data/old.txt")
	ProjectSettings.set_setting("application/config/description", "e2e files")
	ProjectSettings.save()
	await files_settle()

	# A whole folder gone at once is held until someone confirms.
	for i in BULK:
		DirAccess.remove_absolute("res://bulk/f%02d.txt" % i)
	DirAccess.remove_absolute("res://bulk")
	await until(func() -> bool: return session.get_held_deletions().size() == BULK, 30, "held deletions")
	await files_settle()
	mark("files2")
	await wait_marker("b", "files2")
	session.restore_deletions()
	await until(func() -> bool: return FileAccess.file_exists("res://bulk/f%02d.txt" % (BULK - 1)), 60, "restored files")
	await files_settle()
	if session.get_held_deletions().size() != 0:
		fail("A: deletions still held after restoring")
	mark("files3")

	# Code from a teammate waits on B until B accepts it (security.md). The
	# rename turns a harmless text file into a native library.
	for p in CODE_HELD + CODE_PLAIN:
		write_text(p, CODE_TEXT[p])
	write_text("res://notes/readme.txt", CODE_TEXT[CODE_RENAMED])
	EditorInterface.get_resource_filesystem().scan()
	await files_settle()
	DirAccess.rename_absolute("res://notes/readme.txt", CODE_RENAMED)
	EditorInterface.get_resource_filesystem().scan()
	await files_settle()
	mark("code1", FileAccess.get_file_as_string("res://plugins/evil/tool_thing.gd.uid"))
	await wait_marker("b", "code1")
	await settle()
	# B holding them must never delete or revert them for anyone.
	for p in CODE_HELD + CODE_PLAIN + [CODE_RENAMED]:
		if FileAccess.get_file_as_string(p) != CODE_TEXT[p]:
			fail("A: %s changed or vanished while B held it" % p)
	mark("code2")
	await wait_marker("b", "code2")
	log_line("file checks done (A)")


func phase7_files_b() -> void:
	var uid := await wait_marker("a", "files1")
	await settle()
	await until(func() -> bool: return FileAccess.file_exists("res://bulk/f%02d.txt" % (BULK - 1)) and FileAccess.file_exists("res://materials/hero_mat.tres"), 120, "A's new files")
	await settle()
	if uid_of("res://art/hero.png") != uid:
		fail("B: hero.png has uid %s, A has %s" % [uid_of("res://art/hero.png"), uid])
	var root := await open_scene("res://main2d.tscn")
	var hero := root.get_node_or_null("Hero") as Sprite2D
	if hero == null or hero.texture == null or hero.texture.resource_path != "res://art/hero.png":
		fail("B: the Hero sprite did not get its texture (%s)" % (hero.texture if hero else "no Hero"))
	var mat := load("res://materials/hero_mat.tres") as StandardMaterial3D
	if mat == null or mat.albedo_texture == null or mat.albedo_texture.resource_path != "res://art/hero.png":
		fail("B: the material lost its texture")
	mark("files1")

	await wait_marker("a", "files2")
	await settle()
	await until(func() -> bool: return FileAccess.file_exists("res://data/moved.json") and not FileAccess.file_exists("res://data/old.txt"), 60, "A's changes")
	await until(func() -> bool: return FileAccess.get_file_as_string("res://scripts/mover2.gd").contains("2.0 * delta"), 60, "script update")
	await until(func() -> bool: return ProjectSettings.get_setting("application/config/description", "") == "e2e files", 60, "project setting")
	if FileAccess.file_exists("res://data/cfg.json"):
		fail("B: data/cfg.json should have moved")
	var kept := 0
	for i in BULK:
		if FileAccess.file_exists("res://bulk/f%02d.txt" % i):
			kept += 1
	if kept != BULK:
		fail("B: %d of %d files of A's held folder deletion are gone here" % [BULK - kept, BULK])
	mark("files2")
	await wait_marker("a", "files3")
	await settle()

	var tool_uid := await wait_marker("a", "code1")
	await settle()
	await until(func() -> bool: return session.get_held_code().size() == CODE_HELD.size() + 1, 60, "held code files")
	await until(func() -> bool: return FileAccess.file_exists(CODE_PLAIN[0]) and not FileAccess.file_exists("res://notes/readme.txt"), 60, "plain script and the rename")
	for p in CODE_HELD + [CODE_RENAMED]:
		if FileAccess.file_exists(p):
			fail("B: %s was written without being accepted" % p)
	var held := PackedStringArray()
	for h in session.get_held_code():
		held.append(h.path)
	for p in CODE_HELD + [CODE_RENAMED]:
		if not held.has(p):
			fail("B: %s is not in the held list %s" % [p, held])
	if session.get_held_code()[0].by != "Tester A":
		fail("B: held code sender is %s" % session.get_held_code()[0].by)
	mark("code1")
	await wait_marker("a", "code2")
	session.accept_held_code()
	await until(func() -> bool: return session.get_held_code().is_empty() and FileAccess.file_exists(CODE_RENAMED), 60, "accepted code written")
	await files_settle()
	for p in CODE_HELD + CODE_PLAIN + [CODE_RENAMED]:
		if FileAccess.get_file_as_string(p) != CODE_TEXT[p]:
			fail("B: %s differs from A's after accepting" % p)
	if FileAccess.get_file_as_string("res://plugins/evil/tool_thing.gd.uid") != tool_uid:
		fail("B: tool_thing.gd got uid %s, A has %s" % [FileAccess.get_file_as_string("res://plugins/evil/tool_thing.gd.uid"), tool_uid])
	mark("code2")
	log_line("file checks done (B)")


# Tool-imported files (.blend)

const BLEND := "res://models/monkey.blend"


func phase8_blend_a() -> void:
	var es := EditorInterface.get_editor_settings()
	es.set_setting("filesystem/import/blender/blender_path", OS.get_environment("YHDE_BLENDER"))
	DirAccess.make_dir_recursive_absolute(BLEND.get_base_dir())
	DirAccess.copy_absolute(OS.get_environment("YHDE_BLEND_FILE"), ProjectSettings.globalize_path(BLEND))
	await import_now(BLEND)
	var root := await open_scene("res://main3d.tscn")
	var scene := load(BLEND) as PackedScene
	if scene == null:
		fail("A: Blender did not import the .blend (is YHDE_BLENDER right?)")
		return
	var monkey := scene.instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE)
	add_node(root, monkey, "Monkey")
	await files_settle()
	mark("blend")
	log_line("blend added (A)")


func phase8_blend_b() -> void:
	# This editor has no Blender: the result A's Blender made must be used.
	EditorInterface.get_editor_settings().set_setting("filesystem/import/blender/blender_path", "/nonexistent/blender")
	await wait_marker("a", "blend", 900)
	await settle()
	await until(func() -> bool: return FileAccess.file_exists(BLEND), 120, "the .blend")
	await settle()
	var scene := load(BLEND) as PackedScene
	if scene == null:
		fail("B: the .blend does not load without Blender")
	var root := await open_scene("res://main3d.tscn")
	var monkey := root.get_node_or_null("Monkey")
	if monkey == null or monkey.scene_file_path != BLEND:
		fail("B: the Monkey instance did not arrive (%s)" % monkey)
	elif monkey.find_children("*", "MeshInstance3D", true, false).is_empty():
		fail("B: the Monkey instance has no mesh")
	log_line("blend checks done (B)")


# Live co-editing (text_editing.md)

const SHARED_SCRIPT := "res://scripts/mover2.gd"
const CLOSED_SCRIPT := "res://mover.gd"


func open_code(path: String) -> CodeEdit:
	EditorInterface.edit_script(load(path))
	await wait(0.5)
	# The plugin binds open shared scripts a few times a second.
	await until(func() -> bool: return session.is_text_live(path), 30, "live text of " + path)
	return EditorInterface.get_script_editor().get_current_editor().get_base_editor() as CodeEdit


## Types like a person: one character at a time at the caret.
func type_text(editor: CodeEdit, line: int, column: int, text: String) -> void:
	editor.set_caret_line(line)
	editor.set_caret_column(column)
	for ch in text:
		editor.insert_text_at_caret(ch)
		await wait(0.02)


func press_undo(editor: CodeEdit) -> void:
	var ev := InputEventKey.new()
	ev.keycode = KEY_Z
	ev.ctrl_pressed = true
	ev.command_or_control_autoremap = true
	ev.pressed = true
	editor.gui_input.emit(ev)


func phase9_coediting() -> void:
	var editor := await open_code(SHARED_SCRIPT)
	var first := editor.get_line(0)
	mark("code_open")
	await wait_marker(other, "code_open")
	# Both type into the same line at the same time: A at its start, B at its end.
	if role == "a":
		await type_text(editor, 0, 0, "# alpha ")
		await type_text(editor, 2, 0, "# A was here\n")
	else:
		await type_text(editor, 0, first.length(), " # bravo")
		await type_text(editor, editor.get_line_count() - 1, 0, "# B was here\n")
	await settle()
	mark("typed")
	await wait_marker(other, "typed")
	await settle()
	await wait(1.0)
	var text := editor.text
	mark("text1", text)
	var theirs := await wait_marker(other, "text1")
	if theirs != text:
		fail("%s: shared script differs after typing at once:\n--- mine\n%s\n--- theirs\n%s" % [role.to_upper(), text, theirs])
	for piece in ["# alpha ", " # bravo", "# A was here", "# B was here"]:
		if not text.contains(piece):
			fail("%s: the shared script lost %s" % [role.to_upper(), piece])

	# Undo takes back only your own typing.
	if role == "a":
		press_undo(editor)
		await settle()
		mark("undone")
	else:
		await wait_marker("a", "undone")
		await settle()
		await wait(1.0)
	var after := editor.text
	if after.contains("# A was here") or not after.contains("# B was here") or not after.contains(" # bravo"):
		fail("%s: undo should remove only A's last typing:\n%s" % [role.to_upper(), after])
	mark("text2", after)
	if await wait_marker(other, "text2") != after:
		fail("%s: shared script differs after undo" % role.to_upper())

	# Saving saves for everyone.
	if role == "a":
		EditorInterface.get_script_editor().save_all_scripts()
		await settle()
		mark("saved")
	else:
		await wait_marker("a", "saved")
		await settle()
		await until(func() -> bool: return FileAccess.get_file_as_string(SHARED_SCRIPT) == editor.text, 30, "saved script on disk")
		if editor.get_version() != editor.get_saved_version():
			fail("B: the shared script still shows unsaved changes after A saved")

	# A script only A has open: B's file on disk follows the typing.
	if role == "a":
		var closed := await open_code(CLOSED_SCRIPT)
		await type_text(closed, 0, 0, "# typed while B has it closed\n")
		await settle()
		mark("closed_typed", closed.text)
	else:
		var expected := await wait_marker("a", "closed_typed")
		await settle()
		await until(func() -> bool: return FileAccess.get_file_as_string(CLOSED_SCRIPT) == expected, 30, "closed script on disk")
	log_line("co-editing checks done")


# Engine drift (A)

## Layout, text scrolling, animation preview and a tool script change things on
## their own; only the three edits made here may be sent.
func drift_checks() -> void:
	var root := await open_scene("res://drift.tscn")
	await wait(1.5)
	audit_check("drift open")
	set_props(root.get_node("Box"), {"size": Vector2(260, 320)}, "box size")
	set_props(root.get_node("Text"), {"text": "line one\n".repeat(40) + "a long line that scrolls sideways ".repeat(10)}, "text")
	set_props(root.get_node("Wrap"), {"text": "grow ".repeat(60)}, "wrap")
	var anim: AnimationPlayer = root.get_node("Anim")
	anim.play("wiggle")
	await wait(2.5)
	anim.stop()
	await wait(2.5)
	audit_check("drift")
	var tooly := root.get_node("Tooly")
	if int(tooly.get_meta("ticks", 0)) < 10:
		fail("the tool script did not run in the editor (drift test is not exercising it)")
	log_line("drift checks done")


# Checks

## Undo and redo of "b scale" may only touch what that action touched.
func undo_audit(entries: Array) -> void:
	for entry in entries:
		audited += 1
		if entry.type == "ChangeProperty" and not (entry.key in ["scale", "self_modulate"] and entry.path == "Sprite"):
			extras.append("undo/redo: %s %s|%s" % [entry.type, entry.path, entry.key])

func check_presence() -> void:
	var peers: Array = session.get_peers()
	if peers.size() != 1:
		fail("expected 1 peer, got %d" % peers.size())
		return
	var p: Dictionary = peers[0]
	if not String(p.name).ends_with(other.to_upper()):
		fail("unexpected peer name " + p.name)
	log_line("presence ok: %s in %s (%s)" % [p.name, p.scene_name, p.tool])


# Dump

func norm(v, root: Node) -> Variant:
	match typeof(v):
		TYPE_FLOAT:
			var r := snappedf(v, 0.0001)
			return 0.0 if r == 0.0 else r
		TYPE_VECTOR2, TYPE_VECTOR3, TYPE_VECTOR4, TYPE_RECT2, TYPE_COLOR, TYPE_QUATERNION, TYPE_PLANE, TYPE_AABB, \
		TYPE_TRANSFORM2D, TYPE_TRANSFORM3D, TYPE_BASIS, TYPE_PROJECTION:
			var s := var_to_str(v)
			var parts := s.split("(", true, 1)
			var nums := parts[1].trim_suffix(")").split(",")
			var out := []
			for n in nums:
				var r := snappedf(n.to_float(), 0.001)
				out.append(0.0 if r == 0.0 else r)
			return parts[0] + str(out)
		TYPE_PACKED_FLOAT32_ARRAY, TYPE_PACKED_FLOAT64_ARRAY, TYPE_PACKED_VECTOR2_ARRAY, TYPE_PACKED_VECTOR3_ARRAY, \
		TYPE_PACKED_COLOR_ARRAY, TYPE_PACKED_VECTOR4_ARRAY:
			return norm(Array(v), root)
		TYPE_ARRAY:
			var out := []
			for e in v:
				out.append(norm(e, root))
			return out
		TYPE_DICTIONARY:
			var keys := []
			for k in v:
				keys.append([str(norm(k, root)), k])
			keys.sort()
			var out := []
			for k in keys:
				out.append([k[0], norm(v[k[1]], root)])
			return out
		TYPE_OBJECT:
			if v == null:
				return null
			if v is Node:
				return "node:" + str(root.get_path_to(v)) if root.is_ancestor_of(v) or v == root else "node:?"
			if v is Resource:
				var path: String = v.resource_path
				if path != "" and not path.contains("::"):
					return "res:" + path
				var props := {}
				for p in v.get_property_list():
					if p.usage & PROPERTY_USAGE_STORAGE and not p.name in ["resource_path", "resource_scene_unique_id"]:
						props[p.name] = norm(v.get(p.name), root)
				return {"class": v.get_class(), "props": props}
			return "object:" + v.get_class()
		TYPE_STRING_NAME, TYPE_NODE_PATH:
			return str(v)
	return v


## CodeEdit returns its delimiter set in insertion-history order, so the same
## set can read back in a different order; compare it as a set.
const SET_LIKE := ["delimiter_strings", "comment_delimiters", "string_delimiters"]


func dump_node(n: Node, root: Node) -> Dictionary:
	var props := {}
	for p in n.get_property_list():
		if p.usage & PROPERTY_USAGE_STORAGE:
			var v = norm(n.get(p.name), root)
			if p.name in SET_LIKE and v is Array:
				v.sort()
			props[p.name] = v
	var groups := []
	for g in n.get_groups():
		if not String(g).begins_with("_"):
			groups.append(String(g))
	groups.sort()
	var conns := []
	for s in n.get_signal_list():
		for c in n.get_signal_connection_list(s.name):
			if c.flags & CONNECT_PERSIST:
				var t: Object = c.callable.get_object()
				conns.append("%s->%s.%s" % [s.name, root.get_path_to(t) if t is Node else "?", c.callable.get_method()])
	conns.sort()
	# Only what the scene file holds: controls such as Tree add unowned helper
	# children of their own, auto-named from a per-editor counter.
	var children := []
	for c in n.get_children():
		if c.owner == root:
			children.append(String(c.name))
	return {"class": n.get_class(), "props": props, "groups": groups, "conns": conns, "children": children}


func dump_tree(root: Node) -> Dictionary:
	var out := {}
	for n in owned_nodes(root):
		out[str(root.get_path_to(n))] = dump_node(n, root)
	return out


func dump_state() -> void:
	var state := {}
	var open := EditorInterface.get_open_scenes()
	var open_roots := {}
	for r in EditorInterface.get_open_scene_roots():
		open_roots[r.scene_file_path] = r
	for path in SCENES:
		if open_roots.has(path):
			state[path] = {"source": "editor", "tree": dump_tree(open_roots[path])}
		elif FileAccess.file_exists(path):
			var ps := ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_IGNORE) as PackedScene
			var root := ps.instantiate(PackedScene.GEN_EDIT_STATE_MAIN)
			state[path] = {"source": "disk", "tree": dump_tree(root)}
			root.free()
		else:
			state[path] = {"source": "missing"}
	var mat: Resource = load("res://mat.tres")
	state["res://mat.tres"] = norm_resource_file(mat)
	var f := FileAccess.open(shared.path_join(role + ".dump.json"), FileAccess.WRITE)
	f.store_string(JSON.stringify(state, " ", true))
	f.close()
	log_line("dumped " + str(state.keys()))


func norm_resource_file(res: Resource) -> Dictionary:
	var props := {}
	for p in res.get_property_list():
		if p.usage & PROPERTY_USAGE_STORAGE and p.name != "resource_path":
			props[p.name] = norm(res.get(p.name), null)
	return props
