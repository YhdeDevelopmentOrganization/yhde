extends SceneTree
## Second pass, after import: a scene using the external TileSet with a tile.

func _init() -> void:
	var ts: TileSet = load("res://tiles_ext.tres")
	var src := TileSetAtlasSource.new()
	src.texture = load("res://tiles.png")
	src.texture_region_size = Vector2i(16, 16)
	src.create_tile(Vector2i(0, 0))
	var ts2 := TileSet.new()
	ts2.add_source(src, 0)
	ResourceSaver.save(ts2, "res://tiles_uses.tres")
	var root := Node2D.new()
	root.name = "Root"
	var layer := TileMapLayer.new()
	layer.name = "Ground"
	layer.tile_set = load("res://tiles_uses.tres")
	root.add_child(layer)
	layer.owner = root
	var ps := PackedScene.new()
	ps.pack(root)
	ResourceSaver.save(ps, "res://tiles_uses_ext.tscn")
	print("tile scenes ready")
	quit()
