@tool
extends EditorPlugin
## Tile test: TileSet / TileSetAtlasSource / TileSetScenesCollectionSource /
## TileMapLayer / TileMap edits made on editor A must arrive on editor B
## identically, step by step, without B echoing anything back. Uses the
## native mirror hooks (two sync documents, operations through JSON), so no
## server is needed. Needs the native core built with -DYHDE_DIAGNOSTICS=ON.
## Run through run.sh.

var diag
var fails := 0
var steps := 0
var h := -1
var a_root: Node
var b_root: Node
var label := ""


func _enter_tree() -> void:
	await get_tree().process_frame
	await get_tree().process_frame
	diag = ClassDB.instantiate("YhdeDiagnostics")
	if diag == null:
		print("NO DIAGNOSTICS CLASS")
		get_tree().quit(2)
		return
	await embedded_scene()
	await external_tileset()
	await legacy_tilemap()
	await curves()
	print("tiles: steps=", steps, " fails=", fails)
	get_tree().quit(1 if fails > 0 else 0)


# Comparison

func dump(v, depth := 0):
	if depth > 12:
		return "<deep>"
	if v is Resource:
		var r: Resource = v
		if r.resource_path != "" and not r.resource_path.contains("::"):
			return {"@ext": r.resource_path}
		var d := {"@class": r.get_class()}
		for p in diag.stored_properties(r):
			d[p] = dump(r.get(p), depth + 1)
		return d
	if v is Node:
		return {"@node": str(v.name)}
	if v is Array:
		return v.map(func(x): return dump(x, depth + 1))
	if v is Dictionary:
		var d := {}
		for k in v:
			d[var_to_str(k)] = dump(v[k], depth + 1)
		return d
	return v


func dump_node(n: Node) -> Dictionary:
	var d := {"@class": n.get_class(), "@name": str(n.name)}
	for p in diag.stored_properties(n):
		d[p] = dump(n.get(p))
	var kids := []
	for c in n.get_children():
		kids.append(dump_node(c))
	d["@children"] = kids
	return d


func diff_dicts(a, b, path: String, out: PackedStringArray) -> void:
	if out.size() > 25:
		return
	if a is Dictionary and b is Dictionary:
		for k in a:
			if not b.has(k):
				out.append("%s/%s only on A: %s" % [path, k, str(a[k]).left(160)])
			else:
				diff_dicts(a[k], b[k], path + "/" + str(k), out)
		for k in b:
			if not a.has(k):
				out.append("%s/%s only on B: %s" % [path, k, str(b[k]).left(160)])
		return
	if a is Array and b is Array and a.size() == b.size():
		for i in a.size():
			diff_dicts(a[i], b[i], "%s[%d]" % [path, i], out)
		return
	if typeof(a) != typeof(b) or not diag.values_equal(a, b):
		out.append("%s: A=%s B=%s" % [path, var_to_str(a).left(160), var_to_str(b).left(160)])


## Sends A's changes to B and checks that both now hold the same state.
func step(what: String, a_obj: Object, b_obj: Object) -> void:
	steps += 1
	await get_tree().process_frame
	var r: Dictionary = diag.mirror_step(h)
	var problems: PackedStringArray = []
	for e in r.errors:
		problems.append("apply error: " + e)
	for e in r.echo:
		problems.append("B echoed: " + e)
	var da = dump_node(a_obj) if a_obj is Node else dump(a_obj)
	var db = dump_node(b_obj) if b_obj is Node else dump(b_obj)
	if a_obj is Node:
		da.erase("@name") # both test roots live side by side under one parent
		db.erase("@name")
	diff_dicts(da, db, "", problems)
	if problems.is_empty():
		print("ok   %s: %s (%d ops)" % [label, what, r.ops.size()])
		return
	fails += 1
	print("FAIL %s: %s (%d ops)" % [label, what, r.ops.size()])
	for p in problems:
		print("       ", p)
	for o in r.ops.slice(0, 6):
		print("       op ", o.left(300))


# Builders

func atlas_texture() -> Texture2D:
	return load("res://tiles.png")


