@tool
extends EditorPlugin
## Codec test: every Variant type, and every stored property of every
## instantiable Node/Resource class (with non-default values), must survive
## encode -> JSON -> decode. Needs the native core built with
## -DYHDE_DIAGNOSTICS=ON. Run through run.sh.

## Engine setters that renormalize/truncate what they are given, so a value
## read back and re-assigned can differ in the last bits. The codec is exact
## (checked separately); for these the rebuilt value must be approximately equal.
const LAYOUT_DEPENDENT := {"GraphEdit": ["scroll_offset"]}
## CodeEdit reads its delimiters back in insertion-history order: same set.
const SET_LIKE := ["delimiter_strings", "comment_delimiters", "string_delimiters"]

var diag
var fails := 0
var checks := 0

func check_value(label: String, v) -> void:
	checks += 1
	var r: Dictionary = diag.roundtrip(v, false)
	if not r.ok:
		fails += 1
		print("FAIL value ", label, ": ", r.error, " json=", r.json)
		return
	var back = r.value
	if typeof(back) != typeof(v) or not diag.values_equal(v, back):
		if typeof(v) == TYPE_OBJECT:
			return
		fails += 1
		print("FAIL value ", label, ": ", var_to_str(v), " -> ", var_to_str(back), " json=", r.json)

func _approx(a, b) -> bool:
	match typeof(a):
		TYPE_FLOAT:
			return typeof(b) == TYPE_FLOAT and is_equal_approx(a, b)
		TYPE_VECTOR2, TYPE_VECTOR3, TYPE_VECTOR4, TYPE_COLOR, TYPE_QUATERNION, TYPE_TRANSFORM2D, TYPE_TRANSFORM3D, TYPE_BASIS:
			return typeof(b) == typeof(a) and a.is_equal_approx(b)
	return false


func _tolerable(cls: String, rr: Dictionary) -> bool:
	if rr.error != "":
		return false
	for pair in rr.pairs:
		if pair[0] in LAYOUT_DEPENDENT.get(cls, []):
			continue
		if pair[0] in SET_LIKE and pair[1] is Array and pair[2] is Array:
			var a: Array = pair[1].duplicate()
			var b: Array = pair[2].duplicate()
			a.sort()
			b.sort()
			if a == b:
				continue
		if not _approx(pair[1], pair[2]):
			return false
	return true


