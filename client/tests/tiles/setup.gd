extends SceneTree
## Makes the tile test's files: an atlas image, scenes and a TileSet .tres.

func save_scene(root: Node, path: String) -> void:
	var ps := PackedScene.new()
	ps.pack(root)
	ResourceSaver.save(ps, path)
	root.free()

func _init() -> void:
	var img := Image.create(64, 64, false, Image.FORMAT_RGBA8)
	for x in 64:
		for y in 64:
			img.set_pixel(x, y, Color(x / 64.0, y / 64.0, 0.5))
	img.save_png("res://tiles.png")

	var piece := Node2D.new()
	piece.name = "Piece"
	save_scene(piece, "res://tile_scene.tscn")

	for spec in [["res://tiles_embedded.tscn", "TileMapLayer", "Ground"], ["res://tiles_legacy.tscn", "TileMap", "Map"]]:
		var root := Node2D.new()
		root.name = "Root"
		var n: Node = ClassDB.instantiate(spec[1])
		n.name = spec[2]
		root.add_child(n)
		n.owner = root
		save_scene(root, spec[0])

	var ts := TileSet.new()
	ResourceSaver.save(ts, "res://tiles_ext.tres")
	print("tile files ready")
	quit()