func open_pair(path: String) -> void:
	var ps: PackedScene = load(path)
	a_root = ps.instantiate()
	b_root = ps.instantiate()
	add_child(a_root)
	add_child(b_root)
	h = diag.mirror_open(path, a_root, b_root)


func close_pair() -> void:
	diag.mirror_close(h)
	a_root.queue_free()
	b_root.queue_free()
	await get_tree().process_frame


## The same edits, applied to any TileSet (embedded in a scene or a .tres).
func tileset_edits(ts: TileSet, sync_a: Object, sync_b: Object) -> void:
	ts.tile_size = Vector2i(16, 16)
	await step("tile_size", sync_a, sync_b)

	var src := TileSetAtlasSource.new()
	src.texture = atlas_texture()
	src.texture_region_size = Vector2i(16, 16)
	ts.add_source(src, 0)
	await step("add atlas source", sync_a, sync_b)

	src.create_tile(Vector2i(0, 0))
	src.create_tile(Vector2i(1, 0))
	src.create_tile(Vector2i(0, 1))
	src.create_tile(Vector2i(2, 0), Vector2i(2, 1))
	await step("create tiles (incl. a 2x1 tile)", sync_a, sync_b)

	src.margins = Vector2i(1, 1)
	src.separation = Vector2i(0, 0)
	src.use_texture_padding = false
	await step("atlas margins / padding", sync_a, sync_b)
	src.margins = Vector2i(0, 0)
	await step("atlas margins back", sync_a, sync_b)

	var alt := src.create_alternative_tile(Vector2i(0, 0))
	src.get_tile_data(Vector2i(0, 0), alt).flip_h = true
	await step("alternative tile", sync_a, sync_b)

	var td := src.get_tile_data(Vector2i(0, 0), 0)
	td.modulate = Color(1, 0.5, 0.5)
	td.z_index = 3
	td.y_sort_origin = 4
	td.probability = 0.25
	td.texture_origin = Vector2i(1, -2)
	td.transpose = true
	await step("tile data visuals", sync_a, sync_b)

	ts.add_physics_layer()
	ts.set_physics_layer_collision_layer(0, 5)
	await step("add physics layer", sync_a, sync_b)
	td.add_collision_polygon(0)
	td.set_collision_polygon_points(0, 0, PackedVector2Array([Vector2(-8, -8), Vector2(8, -8), Vector2(8, 8)]))
	td.set_collision_polygon_one_way(0, 0, true)
	td.set_constant_linear_velocity(0, Vector2(3, 0))
	await step("collision polygon on tile", sync_a, sync_b)
	td.set_collision_polygon_points(0, 0, PackedVector2Array([Vector2(-8, -8), Vector2(8, -8), Vector2(8, 8), Vector2(-8, 8)]))
	await step("edit collision polygon", sync_a, sync_b)

	ts.add_terrain_set()
	ts.set_terrain_set_mode(0, TileSet.TERRAIN_MODE_MATCH_CORNERS_AND_SIDES)
	ts.add_terrain(0)
	ts.set_terrain_name(0, 0, "grass")
	ts.set_terrain_color(0, 0, Color.GREEN)
	await step("add terrain set + terrain", sync_a, sync_b)
	for c in [Vector2i(0, 0), Vector2i(1, 0)]:
		var t := src.get_tile_data(c, 0)
		t.terrain_set = 0
		t.terrain = 0
		t.set_terrain_peering_bit(TileSet.CELL_NEIGHBOR_RIGHT_SIDE, 0)
		t.set_terrain_peering_bit(TileSet.CELL_NEIGHBOR_BOTTOM_SIDE, 0)
	await step("terrain peering bits", sync_a, sync_b)

	ts.add_custom_data_layer()
	ts.set_custom_data_layer_name(0, "damage")
	ts.set_custom_data_layer_type(0, TYPE_INT)
	await step("add custom data layer", sync_a, sync_b)
	src.get_tile_data(Vector2i(1, 0), 0).set_custom_data("damage", 7)
	await step("custom data value", sync_a, sync_b)

	ts.add_navigation_layer()
	var nav := NavigationPolygon.new()
	nav.vertices = PackedVector2Array([Vector2(-8, -8), Vector2(8, -8), Vector2(8, 8), Vector2(-8, 8)])
	nav.add_polygon(PackedInt32Array([0, 1, 2, 3]))
	src.get_tile_data(Vector2i(1, 0), 0).set_navigation_polygon(0, nav)
	await step("navigation layer + polygon", sync_a, sync_b)

	ts.add_occlusion_layer()
	var occ := OccluderPolygon2D.new()
	occ.polygon = PackedVector2Array([Vector2(-8, -8), Vector2(8, -8), Vector2(8, 8)])
	var occ_td := src.get_tile_data(Vector2i(0, 1), 0)
	occ_td.add_occluder_polygon(0)
	occ_td.set_occluder_polygon(0, 0, occ)
	await step("occlusion layer + occluder", sync_a, sync_b)

	src.move_tile_in_atlas(Vector2i(0, 1), Vector2i(3, 1))
	await step("move tile in atlas", sync_a, sync_b)

	src.remove_alternative_tile(Vector2i(0, 0), alt)
	await step("remove alternative tile", sync_a, sync_b)

	src.remove_tile(Vector2i(1, 0))
	await step("remove tile", sync_a, sync_b)

	ts.remove_custom_data_layer(0)
	await step("remove custom data layer", sync_a, sync_b)
	ts.remove_terrain(0, 0)
	await step("remove terrain", sync_a, sync_b)
	ts.remove_terrain_set(0)
	await step("remove terrain set", sync_a, sync_b)
	ts.remove_physics_layer(0)
	await step("remove physics layer", sync_a, sync_b)
	ts.remove_navigation_layer(0)
	ts.remove_occlusion_layer(0)
	await step("remove nav + occlusion layers", sync_a, sync_b)

	var scenes := TileSetScenesCollectionSource.new()
	var sid := scenes.create_scene_tile(load("res://tile_scene.tscn"))
	ts.add_source(scenes, 1)
	await step("add scenes collection source", sync_a, sync_b)
	scenes.set_scene_tile_display_placeholder(sid, true)
	await step("scene tile placeholder", sync_a, sync_b)

	var src2 := TileSetAtlasSource.new()
	src2.texture = atlas_texture()
	src2.texture_region_size = Vector2i(16, 16)
	src2.create_tile(Vector2i(3, 3))
	ts.add_source(src2, 2)
	await step("add second atlas source", sync_a, sync_b)
	ts.set_source_id(2, 5)
	await step("change source id 2 -> 5", sync_a, sync_b)
	ts.remove_source(5)
	await step("remove atlas source", sync_a, sync_b)
	ts.remove_source(1)
	await step("remove scenes source", sync_a, sync_b)

	var pat := TileMapPattern.new()
	pat.set_cell(Vector2i(0, 0), 0, Vector2i(0, 0), 0)
	pat.set_cell(Vector2i(1, 0), 0, Vector2i(2, 0), 0)
	ts.add_pattern(pat)
	await step("add pattern", sync_a, sync_b)
	ts.remove_pattern(0)
	await step("remove pattern", sync_a, sync_b)

	ts.tile_shape = TileSet.TILE_SHAPE_ISOMETRIC
	ts.tile_layout = TileSet.TILE_LAYOUT_DIAMOND_DOWN
	ts.uv_clipping = true
	await step("tile shape / layout", sync_a, sync_b)
	ts.tile_shape = TileSet.TILE_SHAPE_SQUARE
	await step("tile shape back", sync_a, sync_b)