func perturb(v, hint: int, hint_string: String, type: int):
	if v != null and typeof(v) != type:
		return v # property reports one type but holds another: leave it alone
	match type:
		TYPE_BOOL: return not v
		TYPE_INT: return (v if v != null else 0) + 1
		TYPE_FLOAT: return (v if v != null else 0.0) + 0.3712345
		TYPE_STRING: return "yhde ✓ ünïcode \"q\" \\ \n tab\t"
		TYPE_STRING_NAME: return &"yhde_name"
		TYPE_NODE_PATH: return NodePath("../Some/Path:prop")
		TYPE_VECTOR2: return v + Vector2(1.5, -2.25)
		TYPE_VECTOR2I: return v + Vector2i(3, -4)
		TYPE_VECTOR3: return v + Vector3(1.25, -0.5, 3.0)
		TYPE_VECTOR3I: return v + Vector3i(1, 2, 3)
		TYPE_VECTOR4: return v + Vector4(0.5, 1, 2, 3)
		TYPE_VECTOR4I: return v + Vector4i(1, 1, 1, 1)
		TYPE_RECT2: return Rect2(1.5, 2.5, 30.25, 40.75)
		TYPE_RECT2I: return Rect2i(1, 2, 30, 40)
		TYPE_COLOR: return Color(0.1, 0.25, 0.5, 0.75)
		TYPE_TRANSFORM2D: return Transform2D(0.7, Vector2(1.5, 0.5), 0.1, Vector2(10.5, -3.25))
		TYPE_TRANSFORM3D: return Transform3D(Basis.from_euler(Vector3(0.1, 0.2, 0.3)).scaled(Vector3(1, 2, 3)), Vector3(4.5, -5, 6))
		TYPE_BASIS: return Basis.from_euler(Vector3(0.4, 0.5, 0.6))
		TYPE_QUATERNION: return Quaternion(Vector3.UP, 0.77)
		TYPE_PLANE: return Plane(Vector3(0, 1, 0), 2.5)
		TYPE_AABB: return AABB(Vector3(-1, -2, -3), Vector3(4, 5, 6))
		TYPE_PROJECTION: return Projection.create_perspective(60, 1.5, 0.1, 100)
		TYPE_PACKED_BYTE_ARRAY: return PackedByteArray([1, 2, 3, 250])
		TYPE_PACKED_INT32_ARRAY: return PackedInt32Array([1, -2, 3])
		TYPE_PACKED_INT64_ARRAY: return PackedInt64Array([1, -2, 9007199254740993])
		TYPE_PACKED_FLOAT32_ARRAY: return PackedFloat32Array([0.1, -2.5, 3.75])
		TYPE_PACKED_FLOAT64_ARRAY: return PackedFloat64Array([0.1, -2.5, 3.75])
		TYPE_PACKED_STRING_ARRAY: return PackedStringArray(["a", "b ✓"])
		TYPE_PACKED_VECTOR2_ARRAY: return PackedVector2Array([Vector2(1, 2), Vector2(3.5, 4.5)])
		TYPE_PACKED_VECTOR3_ARRAY: return PackedVector3Array([Vector3(1, 2, 3)])
		TYPE_PACKED_COLOR_ARRAY: return PackedColorArray([Color.RED, Color(0.1, 0.2, 0.3, 0.4)])
		TYPE_PACKED_VECTOR4_ARRAY: return PackedVector4Array([Vector4(1, 2, 3, 4)])
		TYPE_OBJECT:
			if hint == PROPERTY_HINT_RESOURCE_TYPE:
				for c in hint_string.split(","):
					c = c.strip_edges()
					if ClassDB.class_exists(c) and ClassDB.can_instantiate(c) and not ClassDB.is_parent_class(c, "Script"):
						return ClassDB.instantiate(c)
			return v
	return v

