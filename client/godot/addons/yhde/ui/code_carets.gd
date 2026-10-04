@tool
extends Control
## Other people's carets and selections inside a shared script, drawn over the
## code editor like in a shared document: a colored caret with a name tag
## (shown while they type or just moved) and a tinted selection.

const Style := preload("style.gd")
const Smooth := preload("smooth.gd")
const TAG_SECONDS := 2.5

var session: Object
var path := ""
var _carets: Array = []
var _moved := {} # peer id -> when its caret last moved
var _last_pos := {}


func _init() -> void:
	name = "YhdeCodeCarets"
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)


func refresh() -> void:
	if session == null:
		return
	_carets = session.get_script_carets(path)
	var now := Time.get_ticks_msec() / 1000.0
	for c in _carets:
		var key := str(c.caret)
		if _last_pos.get(c.id, "") != key:
			_last_pos[c.id] = key
			_moved[c.id] = now
	queue_redraw()


func _draw() -> void:
	var editor := get_parent() as CodeEdit
	if editor == null:
		return
	var now := Time.get_ticks_msec() / 1000.0
	var lines := editor.get_line_count()
	for c in _carets:
		var color: Color = c.color
		var sel: Array = c.get("sel", [])
		if sel.size() == 4:
			_selection(editor, sel, color, lines)
		var caret: Array = c.get("caret", [])
		if caret.size() != 2:
			continue
		var line := clampi(int(caret[0]), 0, lines - 1)
		var col := clampi(int(caret[1]), 0, editor.get_line(line).length())
		var r := editor.get_rect_at_line_column(line, col)
		if r.position.x < 0 or r.size.y <= 0:
			continue # scrolled out of view
		var at := Smooth.v2("tc:" + str(c.id), Vector2(r.position))
		var h := float(r.size.y)
		draw_rect(Rect2(at + Vector2(-1, 0), Vector2(Style.px(2), h)), color)
		var typing: bool = c.get("typing", false)
		if typing or now - float(_moved.get(c.id, 0.0)) < TAG_SECONDS:
			_tag(at, str(c.name) + (" is typing" if typing else ""), color)


func _selection(editor: CodeEdit, sel: Array, color: Color, lines: int) -> void:
	var tint := color
	tint.a = 0.22
	var from_line := clampi(int(sel[0]), 0, lines - 1)
	var to_line := clampi(int(sel[2]), 0, lines - 1)
	for l in range(from_line, to_line + 1):
		var start := int(sel[1]) if l == from_line else 0
		var end := int(sel[3]) if l == to_line else editor.get_line(l).length()
		start = clampi(start, 0, editor.get_line(l).length())
		end = clampi(end, 0, editor.get_line(l).length())
		var a := editor.get_rect_at_line_column(l, start)
		var b := editor.get_rect_at_line_column(l, end)
		if a.position.x < 0 or b.position.x < 0:
			continue
		var width := maxf(float(b.position.x - a.position.x), Style.px(4) if end == start else 0.0)
		draw_rect(Rect2(Vector2(a.position), Vector2(width, a.size.y)), tint)


func _tag(at: Vector2, text: String, color: Color) -> void:
	var f := Style.font(true)
	var fs := Style.small_size()
	var size := f.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, fs)
	var pad := Vector2(Style.px(5), Style.px(2))
	var box := Rect2(at + Vector2(0, -size.y - pad.y * 2.0), size + pad * 2.0)
	if box.position.y < 0:
		box.position.y = at.y + Style.px(18) # no room above: below the line
	var sb := StyleBoxFlat.new()
	sb.bg_color = color
	sb.set_corner_radius_all(int(Style.px(3)))
	sb.anti_aliasing = true
	draw_style_box(sb, box)
	draw_string(f, box.position + Vector2(pad.x, pad.y + f.get_ascent(fs)), text, HORIZONTAL_ALIGNMENT_LEFT, -1, fs, Style.text_on(color))