# Scenarios

func embedded_scene() -> void:
	label = "embedded"
	open_pair("res://tiles_embedded.tscn")
	var layer: TileMapLayer = a_root.get_node("Ground")
	var ts := TileSet.new()
	layer.tile_set = ts
	await step("assign new embedded TileSet", a_root, b_root)
	await tileset_edits(ts, a_root, b_root)

	for x in 6:
		for y in 4:
			layer.set_cell(Vector2i(x, y), 0, Vector2i(0, 0), 0)
	layer.set_cell(Vector2i(10, 10), 0, Vector2i(2, 0), 0)
	await step("paint cells", a_root, b_root)
	layer.erase_cell(Vector2i(0, 0))
	layer.set_cell(Vector2i(1, 1), 0, Vector2i(3, 1), 0)
	await step("erase + repaint cells", a_root, b_root)
	layer.clear()
	await step("clear layer", a_root, b_root)

	layer.enabled = false
	layer.y_sort_enabled = true
	layer.y_sort_origin = 3
	layer.x_draw_order_reversed = true
	layer.rendering_quadrant_size = 32
	layer.collision_enabled = false
	layer.use_kinematic_bodies = true
	layer.collision_visibility_mode = TileMapLayer.DEBUG_VISIBILITY_MODE_FORCE_SHOW
	layer.navigation_enabled = false
	layer.occlusion_enabled = false
	layer.self_modulate = Color(0.5, 0.5, 1)
	await step("layer properties", a_root, b_root)

	var second := TileMapLayer.new()
	second.name = "Top"
	second.tile_set = ts
	a_root.add_child(second)
	second.owner = a_root
	second.set_cell(Vector2i(2, 2), 0, Vector2i(0, 0), 0)
	await step("new layer sharing the TileSet", a_root, b_root)
	var b_top: TileMapLayer = b_root.get_node_or_null("Top")
	if b_top and b_top.tile_set != b_root.get_node("Ground").tile_set:
		fails += 1
		print("FAIL embedded: B's layers no longer share one TileSet")

	layer.tile_set = null
	await step("unassign TileSet from one layer", a_root, b_root)
	await close_pair()