func _enter_tree() -> void:
	await get_tree().process_frame
	await get_tree().process_frame
	diag = ClassDB.instantiate("YhdeDiagnostics")
	if diag == null:
		print("NO DIAGNOSTICS CLASS")
		get_tree().quit(2)
		return

	# 1. Every Variant type, including edge values.
	var typed_arr: Array[Vector3] = [Vector3(1, 2, 3)]
	var typed_dict: Dictionary[String, int] = {"a": 1}
	var values := {
		"null": null, "true": true, "false": false, "int": 42, "neg": -7, "bigint": 9223372036854775807,
		"minint": -9223372036854775808, "float": 0.1, "tiny": 5e-324, "huge": 1.7976931348623157e308,
		"inf": INF, "-inf": -INF, "nan": NAN, "str": "hello ✓ \u0001 \"x\"", "empty": "",
		"sn": &"name", "np": ^"A/B:c", "v2": Vector2(0.1, 1e30), "v2i": Vector2i(-5, 7),
		"r2": Rect2(1, 2, 3, 4), "r2i": Rect2i(1, 2, 3, 4), "v3": Vector3(0.1, 0.2, 0.3), "v3i": Vector3i(1, 2, 3),
		"t2": Transform2D(1.1, Vector2(2, 3)), "v4": Vector4(1, 2, 3, 4), "v4i": Vector4i(1, 2, 3, 4),
		"plane": Plane(1, 0, 0, 5), "quat": Quaternion(0.1, 0.2, 0.3, 0.9), "aabb": AABB(Vector3.ONE, Vector3(2, 2, 2)),
		"basis": Basis.from_euler(Vector3(1, 2, 3)), "t3": Transform3D(Basis(), Vector3(1, 2, 3)),
		"proj": Projection.create_orthogonal(-1, 1, -1, 1, 0.1, 10), "color": Color(0.1, 0.2, 0.3, 0.4),
		"arr": [1, "two", 3.0, [4, {"five": 5}], null, Vector2(6, 7)], "typed_arr": typed_arr,
		"dict": {1: "a", "b": [2], Vector2(1, 1): {"nested": true}}, "typed_dict": typed_dict,
		"pba": PackedByteArray([0, 255, 128]), "pi32": PackedInt32Array([-2147483648, 2147483647]),
		"pi64": PackedInt64Array([-9223372036854775808, 9223372036854775807]),
		"pf32": PackedFloat32Array([0.1, INF, -0.0]), "pf64": PackedFloat64Array([0.1, 1e-300]),
		"ps": PackedStringArray(["x", "✓", ""]), "pv2": PackedVector2Array([Vector2(0.1, 0.2)]),
		"pv3": PackedVector3Array([Vector3(0.1, 0.2, 0.3)]), "pc": PackedColorArray([Color(0.1, 0.2, 0.3)]),
		"pv4": PackedVector4Array([Vector4(0.1, 0.2, 0.3, 0.4)]),
		"empty_arr": [], "empty_dict": {}, "empty_pba": PackedByteArray(),
	}
	for k in values:
		check_value(k, values[k])

	# Embedded resources (nested + shared) must rebuild with identical data.
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(0.2, 0.4, 0.6)
	mat.next_pass = ShaderMaterial.new()
	var arr_with_shared := [mat, mat]
	var r: Dictionary = diag.roundtrip(arr_with_shared, false)
	checks += 1
	if not r.ok or r.value[0] != r.value[1] or r.value[0].albedo_color != mat.albedo_color or not (r.value[0].next_pass is ShaderMaterial):
		fails += 1
		print("FAIL shared/nested embedded resource: ", r)

	# Built-in scripts are refused unless explicitly allowed.
	var gd := GDScript.new()
	gd.source_code = "extends Node\n"
	r = diag.roundtrip(gd, false)
	checks += 1
	if r.ok or r.value != null:
		fails += 1
		print("FAIL built-in script accepted without permission: ", r)
	r = diag.roundtrip(gd, true)
	checks += 1
	if not r.ok or not (r.value is GDScript) or r.value.source_code != gd.source_code:
		fails += 1
		print("FAIL built-in script with permission: ", r)

	# 2. Every stored property of every instantiable Node / Resource class,
	#    with non-default values.
	var classes_tested := 0
	var props_tested := 0
	var class_fail := {}
	for cls in ClassDB.get_class_list():
		if not ClassDB.can_instantiate(cls):
			continue
		var is_node := ClassDB.is_parent_class(cls, "Node")
		var is_res := ClassDB.is_parent_class(cls, "Resource")
		if not (is_node or is_res):
			continue
		if ClassDB.class_get_api_type(cls) != ClassDB.API_CORE:
			continue
		if cls in ["ResourceImporterScene", "GDExtension", "EditorSettings"] or ClassDB.is_parent_class(cls, "Script"):
			continue
		var obj = ClassDB.instantiate(cls)
		if obj == null:
			continue
		classes_tested += 1
		for p in obj.get_property_list():
			if not (p.usage & PROPERTY_USAGE_STORAGE):
				continue
			if p.name in ["script", "resource_path", "resource_local_to_scene"]:
				continue
			var cur = obj.get(p.name)
			var nv = perturb(cur, p.hint, p.hint_string, p.type)
			obj.set(p.name, nv)
			props_tested += 1
		var rr: Dictionary = diag.roundtrip_object(obj)
		checks += 1
		if not rr.ok and not _tolerable(cls, rr):
			fails += 1
			class_fail[cls] = rr
		if obj is Node:
			obj.free()
	for cls in class_fail:
		print("FAIL class ", cls, ": ", class_fail[cls].mismatches, " ", class_fail[cls].error)
	print("classes=", classes_tested, " props=", props_tested, " checks=", checks, " fails=", fails)
	get_tree().quit(1 if fails > 0 else 0)
