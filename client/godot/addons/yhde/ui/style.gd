@tool
extends RefCounted
## Visual language for the YHDE editor UI: quiet, flat, editor-native.
## Everything derives from the active editor theme so light/dark themes work.

const STATE_OFFLINE := 0
const STATE_CONNECTING := 1
const STATE_SYNCING := 2
const STATE_LIVE := 3
const STATE_RECONNECTING := 4

const AMBER := Color("#FFC700")


static func theme() -> Theme:
	return EditorInterface.get_editor_theme()


static func ui_scale() -> float:
	return EditorInterface.get_editor_scale()


static func px(value: float) -> float:
	return value * ui_scale()


static func color(name: String) -> Color:
	return theme().get_color(name, "Editor")


static func font(bold := false) -> Font:
	return theme().get_font("bold" if bold else "main", "EditorFonts")


static func font_size() -> int:
	return theme().get_font_size("main_size", "EditorFonts")


static func small_size() -> int:
	return maxi(8, font_size() - 2)


# YHDE's own icons (the editor has no speech bubbles): 16x16 white SVG; the
# button's icon colors tint them (muted, accent) in dark and light themes.
const CHAT_ICON := "yhde:chat"
const COMMENT_ICON := "yhde:comment"
const _SVG := {
	"yhde:comment": "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><path d='M8 1.5a6.5 6.5 0 0 0-5.8 9.4L1.5 14.5l3.6-.9A6.5 6.5 0 1 0 8 1.5z' fill='none' stroke='#fff' stroke-width='1.5' stroke-linejoin='round'/></svg>",
	"yhde:chat": "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><path d='M2 3.5A1.5 1.5 0 0 1 3.5 2h9A1.5 1.5 0 0 1 14 3.5v6a1.5 1.5 0 0 1-1.5 1.5H7l-3.5 3v-3h0A1.5 1.5 0 0 1 2 9.5z' fill='none' stroke='#fff' stroke-width='1.5' stroke-linejoin='round'/><path d='M5 5.5h6M5 8h4' stroke='#fff' stroke-width='1.3' stroke-linecap='round'/></svg>",
}
static var _svg_cache := {}

# The YHDE mark in its own colors, never tinted: the ring is off-white on
# dark editor themes and near-black on light ones so it stays visible.
const _LOGO := "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16' viewBox='2.7 -0.8 62.6 62.6'><defs><mask id='cut-under-green' maskUnits='userSpaceOnUse' x='2.7' y='-0.8' width='62.6' height='62.6'><rect x='2.7' y='-0.8' width='62.6' height='62.6' fill='#fff'/><path d='M28.5 36v19.55l5.175-4.83 3.565 7.82 3.45-1.495-3.45-7.705h6.785Z' fill='none' stroke='#000' stroke-width='4.2' stroke-linejoin='round'/><path d='M58.223 27.006a27 8.5-20 0 1-45.989 16.738' fill='none' stroke='#000' stroke-width='7.5' stroke-linejoin='round'/><path d='M58 25V5.45l-5.175 4.83-3.565-7.82-3.45 1.495 3.45 7.705h-6.785Z' fill='none' stroke='#000' stroke-width='4.2' stroke-linejoin='round'/></mask><mask id='cut-green' maskUnits='userSpaceOnUse' x='2.7' y='-0.8' width='62.6' height='62.6'><rect x='2.7' y='-0.8' width='62.6' height='62.6' fill='#fff'/><path d='M58.223 27.006a27 8.5-20 0 1-45.989 16.738' fill='none' stroke='#000' stroke-width='7.5' stroke-linejoin='round'/><path d='M58 25V5.45l-5.175 4.83-3.565-7.82-3.45 1.495 3.45 7.705h-6.785Z' fill='none' stroke='#000' stroke-width='4.2' stroke-linejoin='round'/></mask><mask id='cut-front' maskUnits='userSpaceOnUse' x='2.7' y='-0.8' width='62.6' height='62.6'><rect x='2.7' y='-0.8' width='62.6' height='62.6' fill='#fff'/><path d='M58 25V5.45l-5.175 4.83-3.565-7.82-3.45 1.495 3.45 7.705h-6.785Z' fill='none' stroke='#000' stroke-width='4.2' stroke-linejoin='round'/></mask></defs><g id='Behind-green' mask='url(#cut-under-green)'><path id='Ring-back' d='M8.628 41.234a27 8.5-20 0 1 50.744-18.469' fill='none' stroke='RING' stroke-width='3.5' stroke-linecap='round'/><circle id='Planet' cx='34' cy='32' r='14' fill='#7cc4ff'/></g><path id='Cursor-green' mask='url(#cut-green)' d='M28.5 36v19.55l5.175-4.83 3.565 7.82 3.45-1.495-3.45-7.705h6.785Z' fill='#5ee6a8' stroke='#5ee6a8' stroke-width='1.5' stroke-linejoin='round'/><path id='Ring-front' mask='url(#cut-front)' d='M59.372 22.765a27 8.5-20 0 1-50.744 18.47' fill='none' stroke='RING' stroke-width='3.5' stroke-linecap='round'/><path id='Cursor-coral' d='M58 25V5.45l-5.175 4.83-3.565-7.82-3.45 1.495 3.45 7.705h-6.785Z' fill='#ff8a65' stroke='#ff8a65' stroke-width='1.5' stroke-linejoin='round'/></svg>"