func external_tileset() -> void:
	label = "tres"
	var path := "res://tiles_ext.tres"
	var a_ts: TileSet = load(path)
	var b_ts: TileSet = ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_IGNORE)
	h = diag.mirror_open(path, a_ts, b_ts)
	await tileset_edits(a_ts, a_ts, b_ts)
	diag.mirror_close(h)

	label = "uses-tres"
	open_pair("res://tiles_uses_ext.tscn")
	var layer: TileMapLayer = a_root.get_node("Ground")
	layer.set_cell(Vector2i(4, 4), 0, Vector2i(0, 0), 0)
	await step("paint with external TileSet", a_root, b_root)
	await close_pair()


func legacy_tilemap() -> void:
	label = "TileMap"
	open_pair("res://tiles_legacy.tscn")
	var tm: TileMap = a_root.get_node("Map")
	var ts := TileSet.new()
	var src := TileSetAtlasSource.new()
	src.texture = atlas_texture()
	src.texture_region_size = Vector2i(16, 16)
	src.create_tile(Vector2i(0, 0))
	ts.add_source(src, 0)
	tm.tile_set = ts
	await step("TileMap tile_set", a_root, b_root)
	tm.set_cell(0, Vector2i(1, 1), 0, Vector2i(0, 0))
	await step("TileMap paint layer 0", a_root, b_root)
	tm.add_layer(-1)
	tm.set_layer_name(1, "upper")
	tm.set_cell(1, Vector2i(2, 2), 0, Vector2i(0, 0))
	await step("TileMap add layer + paint", a_root, b_root)
	tm.remove_layer(1)
	await step("TileMap remove layer", a_root, b_root)
	tm.add_layer(-1)
	tm.add_layer(-1)
	tm.set_cell(1, Vector2i(5, 5), 0, Vector2i(0, 0))
	tm.set_cell(2, Vector2i(6, 6), 0, Vector2i(0, 0))
	await step("TileMap two more layers", a_root, b_root)
	tm.remove_layer(0)
	await step("TileMap remove first layer", a_root, b_root)
	await close_pair()


## Other resources with per-entry properties (point_N/…) lose entries the same way.
func curves() -> void:
	label = "curves"
	open_pair("res://tiles_embedded.tscn")
	var layer: TileMapLayer = a_root.get_node("Ground")
	var c := Curve.new()
	c.add_point(Vector2(0, 0))
	c.add_point(Vector2(0.5, 1))
	c.add_point(Vector2(1, 0))
	layer.set_meta("curve", c)
	await step("curve with 3 points", a_root, b_root)
	c.remove_point(1)
	await step("remove curve point", a_root, b_root)
	await close_pair()
