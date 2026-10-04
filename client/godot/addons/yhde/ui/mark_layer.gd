@tool
extends Control
## A see-through layer over an editor list (tabs, a tree, a file list) that
## draws people's avatars at the right end of the rows they are at.

const Style := preload("style.gd")

## Called on every draw: returns [{rect: Rect2, people: [{name, color, initials, typing}], small?}]
var provider: Callable


func _init() -> void:
	name = "YhdeMarks"
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)


func _ready() -> void:
	# Redraw with the list (it redraws when it scrolls, expands or changes).
	var parent := get_parent() as Control
	if parent and not parent.draw.is_connected(queue_redraw):
		parent.draw.connect(queue_redraw)


func _draw() -> void:
	if not provider.is_valid():
		return
	var parent := get_parent() as Control
	var clip := Rect2(Vector2.ZERO, parent.size) if parent else Rect2(Vector2.ZERO, size)
	for mark in provider.call(parent):
		var rect: Rect2 = mark.rect
		if not clip.intersects(rect):
			continue
		var small: bool = mark.get("small", false)
		var d := Style.px(12 if small else 16)
		var step := d * 0.62
		var people: Array = mark.people
		var shown := mini(people.size(), 3)
		var x := minf(rect.end.x, clip.end.x) - Style.px(4) - d
		var y := rect.position.y + (rect.size.y - d) * 0.5
		for i in range(shown - 1, -1, -1):
			var p: Dictionary = people[i]
			_avatar(Vector2(x - (shown - 1 - i) * step, y), d, p)
		if people.size() > shown:
			var f := Style.font(true)
			draw_string(f, Vector2(x - shown * step - Style.px(14), y + d * 0.75), "+%d" % (people.size() - shown),
				HORIZONTAL_ALIGNMENT_LEFT, -1, Style.small_size() - 2, Style.muted())


func _avatar(at: Vector2, d: float, p: Dictionary) -> void:
	var r := d * 0.5
	var c := at + Vector2(r, r)
	draw_circle(c, r, Style.color("base_color"), true, -1.0, true)
	draw_circle(c, r - 1.0, p.color, true, -1.0, true)
	if d >= Style.px(14):
		var f := Style.font(true)
		var fs := int(maxf(7.0, d * 0.42))
		# While they type, the avatar says so instead of showing initials.
		var text: String = "..." if p.get("typing", false) else p.initials
		var w := f.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, fs).x
		draw_string(f, Vector2(c.x - w * 0.5, c.y + (f.get_ascent(fs) - f.get_descent(fs)) * 0.5), text,
			HORIZONTAL_ALIGNMENT_LEFT, -1, fs, Style.text_on(p.color))
