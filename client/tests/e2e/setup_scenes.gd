extends SceneTree
## Builds the shared starting project for the end-to-end test: a 2D scene, a
## 3D scene, a UI scene, an instanced sub-scene and an external material.
## Run with: godot --headless --path <project> -s res://setup_scenes.gd


## Own every node the way the editor does; nodes inside instanced sub-scenes
## keep belonging to their instance.
func _own(node: Node, root: Node) -> void:
	node.owner = root
	if node.scene_file_path != "":
		return
	for c in node.get_children():
		_own(c, root)


func _save(root: Node, path: String) -> void:
	_own_children(root)
	var ps := PackedScene.new()
	var err := ps.pack(root)
	assert(err == OK)
	err = ResourceSaver.save(ps, path)
	assert(err == OK)
	root.free()


func _own_children(root: Node) -> void:
	for c in root.get_children():
		_own(c, root)


func _init() -> void:
	# A plain (non-tool) script with every common kind of exported variable.
	var script := FileAccess.open("res://mover.gd", FileAccess.WRITE)
	script.store_string("""extends Node2D

enum Kind { WALK, RUN, FLY }

@export var speed := 1.0
@export var title := "mover"
@export var tint: Color = Color.WHITE
@export var path_points: Array[Vector2] = []
@export var scores: Dictionary[String, int] = {}
@export var target: Node
@export var payload: Resource
@export_range(0, 10) var level := 1
@export var kind: Kind = Kind.WALK
@export_flags("A", "B", "C") var flags := 0
""")
	script.close()

	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(0.8, 0.1, 0.1)
	mat.roughness = 0.3
	ResourceSaver.save(mat, "res://mat.tres")

	# Instanced sub-scene.
	var enemy := Node2D.new()
	enemy.name = "Enemy"
	var body := Sprite2D.new()
	body.name = "Body"
	var grad := GradientTexture2D.new()
	grad.width = 32
	grad.height = 32
	body.texture = grad
	enemy.add_child(body)
	var hit := CollisionShape2D.new()
	hit.name = "Hit"
	var circle := CircleShape2D.new()
	circle.radius = 12.0
	hit.shape = circle
	enemy.add_child(hit)
	_save(enemy, "res://enemy.tscn")

	# 2D scene.
	var main2d := Node2D.new()
	main2d.name = "Main2D"
	var sprite := Sprite2D.new()
	sprite.name = "Sprite"
	sprite.position = Vector2(100, 50)
	var tex := GradientTexture2D.new()
	tex.width = 64
	tex.height = 64
	sprite.texture = tex
	main2d.add_child(sprite)
	var shape := CollisionShape2D.new()
	shape.name = "Shape"
	var rect := RectangleShape2D.new()
	rect.size = Vector2(40, 20)
	shape.shape = rect
	main2d.add_child(shape)
	var label := Label.new()
	label.name = "Label"
	label.text = "Hello"
	main2d.add_child(label)
	var poly := Polygon2D.new()
	poly.name = "Poly"
	poly.polygon = PackedVector2Array([Vector2(0, 0), Vector2(30, 0), Vector2(15, 25)])
	main2d.add_child(poly)
	var line := Line2D.new()
	line.name = "Line"
	line.points = PackedVector2Array([Vector2(0, 0), Vector2(50, 50)])
	main2d.add_child(line)
	var group := Node2D.new()
	group.name = "Group"
	for n in ["A", "B", "C"]:
		var child := Node2D.new()
		child.name = n
		group.add_child(child)
	main2d.add_child(group)
	var enemy_inst := (load("res://enemy.tscn") as PackedScene).instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE)
	enemy_inst.name = "Enemy1"
	main2d.add_child(enemy_inst)
	var anim := AnimationPlayer.new()
	anim.name = "Anim"
	var lib := AnimationLibrary.new()
	var a := Animation.new()
	var t := a.add_track(Animation.TYPE_VALUE)
	a.track_set_path(t, NodePath("Sprite:position"))
	a.track_insert_key(t, 0.0, Vector2.ZERO)
	a.track_insert_key(t, 1.0, Vector2(10, 10))
	lib.add_animation("move", a)
	anim.add_animation_library("", lib)
	main2d.add_child(anim)
	var path := Path2D.new()
	path.name = "Path"
	var curve := Curve2D.new()
	curve.add_point(Vector2.ZERO)
	curve.add_point(Vector2(100, 0))
	path.curve = curve
	main2d.add_child(path)
	_save(main2d, "res://main2d.tscn")

	# 3D scene.
	var main3d := Node3D.new()
	main3d.name = "Main3D"
	var box := MeshInstance3D.new()
	box.name = "Box"
	var bm := BoxMesh.new()
	bm.size = Vector3(1, 2, 1)
	box.mesh = bm
	var inline_mat := StandardMaterial3D.new()
	inline_mat.albedo_color = Color(0.1, 0.6, 0.9)
	box.material_override = inline_mat
	main3d.add_child(box)
	var sphere := MeshInstance3D.new()
	sphere.name = "Sphere"
	sphere.mesh = SphereMesh.new()
	sphere.set_surface_override_material(0, load("res://mat.tres"))
	sphere.position = Vector3(2, 0, 0)
	main3d.add_child(sphere)
	var cam := Camera3D.new()
	cam.name = "Camera"
	cam.position = Vector3(0, 2, 6)
	main3d.add_child(cam)
	var sun := DirectionalLight3D.new()
	sun.name = "Sun"
	main3d.add_child(sun)
	var csg := CSGBox3D.new()
	csg.name = "CSG"
	main3d.add_child(csg)
	var pivot := Node3D.new()
	pivot.name = "Pivot"
	for n in ["P1", "P2"]:
		var c := Node3D.new()
		c.name = n
		pivot.add_child(c)
	main3d.add_child(pivot)
	var particles := GPUParticles3D.new()
	particles.name = "Particles"
	particles.process_material = ParticleProcessMaterial.new()
	main3d.add_child(particles)
	_save(main3d, "res://main3d.tscn")

	# UI scene.
	var ui := Control.new()
	ui.name = "UI"
	var vb := VBoxContainer.new()
	vb.name = "VBox"
	ui.add_child(vb)
	var button := Button.new()
	button.name = "Button"
	button.text = "Play"
	vb.add_child(button)
	var ui_label := Label.new()
	ui_label.name = "Title"
	ui_label.text = "Title"
	vb.add_child(ui_label)
	var cr := ColorRect.new()
	cr.name = "Swatch"
	cr.color = Color.CORNFLOWER_BLUE
	vb.add_child(cr)
	_save(ui, "res://ui.tscn")

	# Engine drift: things the engine changes by itself in the editor. Opening
	# and watching this scene must send nothing.
	var tooly := FileAccess.open("res://tooly.gd", FileAccess.WRITE)
	tooly.store_string("""@tool
extends Node2D

func _process(delta: float) -> void:
	rotation += delta
	modulate = Color(0.2, 0.4, 0.6)
	set_meta("ticks", int(get_meta("ticks", 0)) + 1)
""")
	tooly.close()
	var drift := Control.new()
	drift.name = "Drift"
	drift.size = Vector2(800, 600)
	var drift_box := VBoxContainer.new()
	drift_box.name = "Box"
	drift_box.size = Vector2(200, 200)
	drift.add_child(drift_box)
	for i in 3:
		var b := Button.new()
		b.name = "B%d" % i
		b.text = "Button %d" % i
		drift_box.add_child(b)
	var te := TextEdit.new()
	te.name = "Text"
	te.position = Vector2(300, 0)
	te.size = Vector2(200, 120)
	te.text = "short"
	drift.add_child(te)
	var wrap := Label.new()
	wrap.name = "Wrap"
	wrap.position = Vector2(300, 200)
	wrap.custom_minimum_size = Vector2(150, 0)
	wrap.autowrap_mode = TextServer.AUTOWRAP_WORD
	wrap.text = "short"
	drift.add_child(wrap)
	var mover := Node2D.new()
	mover.name = "Mover"
	drift.add_child(mover)
	var wiggle := Animation.new()
	wiggle.length = 1.0
	wiggle.loop_mode = Animation.LOOP_LINEAR
	var t0 := wiggle.add_track(Animation.TYPE_VALUE)
	wiggle.track_set_path(t0, "Mover:position")
	wiggle.track_insert_key(t0, 0.0, Vector2(0, 0))
	wiggle.track_insert_key(t0, 0.5, Vector2(50, 20))
	var t1 := wiggle.add_track(Animation.TYPE_VALUE)
	wiggle.track_set_path(t1, "Mover:modulate")
	wiggle.track_insert_key(t1, 0.0, Color.WHITE)
	wiggle.track_insert_key(t1, 0.5, Color.RED)
	var wiggle_lib := AnimationLibrary.new()
	wiggle_lib.add_animation("wiggle", wiggle)
	var drift_anim := AnimationPlayer.new()
	drift_anim.name = "Anim"
	drift_anim.add_animation_library("", wiggle_lib)
	drift.add_child(drift_anim)
	var spinner := Node2D.new()
	spinner.name = "Tooly"
	spinner.set_script(load("res://tooly.gd"))
	drift.add_child(spinner)
	_save(drift, "res://drift.tscn")

	# Root changes: Change Type (incl. the root) and Make Scene Root.
	var rr := Node2D.new()
	rr.name = "RootR"
	rr.position = Vector2(7, 8)
	var keep := Node2D.new()
	keep.name = "Keep"
	keep.position = Vector2(30, 40)
	rr.add_child(keep)
	var leaf := Sprite2D.new()
	leaf.name = "Leaf"
	leaf.modulate = Color(0.2, 0.8, 0.4)
	keep.add_child(leaf)
	var other := Sprite2D.new()
	other.name = "Other"
	other.modulate = Color(0.9, 0.1, 0.1)
	rr.add_child(other)
	var swap := Node2D.new()
	swap.name = "Swap"
	swap.position = Vector2(-5, 3)
	swap.add_child(Node2D.new())
	rr.add_child(swap)
	_save(rr, "res://roots.tscn")

	# Live instances: a part, a scene that nests it, a host that instances both
	# (one instance with editable children and a local override) and a scene
	# that inherits the part.
	var part := Node2D.new()
	part.name = "Part"
	var part_body := Sprite2D.new()
	part_body.name = "Body"
	part.add_child(part_body)
	var tip := Node2D.new()
	tip.name = "Tip"
	tip.position = Vector2(10, 0)
	part.add_child(tip)
	_save(part, "res://part.tscn")
	var part_scene := load("res://part.tscn") as PackedScene
	var wrap_root := Node2D.new()
	wrap_root.name = "Wrap"
	var inner := part_scene.instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE)
	inner.name = "Inner"
	wrap_root.add_child(inner)
	_save(wrap_root, "res://wrap.tscn")
	var host := Node2D.new()
	host.name = "Host"
	var p1 := part_scene.instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE)
	p1.name = "P1"
	p1.position = Vector2(100, 0)
	host.add_child(p1)
	var p2 := part_scene.instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE)
	p2.name = "P2"
	host.add_child(p2)
	var w1 := (load("res://wrap.tscn") as PackedScene).instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE)
	w1.name = "W1"
	host.add_child(w1)
	var marker := Node2D.new()
	marker.name = "Marker"
	host.add_child(marker)
	_own_children(host)
	host.set_editable_instance(p2, true)
	p2.get_node("Body").modulate = Color(0, 1, 0)
	var host_scene := PackedScene.new()
	host_scene.pack(host)
	ResourceSaver.save(host_scene, "res://host.tscn")
	host.free()
	# Only the editor can mark a scene as inherited, so write it as text.
	var sub := FileAccess.open("res://sub.tscn", FileAccess.WRITE)
	sub.store_string("""[gd_scene load_steps=2 format=3]

[ext_resource type="PackedScene" path="res://part.tscn" id="1_part"]

[node name="Sub" instance=ExtResource("1_part")]

[node name="Extra" type="Node2D" parent="."]
""")
	sub.close()

	print("scenes ready")
	quit()
