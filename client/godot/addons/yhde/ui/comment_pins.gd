@tool
extends RefCounted
## Figma-style comment pins in the 2D and 3D viewports.
##
## A comment is anchored to a node by its YHDE id, at a point in that node's
## local space, so the pin follows the node wherever anyone moves it. Without a
## node it sits at a point in the scene. The anchor is the pin's tip.

const Style := preload("style.gd")
const Avatar := preload("avatar.gd")

const RADIUS := 11.0


# Anchors (placing a comment)

## Where a click at `viewport_pos` in the 2D editor lands: the node under the
## cursor (or the selected one), and the point in its local space.
static func anchor_2d(session: Object, viewport_pos: Vector2) -> Dictionary:
	var vp := EditorInterface.get_editor_viewport_2d()
	var root := EditorInterface.get_edited_scene_root()
	if vp == null or root == null:
		return {}
	var world: Vector2 = vp.global_canvas_transform.affine_inverse() * viewport_pos
	var node := _pick_2d(root, world)
	var result := {"scene": root.scene_file_path, "node_id": "", "path": ""}
	if node:
		var id: String = session.get_node_id(node)
		if id != "":
			result.node_id = id
			result.path = str(root.get_path_to(node))
			var local: Vector2 = (node as CanvasItem).get_global_transform().affine_inverse() * world
			result.anchor = {"space": "2d", "x": local.x, "y": local.y}
			return result
	result.anchor = {"space": "2d", "x": world.x, "y": world.y}
	return result


## The same for the 3D editor: the selected node's depth decides how far into
## the scene the click goes (without one, a few meters in front of the camera).
static func anchor_3d(session: Object, camera: Camera3D, viewport_pos: Vector2) -> Dictionary:
	var root := EditorInterface.get_edited_scene_root()
	if camera == null or root == null:
		return {}
	var node: Node3D = null
	for n in EditorInterface.get_selection().get_selected_nodes():
		if n is Node3D and (n == root or root.is_ancestor_of(n)):
			node = n
			break
	var depth := 5.0
	if node:
		depth = maxf(0.1, camera.global_position.distance_to(node.global_position))
	var world := camera.project_position(viewport_pos, depth)
	var result := {"scene": root.scene_file_path, "node_id": "", "path": ""}
	if node:
		var id: String = session.get_node_id(node)
		if id != "":
			result.node_id = id
			result.path = str(root.get_path_to(node))
			var local: Vector3 = node.global_transform.affine_inverse() * world
			result.anchor = {"space": "3d", "x": local.x, "y": local.y, "z": local.z}
			return result
	result.anchor = {"space": "3d", "x": world.x, "y": world.y, "z": world.z}
	return result


static func _pick_2d(root: Node, world: Vector2) -> CanvasItem:
	# The selected node wins when the click is on it; then the topmost node
	# whose shape contains the point; otherwise nothing (a spot in the scene).
	var overlays := preload("overlays.gd")
	for n in EditorInterface.get_selection().get_selected_nodes():
		if n is CanvasItem and _contains(overlays, n, world):
			return n
	var best: CanvasItem = null
	var candidates: Array = [root]
	candidates.append_array(root.find_children("*", "CanvasItem", true, false))
	for n in candidates:
		if not (n is CanvasItem) or not n.is_visible_in_tree():
			continue
		if n != root and n.owner != root:
			continue # inside an instanced scene: its instance is the target
		if _contains(overlays, n, world):
			best = n # later in tree order draws on top
	return best


static func _contains(overlays: Script, n: CanvasItem, world: Vector2) -> bool:
	var rect = overlays._local_rect(n, null)
	if rect == null:
		return false
	var local: Vector2 = n.get_global_transform().affine_inverse() * world
	return (rect as Rect2).has_point(local)


# Where pins are on screen

static func screen_2d(session: Object, thread: Dictionary) -> Variant:
	var vp := EditorInterface.get_editor_viewport_2d()
	var a: Dictionary = thread.get("anchor", {})
	if vp == null or a.get("space") != "2d":
		return null
	var p := Vector2(float(a.x), float(a.y))
	var node = _node_of(session, thread)
	if thread.get("node") != null:
		if not (node is CanvasItem) or not node.is_visible_in_tree():
			return null # its node is gone or hidden: listed in the panel only
		p = (node as CanvasItem).get_global_transform() * p
	return vp.global_canvas_transform * p