static func logo() -> Texture2D:
	var dark := color("base_color").get_luminance() < 0.5
	var ring := "#f4f2ec" if dark else "#131315"
	var key := "logo%s@%.2f" % [ring, ui_scale()]
	if _svg_cache.has(key):
		return _svg_cache[key]
	var img := Image.new()
	if img.load_svg_from_string(_LOGO.replace("RING", ring), ui_scale()) != OK:
		return null
	var tex := ImageTexture.create_from_image(img)
	_svg_cache[key] = tex
	return tex


## Shows a button's icon as drawn, without the theme's icon tint.
static func untinted_icon(button: Button) -> void:
	for state in ["normal", "hover", "pressed", "hover_pressed", "focus", "disabled"]:
		button.add_theme_color_override("icon_%s_color" % state, Color.WHITE)


static func icon(name: String) -> Texture2D:
	if _SVG.has(name):
		return _own_icon(name)
	# Editor icon names change between Godot versions: never draw a blank.
	var t := theme()
	if t.has_icon(name, "EditorIcons"):
		return t.get_icon(name, "EditorIcons")
	for fallback in ["Info", "Node"]:
		if t.has_icon(fallback, "EditorIcons"):
			return t.get_icon(fallback, "EditorIcons")
	return null


## Readable text on top of `bg`.
static func text_on(bg: Color) -> Color:
	return Color(0.07, 0.07, 0.09) if bg.get_luminance() > 0.62 else Color.WHITE


static func muted() -> Color:
	var c := color("font_color")
	c.a *= 0.6
	return c


static func state_color(state: int) -> Color:
	match state:
		STATE_LIVE:
			return color("success_color")
		STATE_CONNECTING, STATE_SYNCING, STATE_RECONNECTING:
			return AMBER
	return color("font_disabled_color")


static func box(bg: Color, radius := 6.0, padding := Vector4(8, 5, 8, 5), border := Color.TRANSPARENT, border_width := 0) -> StyleBoxFlat:
	var sb := StyleBoxFlat.new()
	sb.bg_color = bg
	sb.set_corner_radius_all(int(px(radius)))
	sb.content_margin_left = px(padding.x)
	sb.content_margin_top = px(padding.y)
	sb.content_margin_right = px(padding.z)
	sb.content_margin_bottom = px(padding.w)
	if border_width > 0:
		sb.border_color = border
		sb.set_border_width_all(int(max(1.0, px(border_width))))
	sb.anti_aliasing = true
	return sb


## A flat button that only shows a background on hover/press (Figma-like).
static func quiet_button(button: Button) -> void:
	button.flat = false
	var hover := color("font_color")
	hover.a = 0.08
	var press := hover
	press.a = 0.14
	button.add_theme_stylebox_override("normal", box(Color.TRANSPARENT, 5, Vector4(6, 3, 6, 3)))
	button.add_theme_stylebox_override("hover", box(hover, 5, Vector4(6, 3, 6, 3)))
	button.add_theme_stylebox_override("pressed", box(press, 5, Vector4(6, 3, 6, 3)))
	button.add_theme_stylebox_override("focus", StyleBoxEmpty.new())
	button.focus_mode = Control.FOCUS_NONE


## The one accent button of a panel.
static func primary_button(button: Button) -> void:
	var accent := color("accent_color")
	button.add_theme_stylebox_override("normal", box(accent, 6, Vector4(12, 5, 12, 5)))
	button.add_theme_stylebox_override("hover", box(accent.lightened(0.12), 6, Vector4(12, 5, 12, 5)))
	button.add_theme_stylebox_override("pressed", box(accent.darkened(0.12), 6, Vector4(12, 5, 12, 5)))
	button.add_theme_stylebox_override("focus", StyleBoxEmpty.new())
	var fg := text_on(accent)
	for key in ["font_color", "font_hover_color", "font_pressed_color", "font_focus_color"]:
		button.add_theme_color_override(key, fg)
	button.focus_mode = Control.FOCUS_NONE


static func secondary_button(button: Button) -> void:
	var fg := color("font_color")
	var border := fg
	border.a = 0.22
	var hover := fg
	hover.a = 0.06
	button.add_theme_stylebox_override("normal", box(Color.TRANSPARENT, 6, Vector4(12, 5, 12, 5), border, 1))
	button.add_theme_stylebox_override("hover", box(hover, 6, Vector4(12, 5, 12, 5), border, 1))
	button.add_theme_stylebox_override("pressed", box(hover, 6, Vector4(12, 5, 12, 5), border, 1))
	button.add_theme_stylebox_override("focus", StyleBoxEmpty.new())
	button.focus_mode = Control.FOCUS_NONE


