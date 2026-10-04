@tool
extends Control
## Round avatar with initials. A thin ring in the background color separates
## overlapping avatars; an accent ring marks the person you are following.

signal pressed

const Style := preload("style.gd")

var initials := "?":
	set(value):
		initials = value
		queue_redraw()
var fill := Color.GRAY:
	set(value):
		fill = value
		queue_redraw()
var ring := Color.TRANSPARENT:
	set(value):
		ring = value
		queue_redraw()
var diameter := 24.0:
	set(value):
		diameter = value
		custom_minimum_size = Vector2(diameter, diameter)
		queue_redraw()

var _hover := false


func _init() -> void:
	mouse_filter = Control.MOUSE_FILTER_STOP
	mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	custom_minimum_size = Vector2(diameter, diameter)
	mouse_entered.connect(func() -> void:
		_hover = true
		queue_redraw())
	mouse_exited.connect(func() -> void:
		_hover = false
		queue_redraw())


func setup(p_initials: String, p_fill: Color, p_diameter: float) -> void:
	initials = p_initials
	fill = p_fill
	diameter = p_diameter


func _gui_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT and event.pressed:
		pressed.emit()
		accept_event()


func _draw() -> void:
	var r := diameter * 0.5
	var c := Vector2(r, r)
	draw_circle(c, r, Style.color("base_color"), true, -1.0, true)
	var body := fill.lightened(0.08) if _hover else fill
	draw_circle(c, r - Style.px(1.5), body, true, -1.0, true)
	if ring.a > 0.0:
		draw_arc(c, r - Style.px(0.75), 0.0, TAU, 48, ring, Style.px(1.5), true)
	var f := Style.font(true)
	var fs := int(max(7.0, diameter * 0.4))
	var size := f.get_string_size(initials, HORIZONTAL_ALIGNMENT_LEFT, -1, fs)
	var ascent := f.get_ascent(fs)
	var descent := f.get_descent(fs)
	var baseline := c.y + (ascent - descent) * 0.5
	draw_string(f, Vector2(c.x - size.x * 0.5, baseline), initials, HORIZONTAL_ALIGNMENT_LEFT, -1, fs, Style.text_on(fill))