static func screen_3d(session: Object, thread: Dictionary, camera: Camera3D, k: Vector2) -> Variant:
	var a: Dictionary = thread.get("anchor", {})
	if camera == null or a.get("space") != "3d":
		return null
	var p := Vector3(float(a.x), float(a.y), float(a.z))
	var node = _node_of(session, thread)
	if thread.get("node") != null:
		if not (node is Node3D) or not node.is_visible_in_tree():
			return null
		p = (node as Node3D).global_transform * p
	if camera.is_position_behind(p):
		return null
	return camera.unproject_position(p) * k


static func _node_of(session: Object, thread: Dictionary) -> Variant:
	if thread.get("node") == null:
		return null
	return session.find_node_by_id(str(thread.scene), str(thread.node))


# Drawing

## pins: [{pos, color, label, selected, resolved}]
static func draw(overlay: Control, pins: Array) -> void:
	for pin in pins:
		_pin(overlay, pin.pos, pin.color, pin.label, pin.selected, pin.resolved)


static func _pin(overlay: Control, tip: Vector2, color: Color, label: String, selected: bool, resolved: bool) -> void:
	var r := Style.px(RADIUS) * (1.15 if selected else 1.0)
	var c := tip + Vector2(r, -r)
	var fill := color
	if resolved:
		fill = fill.lerp(Style.color("base_color"), 0.55)
	# Speech-bubble pin: a circle with its bottom-left corner squared off at the tip.
	var edge := Color.WHITE
	overlay.draw_circle(c, r + Style.px(1.5), edge, true, -1.0, true)
	# The outline goes round the squared corner too: left and bottom edges.
	overlay.draw_rect(Rect2(tip + Vector2(-Style.px(1.5), -r), Vector2(r + Style.px(1.5), r + Style.px(1.5))), edge)
	overlay.draw_circle(c, r, fill, true, -1.0, true)
	overlay.draw_rect(Rect2(tip + Vector2(0, -r), Vector2(r, r)), fill)
	if selected:
		overlay.draw_arc(c, r + Style.px(4), 0.0, TAU, 40, Style.color("accent_color"), Style.px(2), true)
	var f := Style.font(true)
	var fs := int(max(8.0, r * 0.95))
	var size := f.get_string_size(label, HORIZONTAL_ALIGNMENT_LEFT, -1, fs)
	var baseline := c.y + (f.get_ascent(fs) - f.get_descent(fs)) * 0.5
	overlay.draw_string(f, Vector2(c.x - size.x * 0.5, baseline), label, HORIZONTAL_ALIGNMENT_LEFT, -1, fs, Style.text_on(fill))


## The pin under a point, or "".
static func hit(pins: Array, at: Vector2) -> String:
	var r := Style.px(RADIUS) + Style.px(3)
	for i in range(pins.size() - 1, -1, -1):
		var pin: Dictionary = pins[i]
		var c: Vector2 = pin.pos + Vector2(Style.px(RADIUS), -Style.px(RADIUS))
		if c.distance_to(at) <= r:
			return pin.id
	return ""


## Placing a comment: a banner says so, and a ghost pin follows the cursor.
static func draw_comment_mode(overlay: Control, cursor) -> void:
	if cursor is Vector2:
		_pin(overlay, cursor, Style.color("accent_color"), "+", false, false)
	var text := "Click to comment  ·  Esc to cancel"
	var f := Style.font(true)
	var fs := Style.small_size()
	var size := f.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, fs)
	var pad := Vector2(Style.px(10), Style.px(4))
	var box := Rect2(Vector2((overlay.size.x - size.x) * 0.5 - pad.x, 0), size + pad * 2.0)
	var sb := StyleBoxFlat.new()
	sb.bg_color = Style.color("accent_color")
	sb.corner_radius_bottom_left = int(Style.px(6))
	sb.corner_radius_bottom_right = int(Style.px(6))
	sb.anti_aliasing = true
	overlay.draw_style_box(sb, box)
	overlay.draw_string(f, box.position + Vector2(pad.x, pad.y + f.get_ascent(fs)), text, HORIZONTAL_ALIGNMENT_LEFT, -1, fs, Style.text_on(sb.bg_color))
