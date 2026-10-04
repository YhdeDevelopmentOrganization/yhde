@tool
extends RefCounted
## Draws other people inside the 2D and 3D viewports: their cursor with a name
## tag, outlines around what they have selected, and dashed "ghosts" of what
## they are dragging right now.

const Style := preload("style.gd")
const Smooth := preload("smooth.gd")

# Cursor arrow (Figma-like), in unscaled pixels, tip at the origin.
const ARROW := [
	Vector2(0, 0), Vector2(0, 15.5), Vector2(4.2, 11.8), Vector2(7.1, 18.2),
	Vector2(9.6, 17.1), Vector2(6.8, 10.9), Vector2(12.2, 10.9),
]


# 2D

static func draw_2d(overlay: Control, peers: Array) -> void:
	var vp := EditorInterface.get_editor_viewport_2d()
	if vp == null:
		return
	var view := vp.global_canvas_transform
	for p in peers:
		var color: Color = p.color
		var labelled := false
		for n in p.selection:
			if n is CanvasItem and is_instance_valid(n) and n.is_visible_in_tree():
				var top = _outline_2d(overlay, view, n, n.get_global_transform(), null, color, false)
				if not labelled and not p.has("cursor") and top != null:
					_tag(overlay, top, p.name, color)
					labelled = true
		for g in p.ghosts:
			var node = g.node
			if node is CanvasItem and is_instance_valid(node):
				var xf := Smooth.xf2("g2:%s:%d" % [p.id, node.get_instance_id()], g.xform)
				_outline_2d(overlay, view, node, xf, g.get("size"), color, true)
		if p.has("cursor"):
			_cursor(overlay, view * Smooth.v2("c2:" + p.id, p.cursor as Vector2), p.name, color)


static func _local_rect(n: CanvasItem, size_override) -> Variant:
	if size_override is Vector2:
		return Rect2(Vector2.ZERO, size_override)
	var item: Variant = n # dynamic access below; each branch checks the type first
	if n is Control:
		return Rect2(Vector2.ZERO, item.size)
	if n is Sprite2D:
		return item.get_rect()
	if n is AnimatedSprite2D:
		var frames: SpriteFrames = item.sprite_frames
		if frames and frames.has_animation(item.animation):
			var tex: Texture2D = frames.get_frame_texture(item.animation, item.frame)
			if tex:
				var s := tex.get_size()
				return Rect2(-s * 0.5 if item.centered else Vector2.ZERO, s)
	if n is CollisionShape2D and item.shape:
		return (item.shape as Shape2D).get_rect()
	if (n is Polygon2D or n is CollisionPolygon2D) and item.polygon.size() > 0:
		return _bounds(item.polygon)
	if n is Line2D and item.points.size() > 0:
		return _bounds(item.points).grow(item.width * 0.5)
	if n is Path2D and item.curve and item.curve.point_count > 0:
		return _bounds(item.curve.get_baked_points())
	if n is TileMapLayer and item.tile_set:
		var used: Rect2i = item.get_used_rect()
		if used.size != Vector2i.ZERO:
			var ts := Vector2(item.tile_set.tile_size)
			return Rect2(Vector2(used.position) * ts, Vector2(used.size) * ts)
	return null


static func _bounds(points) -> Rect2:
	var r := Rect2(points[0], Vector2.ZERO)
	for pt in points:
		r = r.expand(pt)
	return r


## Returns the screen-space top-left of the outline (for a name tag), or null.
static func _outline_2d(overlay: Control, view: Transform2D, n: CanvasItem, xf: Transform2D, size_override, color: Color, dashed: bool) -> Variant:
	var width := Style.px(1.5)
	var rect = _local_rect(n, size_override)
	var to_screen := view * xf
	if rect == null:
		var c := to_screen.origin
		var r := Style.px(6)
		var diamond := PackedVector2Array([c + Vector2(0, -r), c + Vector2(r, 0), c + Vector2(0, r), c + Vector2(-r, 0), c + Vector2(0, -r)])
		overlay.draw_polyline(diamond, color, width, true)
		return c + Vector2(-r, -r)
	var corners := [
		to_screen * rect.position,
		to_screen * Vector2(rect.end.x, rect.position.y),
		to_screen * rect.end,
		to_screen * Vector2(rect.position.x, rect.end.y),
	]
	for i in 4:
		var a: Vector2 = corners[i]
		var b: Vector2 = corners[(i + 1) % 4]
		if dashed:
			overlay.draw_dashed_line(a, b, color, width, Style.px(5), true, true)
		else:
			overlay.draw_line(a, b, color, width, true)
	var top: Vector2 = corners[0]
	for c in corners:
		if c.y < top.y or (is_equal_approx(c.y, top.y) and c.x < top.x):
			top = c
	return top


