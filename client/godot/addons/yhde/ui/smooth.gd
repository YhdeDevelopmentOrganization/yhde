@tool
extends RefCounted
## Smooth motion for everything other people move on your screen.
##
## Presence arrives about 15 times a second. Drawn as it arrives, cursors,
## ghosts and cameras jump; here each value glides towards its latest target
## (exponential smoothing, frame-rate independent), which reads as continuous
## motion with a delay of a few frames. Values that jump very far (a teleport,
## a scene switch) snap instead of flying across the screen.
##
## `moving` tells the caller to keep redrawing until everything has arrived.

const RATE := 18.0          # higher = snappier; 18/s settles in ~0.25 s
const SNAP_2D := 3000.0     # pixels
const SNAP_3D := 200.0      # meters
const FORGET_AFTER := 5.0   # seconds without use

static var enabled := true
static var moving := false
static var _values := {}
static var _last := {}


static func _alpha(key: String) -> float:
	var now := Time.get_ticks_msec() / 1000.0
	var dt := clampf(now - float(_last.get(key, now)), 0.0, 0.1)
	_last[key] = now
	return 1.0 - exp(-RATE * dt)


static func v2(key: String, target: Vector2) -> Vector2:
	if not enabled:
		return target
	var cur = _values.get(key)
	var a := _alpha(key)
	if cur == null or (cur as Vector2).distance_to(target) > SNAP_2D:
		_values[key] = target
		return target
	var next: Vector2 = (cur as Vector2).lerp(target, a)
	if next.distance_to(target) < 0.3:
		next = target
	else:
		moving = true
	_values[key] = next
	return next


static func v3(key: String, target: Vector3) -> Vector3:
	if not enabled:
		return target
	var cur = _values.get(key)
	var a := _alpha(key)
	if cur == null or (cur as Vector3).distance_to(target) > SNAP_3D:
		_values[key] = target
		return target
	var next: Vector3 = (cur as Vector3).lerp(target, a)
	if next.distance_to(target) < 0.001:
		next = target
	else:
		moving = true
	_values[key] = next
	return next


static func xf2(key: String, target: Transform2D) -> Transform2D:
	if not enabled:
		return target
	var cur = _values.get(key)
	var a := _alpha(key)
	if cur == null or (cur as Transform2D).origin.distance_to(target.origin) > SNAP_2D:
		_values[key] = target
		return target
	var next: Transform2D = (cur as Transform2D).interpolate_with(target, a)
	if next.is_equal_approx(target) or next.origin.distance_to(target.origin) < 0.05:
		next = target
	else:
		moving = true
	_values[key] = next
	return next


static func xf3(key: String, target: Transform3D) -> Transform3D:
	if not enabled:
		return target
	var cur = _values.get(key)
	var a := _alpha(key)
	if cur == null or (cur as Transform3D).origin.distance_to(target.origin) > SNAP_3D:
		_values[key] = target
		return target
	var next: Transform3D = (cur as Transform3D).interpolate_with(target, a)
	if next.is_equal_approx(target):
		next = target
	else:
		moving = true
	_values[key] = next
	return next


static func number(key: String, target: float) -> float:
	if not enabled:
		return target
	var cur = _values.get(key)
	var a := _alpha(key)
	if cur == null:
		_values[key] = target
		return target
	var next: float = lerpf(float(cur), target, a)
	if absf(next - target) < 0.0005 * maxf(1.0, absf(target)):
		next = target
	else:
		moving = true
	_values[key] = next
	return next


## Call once per frame before drawing; drops values nobody asked for lately.
static func begin_frame() -> void:
	moving = false
	var now := Time.get_ticks_msec() / 1000.0
	if _last.size() > 256:
		for key in _last.keys():
			if now - float(_last[key]) > FORGET_AFTER:
				_last.erase(key)
				_values.erase(key)