static func field(line_edit: LineEdit) -> void:
	var fg := color("font_color")
	var border := fg
	border.a = 0.14
	var focus := color("accent_color")
	var bg := color("dark_color_1")
	line_edit.add_theme_stylebox_override("normal", box(bg, 6, Vector4(8, 5, 8, 5), border, 1))
	line_edit.add_theme_stylebox_override("focus", box(bg, 6, Vector4(8, 5, 8, 5), focus, 1))
	line_edit.add_theme_stylebox_override("read_only", box(bg, 6, Vector4(8, 5, 8, 5), border, 1))


static func caption(text: String) -> Label:
	var l := Label.new()
	l.text = text.to_upper()
	l.add_theme_font_override("font", font(true))
	l.add_theme_font_size_override("font_size", small_size())
	l.add_theme_color_override("font_color", muted())
	return l


static func label(text := "", muted_text := false, size := 0) -> Label:
	var l := Label.new()
	l.text = text
	if muted_text:
		l.add_theme_color_override("font_color", muted())
	if size > 0:
		l.add_theme_font_size_override("font_size", size)
	return l


static func hairline() -> HSeparator:
	var sep := HSeparator.new()
	var line := StyleBoxLine.new()
	var c := color("font_color")
	c.a = 0.08
	line.color = c
	line.thickness = 1
	sep.add_theme_stylebox_override("separator", line)
	sep.add_theme_constant_override("separation", int(px(14)))
	return sep


static func relative_time(unix: float) -> String:
	var dt := int(Time.get_unix_time_from_system() - unix)
	if dt < 5:
		return "now"
	if dt < 60:
		return "%ds" % dt
	if dt < 3600:
		return "%dm" % (dt / 60)
	return "%dh" % (dt / 3600)


static func danger() -> Color:
	return color("error_color")


static func warning() -> Color:
	return color("warning_color")


## A soft panel for grouped content (cards, banners, message bubbles).
static func card(bg_alpha := 0.05, radius := 8.0, padding := Vector4(10, 8, 10, 8), tint := Color.TRANSPARENT) -> StyleBoxFlat:
	var fg := color("font_color")
	var bg := fg
	bg.a = bg_alpha
	if tint.a > 0.0:
		bg = tint
		bg.a = bg_alpha
	return box(bg, radius, padding)


static func panel(child: Control, style: StyleBox) -> PanelContainer:
	var p := PanelContainer.new()
	p.add_theme_stylebox_override("panel", style)
	p.add_child(child)
	return p


## Multi-line input that grows with its text (chat and comments).
static func text_input(placeholder: String) -> TextEdit:
	var t := TextEdit.new()
	t.placeholder_text = placeholder
	t.wrap_mode = TextEdit.LINE_WRAPPING_BOUNDARY
	t.scroll_fit_content_height = true
	t.custom_minimum_size = Vector2(0, px(30))
	t.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	var fg := color("font_color")
	var border := fg
	border.a = 0.14
	var bg := color("dark_color_1")
	t.add_theme_stylebox_override("normal", box(bg, 6, Vector4(8, 5, 8, 5), border, 1))
	t.add_theme_stylebox_override("focus", box(bg, 6, Vector4(8, 5, 8, 5), color("accent_color"), 1))
	return t


## Small round count, e.g. unread messages on a tab.
static func badge_text(n: int) -> String:
	return "" if n <= 0 else (" %d" % n if n < 100 else " 99+")


static func icon_button(icon_name: String, tooltip: String) -> Button:
	var b := Button.new()
	b.icon = icon(icon_name)
	b.tooltip_text = tooltip
	quiet_button(b)
	b.add_theme_color_override("icon_normal_color", muted())
	return b


static func clock(unix: float) -> String:
	var t := Time.get_datetime_dict_from_unix_time(int(unix + Time.get_time_zone_from_system().bias * 60))
	var today := Time.get_datetime_dict_from_unix_time(int(Time.get_unix_time_from_system() + Time.get_time_zone_from_system().bias * 60))
	var hm := "%02d:%02d" % [t.hour, t.minute]
	if t.year == today.year and t.month == today.month and t.day == today.day:
		return hm
	return "%d.%d. %s" % [t.day, t.month, hm]


static func _own_icon(name: String) -> Texture2D:
	var key := "%s@%.2f" % [name, ui_scale()]
	if _svg_cache.has(key):
		return _svg_cache[key]
	var img := Image.new()
	var svg: String = _SVG[name]
	if img.load_svg_from_string(svg, ui_scale()) != OK:
		return null
	var tex := ImageTexture.create_from_image(img)
	_svg_cache[key] = tex
	return tex