# 3D

static func draw_3d(overlay: Control, peers: Array) -> void:
	var pair := _viewport_for(overlay)
	if pair.is_empty():
		return
	var cam: Camera3D = pair[0]
	var vp: SubViewport = pair[1]
	if cam == null or vp.size.x == 0 or vp.size.y == 0:
		return
	var k := overlay.size / Vector2(vp.size)
	for p in peers:
		var color: Color = p.color
		var labelled := false
		for n in p.selection:
			if n is Node3D and is_instance_valid(n) and n.is_visible_in_tree():
				var top = _box_3d(overlay, cam, k, n, n.global_transform, color, false)
				if not labelled and not p.has("cursor") and top != null:
					_tag(overlay, top, p.name, color)
					labelled = true
		for g in p.ghosts:
			var node = g.node
			if node is Node3D and is_instance_valid(node):
				var xf := Smooth.xf3("g3:%s:%d" % [p.id, node.get_instance_id()], g.xform)
				_box_3d(overlay, cam, k, node, xf, color, true)
		if p.has("camera"):
			var eye: Vector3 = Smooth.v3("e3:" + p.id, (p.camera as Transform3D).origin)
			if not cam.is_position_behind(eye):
				var s := cam.unproject_position(eye) * k
				overlay.draw_circle(s, Style.px(5), color, true, -1.0, true)
				overlay.draw_arc(s, Style.px(5), 0, TAU, 24, Color.WHITE, Style.px(1.25), true)
				if not p.has("cursor"):
					_tag(overlay, s + Vector2(Style.px(8), -Style.px(8)), p.name, color)
		if p.has("cursor"):
			var at: Vector3 = Smooth.v3("c3:" + p.id, p.cursor)
			if not cam.is_position_behind(at):
				_cursor(overlay, cam.unproject_position(at) * k, p.name, color)


static func _viewport_for(overlay: Control) -> Array:
	for i in 4:
		var vp := EditorInterface.get_editor_viewport_3d(i)
		if vp == null:
			continue
		var holder := vp.get_parent()
		var editor_viewport: Node = holder.get_parent() if holder else null
		if editor_viewport and editor_viewport.is_ancestor_of(overlay):
			return [vp.get_camera_3d(), vp]
	var first := EditorInterface.get_editor_viewport_3d(0)
	return [first.get_camera_3d(), first] if first else []


static func _local_aabb(n: Node3D) -> AABB:
	var item: Variant = n
	if n is VisualInstance3D:
		var box: AABB = item.get_aabb()
		if box.size != Vector3.ZERO:
			return box
	if n is CollisionShape3D and item.shape:
		var mesh: Mesh = (item.shape as Shape3D).get_debug_mesh()
		if mesh:
			return mesh.get_aabb()
	return AABB(Vector3(-0.2, -0.2, -0.2), Vector3(0.4, 0.4, 0.4))


const EDGES := [
	[0, 1], [1, 3], [3, 2], [2, 0], [4, 5], [5, 7], [7, 6], [6, 4], [0, 4], [1, 5], [2, 6], [3, 7],
]


static func _box_3d(overlay: Control, cam: Camera3D, k: Vector2, n: Node3D, xf: Transform3D, color: Color, dashed: bool) -> Variant:
	var box := _local_aabb(n)
	var world: Array[Vector3] = []
	for i in 8:
		world.append(xf * box.get_endpoint(i))
	var screen: Array = []
	var top = null
	for w in world:
		if cam.is_position_behind(w):
			screen.append(null)
			continue
		var s := cam.unproject_position(w) * k
		screen.append(s)
		if top == null or s.y < top.y:
			top = s
	var width := Style.px(1.5)
	for e in EDGES:
		var a = screen[e[0]]
		var b = screen[e[1]]
		if a == null or b == null:
			continue
		if dashed:
			overlay.draw_dashed_line(a, b, color, width, Style.px(5), true, true)
		else:
			overlay.draw_line(a, b, color, width, true)
	return top


# Shared

## While following someone, frame the viewport in their color (Figma-style).
static func draw_follow_frame(overlay: Control, view: Dictionary) -> void:
	if view.is_empty():
		return
	var color: Color = view.color
	var w := Style.px(3)
	var r := Rect2(Vector2.ONE * w * 0.5, overlay.size - Vector2.ONE * w)
	overlay.draw_rect(r, color, false, w)
	var text := "Following %s  ·  click or press any key to stop" % view.name
	var f := Style.font(true)
	var fs := Style.small_size()
	var size := f.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, fs)
	var pad := Vector2(Style.px(10), Style.px(4))
	var box := Rect2(Vector2((overlay.size.x - size.x) * 0.5 - pad.x, 0), size + pad * 2.0)
	var sb := StyleBoxFlat.new()
	sb.bg_color = color
	sb.corner_radius_bottom_left = int(Style.px(6))
	sb.corner_radius_bottom_right = int(Style.px(6))
	sb.anti_aliasing = true
	overlay.draw_style_box(sb, box)
	overlay.draw_string(f, box.position + Vector2(pad.x, pad.y + f.get_ascent(fs)), text, HORIZONTAL_ALIGNMENT_LEFT, -1, fs, Style.text_on(color))


static func _cursor(overlay: Control, at: Vector2, name: String, color: Color) -> void:
	# Off screen there is nothing to see. A 3D cursor close to the camera's
	# plane projects to huge coordinates, where the arrow can't be filled.
	if not at.is_finite() or not Rect2(Vector2.ZERO, overlay.size).grow(Style.px(64)).has_point(at):
		return
	var s := Style.ui_scale() * 1.2
	var pts := PackedVector2Array()
	for p in ARROW:
		pts.append(at + p * s)
	overlay.draw_colored_polygon(pts, color)
	var outline := pts.duplicate()
	outline.append(pts[0])
	overlay.draw_polyline(outline, Color.WHITE, 1.25 * s, true)
	_tag(overlay, at + Vector2(11, 17) * s, name, color)


# Name tag boxes and text sizes, kept between redraws.
static var _boxes := {} # Color -> StyleBoxFlat
static var _sizes := {} # "<font id>:<size>:<text>" -> Vector2


static func _box(color: Color, radius: int) -> StyleBoxFlat:
	var sb: StyleBoxFlat = _boxes.get(color)
	if sb == null or sb.corner_radius_top_left != radius:
		if _boxes.size() > 64:
			_boxes.clear()
		sb = StyleBoxFlat.new()
		sb.bg_color = color
		sb.set_corner_radius_all(radius)
		sb.anti_aliasing = true
		_boxes[color] = sb
	return sb


static func _text_size(f: Font, fs: int, text: String) -> Vector2:
	var key := "%d:%d:%s" % [f.get_instance_id(), fs, text]
	var size = _sizes.get(key)
	if size == null:
		if _sizes.size() > 512:
			_sizes.clear()
		size = f.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, fs)
		_sizes[key] = size
	return size


static func _tag(overlay: Control, at: Vector2, text: String, color: Color) -> void:
	var f := Style.font(true)
	var fs := Style.small_size()
	var size := _text_size(f, fs, text)
	var pad := Vector2(Style.px(6), Style.px(3))
	var rect := Rect2(at, size + pad * 2.0)
	# Stay inside the viewport: flip left/up rather than getting clipped.
	var bounds := Rect2(Vector2.ZERO, overlay.size)
	if rect.end.x > bounds.end.x:
		rect.position.x = maxf(bounds.position.x, at.x - rect.size.x - Style.px(14))
	if rect.end.y > bounds.end.y:
		rect.position.y = maxf(bounds.position.y, at.y - rect.size.y - Style.px(20))
	at = rect.position
	overlay.draw_style_box(_box(color, int(Style.px(4))), rect)
	var baseline := at.y + pad.y + f.get_ascent(fs)
	overlay.draw_string(f, Vector2(at.x + pad.x, baseline), text, HORIZONTAL_ALIGNMENT_LEFT, -1, fs, Style.text_on(color))
