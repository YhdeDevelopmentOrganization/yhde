#include "sync/variant_codec.h"

#include <godot_cpp/classes/class_db_singleton.hpp>
#include <godot_cpp/classes/json.hpp>
#include <godot_cpp/classes/marshalls.hpp>
#include <godot_cpp/classes/resource_loader.hpp>
#include <godot_cpp/classes/resource_uid.hpp>
#include <godot_cpp/classes/script.hpp>
#include <godot_cpp/core/object.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/typed_array.hpp>

#include <cmath>
#include <cstring>

using namespace godot;

namespace yhde {

namespace {

constexpr int kMaxDepth = 64;
constexpr double kMaxSafeInt = 9007199254740992.0; // 2^53


// Scalars

Variant enc_real(double v) {
	if (std::isnan(v)) return String("nan");
	if (std::isinf(v)) return String(v > 0 ? "inf" : "-inf");
	return v;
}

bool dec_real(const Variant &v, double &out) {
	switch (v.get_type()) {
		case Variant::FLOAT:
			out = double(v);
			return true;
		case Variant::INT:
			out = double(int64_t(v));
			return true;
		case Variant::STRING: {
			String s = v;
			if (s == "nan") {
				out = std::nan("");
				return true;
			}
			if (s == "inf") {
				out = INFINITY;
				return true;
			}
			if (s == "-inf") {
				out = -INFINITY;
				return true;
			}
			return false;
		}
		default:
			return false;
	}
}

Variant enc_int(int64_t v) {
	if (double(v) > kMaxSafeInt || double(v) < -kMaxSafeInt) return String::num_int64(v);
	return v;
}

bool dec_int(const Variant &v, int64_t &out) {
	switch (v.get_type()) {
		case Variant::INT:
			out = int64_t(v);
			return true;
		case Variant::FLOAT: {
			double d = double(v);
			if (!std::isfinite(d)) return false;
			out = int64_t(d);
			return true;
		}
		case Variant::STRING: {
			String s = v;
			if (!s.is_valid_int()) return false;
			out = s.to_int();
			return true;
		}
		default:
			return false;
	}
}

// Reads `count` reals from a tagged array starting at index 1.
bool reals(const Array &a, int count, double *out) {
	if (a.size() != count + 1) return false;
	for (int i = 0; i < count; i++) {
		if (!dec_real(a[i + 1], out[i])) return false;
	}
	return true;
}

bool ints(const Array &a, int count, int64_t *out) {
	if (a.size() != count + 1) return false;
	for (int i = 0; i < count; i++) {
		if (!dec_int(a[i + 1], out[i])) return false;
	}
	return true;
}

Array tagged(const char *tag) {
	Array a;
	a.push_back(String(tag));
	return a;
}

// Packed arrays

String b64(const PackedByteArray &bytes) {
	if (bytes.is_empty()) return String();
	return Marshalls::get_singleton()->raw_to_base64(bytes);
}

bool unb64(const Variant &v, PackedByteArray &out) {
	if (v.get_type() != Variant::STRING) return false;
	String s = v;
	if (s.is_empty()) {
		out = PackedByteArray();
		return true;
	}
	out = Marshalls::get_singleton()->base64_to_raw(s);
	return true;
}

template <typename TVec, int N>
Variant enc_real_vectors(const char *tag, const PackedByteArray &raw) {
	Array a = tagged(tag);
	a.push_back(b64(raw));
	a.push_back(int64_t(sizeof(real_t)));
	return a;
}

// Rebuilds reals from raw bytes of either precision.
bool raw_reals(const Array &a, std::vector<double> &out) {
	if (a.size() != 3) return false;
	PackedByteArray raw;
	if (!unb64(a[1], raw)) return false;
	int64_t size = 0;
	if (!dec_int(a[2], size)) return false;
	if (size == 4) {
		PackedFloat32Array f = raw.to_float32_array();
		out.resize(size_t(f.size()));
		for (int64_t i = 0; i < f.size(); i++) out[size_t(i)] = f[i];
		return int64_t(raw.size()) == f.size() * 4;
	}
	if (size == 8) {
		PackedFloat64Array f = raw.to_float64_array();
		out.resize(size_t(f.size()));
		for (int64_t i = 0; i < f.size(); i++) out[size_t(i)] = f[i];
		return int64_t(raw.size()) == f.size() * 8;
	}
	return false;
}

// Objects

bool is_script_class(const StringName &cls) {
	return ClassDBSingleton::get_singleton()->is_parent_class(cls, "Script");
}

Variant encode_resource(const Ref<Resource> &res, EncodeState &st, const String &hint);

Variant encode_object(Object *obj, EncodeState &st, const String &hint) {
	if (!obj) return Variant();
	if (Node *node = Object::cast_to<Node>(obj)) {
		String id = st.host ? st.host->codec_node_id(node) : String();
		String path = st.host ? st.host->codec_node_path(node) : String();
		if (id.is_empty() && path.is_empty()) return Variant();
		Array a = tagged("node");
		a.push_back(id);
		a.push_back(path);
		return a;
	}
	if (Resource *r = Object::cast_to<Resource>(obj)) {
		return encode_resource(Ref<Resource>(r), st, hint);
	}
	// Plain Objects/RefCounteds are never stored in scene files.
	return Variant();
}

Variant encode_resource(const Ref<Resource> &res, EncodeState &st, const String &hint) {
	if (res.is_null()) return Variant();
	bool embedded = st.host && st.host->codec_is_embedded(res);
	if (!embedded) {
		String path = res->get_path();
		if (path.is_empty()) return Variant(); // runtime-only resource, not saved in any file
		Array a = tagged("res");
		a.push_back(path);
		String uid;
		if (!path.contains("::")) {
			int64_t id = ResourceLoader::get_singleton()->get_resource_uid(path);
			if (id != ResourceUID::INVALID_ID) uid = ResourceUID::get_singleton()->id_to_text(id);
		}
		a.push_back(uid);
		return a;
	}

	if (st.refs_only) {
		String known = st.host->codec_known_embedded_id(res);
		if (known.is_empty()) return Variant();
		Array a = tagged("subref");
		a.push_back(known);
		return a;
	}

	String id = st.host->codec_embedded_id(res, hint);
	std::string key = id.utf8().get_data();
	if (st.emitted.count(key)) {
		Array a = tagged("subref");
		a.push_back(id);
		return a;
	}
	st.emitted.insert(key);

	Array a = tagged("sub");
	a.push_back(id);
	a.push_back(res->get_class());
	a.push_back(encode_properties(res.ptr(), st, id, false));
	return a;
}

Ref<Resource> load_external(const String &path, const String &uid_text, DecodeState &st) {
	String resolved = path;
	if (!uid_text.is_empty()) {
		int64_t id = ResourceUID::get_singleton()->text_to_id(uid_text);
		if (id != ResourceUID::INVALID_ID && ResourceUID::get_singleton()->has_id(id)) {
			String by_uid = ResourceUID::get_singleton()->get_id_path(id);
			if (is_safe_resource_path(by_uid)) resolved = by_uid;
		}
	}
	if (!is_safe_resource_path(resolved)) {
		st.fail(String("Refused resource path ") + resolved);
		return Ref<Resource>();
	}
	ResourceLoader *loader = ResourceLoader::get_singleton();
	if (resolved.contains("::")) {
		// Built-in resource of another file: only reachable through the cache.
		if (!loader->has_cached(resolved)) {
			String file = resolved.get_slice("::", 0);
			if (loader->exists(file)) loader->load(file);
		}
		Ref<Resource> cached = loader->has_cached(resolved) ? loader->get_cached_ref(resolved) : Ref<Resource>();
		if (cached.is_null()) st.fail(String("Missing built-in resource ") + resolved);
		return cached;
	}
	if (!loader->exists(resolved)) {
		st.fail(String("Missing resource ") + resolved);
		return Ref<Resource>();
	}
	Ref<Resource> res = loader->load(resolved);
	if (res.is_null()) st.fail(String("Could not load ") + resolved);
	return res;
}

Variant decode_sub(const Array &a, DecodeState &st) {
	if (a.size() != 4 || a[1].get_type() != Variant::STRING || a[2].get_type() != Variant::STRING ||
			a[3].get_type() != Variant::ARRAY) {
		st.fail("Malformed embedded resource");
		return Variant();
	}
	String id = a[1];
	StringName cls = String(a[2]);
	if (!st.host) {
		st.fail("No document for embedded resource");
		return Variant();
	}
	ClassDBSingleton *db = ClassDBSingleton::get_singleton();
	if (!db->class_exists(cls) || !db->is_parent_class(cls, "Resource") || !db->can_instantiate(cls) ||
			class_is_blocked(cls, st.allow_builtin_scripts)) {
		st.fail(String("Refused to create resource of class ") + String(cls));
		return Variant();
	}

	Ref<Resource> res = st.host->codec_find_embedded(id);
	if (res.is_null() || res->get_class() != String(cls)) {
		Variant created = db->instantiate(cls);
		res = created;
		if (res.is_null()) {
			st.fail(String("Could not create ") + String(cls));
			return Variant();
		}
		res->set_meta(kIdMeta, id);
		st.host->codec_register_embedded(id, res);
	}
	st.touched.push_back(res);
	apply_properties(res.ptr(), a[3], st, nullptr);
	return res;
}

} // namespace

// Public helpers

bool is_safe_resource_path(const String &path) {
	if (!(path.begins_with("res://") || path.begins_with("uid://"))) return false;
	String file = path.get_slice("::", 0);
	return !file.contains("..") && !file.contains("\\");
}

String resource_file_of(const Ref<Resource> &res) {
	if (res.is_null()) return String();
	return res->get_path().get_slice("::", 0);
}

bool class_is_blocked(const StringName &cls, bool allow_builtin_scripts) {
	if (is_script_class(cls)) return !allow_builtin_scripts;
	static const char *denied[] = { "GDExtension", "EditorSettings", "EditorFeatureProfile" };
	for (const char *d : denied) {
		if (String(cls) == d) return true;
	}
	// Editor classes never belong in a scene or resource (a game cannot even
	// load them), and creating one from a teammate's operation could reach
	// into the editor itself.
	ClassDBSingleton *db = ClassDBSingleton::get_singleton();
	if (db && db->class_exists(cls)) {
		ClassDBSingleton::APIType api = db->class_get_api_type(cls);
		if (api == ClassDBSingleton::API_EDITOR || api == ClassDBSingleton::API_EDITOR_EXTENSION) return true;
	}
	return false;
}

String to_json(const Variant &v) {
	return JSON::stringify(v, String(), false, true);
}

bool from_json(const String &text, Variant &out) {
	Ref<JSON> json;
	json.instantiate();
	if (json->parse(text) != OK) return false;
	out = json->get_data();
	return true;
}

std::vector<StoredProperty> stored_properties(Object *object) {
	std::vector<StoredProperty> out;
	if (!object) return out;
	TypedArray<Dictionary> list = object->get_property_list();
	bool has_script = false;
	out.reserve(size_t(list.size()));
	for (int64_t i = 0; i < list.size(); i++) {
		Dictionary d = list[i];
		int64_t usage = d.get("usage", 0);
		if (!(usage & PROPERTY_USAGE_STORAGE)) continue;
		if (usage & (PROPERTY_USAGE_CATEGORY | PROPERTY_USAGE_GROUP | PROPERTY_USAGE_SUBGROUP)) continue;
		String name = d.get("name", String());
		if (name.is_empty() || name == kIdMetaProperty || name == "resource_path" ||
				name == "resource_scene_unique_id") {
			continue;
		}
		if (name == "script") {
			has_script = true;
			continue;
		}
		out.push_back({ StringName(name), int(int64_t(d.get("type", 0))) });
	}
	if (has_script) out.insert(out.begin(), { StringName("script"), int(Variant::OBJECT) });
	return out;
}

bool values_equal(const Variant &a, const Variant &b) {
	if (a.get_type() != b.get_type()) return false;
	return a.hash_compare(b);
}

Variant snapshot_value(const Variant &v) {
	switch (v.get_type()) {
		case Variant::ARRAY:
			return Array(v).duplicate(true);
		case Variant::DICTIONARY:
			return Dictionary(v).duplicate(true);
		default:
			return v;
	}
}

// Encode

Variant encode_value(const Variant &value, EncodeState &st, const String &hint) {
	if (st.depth > kMaxDepth) return Variant();
	struct DepthGuard {
		int &d;
		explicit DepthGuard(int &x) : d(x) { d++; }
		~DepthGuard() { d--; }
	} guard(st.depth);

	switch (value.get_type()) {
		case Variant::NIL:
			return Variant();
		case Variant::BOOL:
			return bool(value);
		case Variant::INT: {
			Array a = tagged("i");
			a.push_back(enc_int(int64_t(value)));
			return a;
		}
		case Variant::FLOAT: {
			Array a = tagged("f");
			a.push_back(enc_real(double(value)));
			return a;
		}
		case Variant::STRING:
			return String(value);
		case Variant::STRING_NAME: {
			Array a = tagged("sn");
			a.push_back(String(StringName(value)));
			return a;
		}
		case Variant::NODE_PATH: {
			Array a = tagged("np");
			a.push_back(String(NodePath(value)));
			return a;
		}
		case Variant::VECTOR2: {
			Vector2 v = value;
			Array a = tagged("v2");
			a.push_back(enc_real(v.x));
			a.push_back(enc_real(v.y));
			return a;
		}
		case Variant::VECTOR2I: {
			Vector2i v = value;
			Array a = tagged("v2i");
			a.push_back(int64_t(v.x));
			a.push_back(int64_t(v.y));
			return a;
		}
		case Variant::RECT2: {
			Rect2 r = value;
			Array a = tagged("r2");
			a.push_back(enc_real(r.position.x));
			a.push_back(enc_real(r.position.y));
			a.push_back(enc_real(r.size.x));
			a.push_back(enc_real(r.size.y));
			return a;
		}
		case Variant::RECT2I: {
			Rect2i r = value;
			Array a = tagged("r2i");
			a.push_back(int64_t(r.position.x));
			a.push_back(int64_t(r.position.y));
			a.push_back(int64_t(r.size.x));
			a.push_back(int64_t(r.size.y));
			return a;
		}
		case Variant::VECTOR3: {
			Vector3 v = value;
			Array a = tagged("v3");
			a.push_back(enc_real(v.x));
			a.push_back(enc_real(v.y));
			a.push_back(enc_real(v.z));
			return a;
		}
		case Variant::VECTOR3I: {
			Vector3i v = value;
			Array a = tagged("v3i");
			a.push_back(int64_t(v.x));
			a.push_back(int64_t(v.y));
			a.push_back(int64_t(v.z));
			return a;
		}
		case Variant::TRANSFORM2D: {
			Transform2D t = value;
			Array a = tagged("t2");
			for (int c = 0; c < 3; c++) {
				a.push_back(enc_real(t.columns[c].x));
				a.push_back(enc_real(t.columns[c].y));
			}
			return a;
		}
		case Variant::VECTOR4: {
			Vector4 v = value;
			Array a = tagged("v4");
			a.push_back(enc_real(v.x));
			a.push_back(enc_real(v.y));
			a.push_back(enc_real(v.z));
			a.push_back(enc_real(v.w));
			return a;
		}
		case Variant::VECTOR4I: {
			Vector4i v = value;
			Array a = tagged("v4i");
			a.push_back(int64_t(v.x));
			a.push_back(int64_t(v.y));
			a.push_back(int64_t(v.z));
			a.push_back(int64_t(v.w));
			return a;
		}
		case Variant::PLANE: {
			Plane p = value;
			Array a = tagged("pl");
			a.push_back(enc_real(p.normal.x));
			a.push_back(enc_real(p.normal.y));
			a.push_back(enc_real(p.normal.z));
			a.push_back(enc_real(p.d));
			return a;
		}
		case Variant::QUATERNION: {
			Quaternion q = value;
			Array a = tagged("q");
			a.push_back(enc_real(q.x));
			a.push_back(enc_real(q.y));
			a.push_back(enc_real(q.z));
			a.push_back(enc_real(q.w));
			return a;
		}
		case Variant::AABB: {
			godot::AABB b = value;
			Array a = tagged("ab");
			a.push_back(enc_real(b.position.x));
			a.push_back(enc_real(b.position.y));
			a.push_back(enc_real(b.position.z));
			a.push_back(enc_real(b.size.x));
			a.push_back(enc_real(b.size.y));
			a.push_back(enc_real(b.size.z));
			return a;
		}
		case Variant::BASIS: {
			Basis b = value;
			Array a = tagged("b");
			for (int r = 0; r < 3; r++) {
				a.push_back(enc_real(b.rows[r].x));
				a.push_back(enc_real(b.rows[r].y));
				a.push_back(enc_real(b.rows[r].z));
			}
			return a;
		}
		case Variant::TRANSFORM3D: {
			Transform3D t = value;
			Array a = tagged("t3");
			for (int r = 0; r < 3; r++) {
				a.push_back(enc_real(t.basis.rows[r].x));
				a.push_back(enc_real(t.basis.rows[r].y));
				a.push_back(enc_real(t.basis.rows[r].z));
			}
			a.push_back(enc_real(t.origin.x));
			a.push_back(enc_real(t.origin.y));
			a.push_back(enc_real(t.origin.z));
			return a;
		}
		case Variant::PROJECTION: {
			Projection p = value;
			Array a = tagged("pj");
			for (int c = 0; c < 4; c++) {
				a.push_back(enc_real(p.columns[c].x));
				a.push_back(enc_real(p.columns[c].y));
				a.push_back(enc_real(p.columns[c].z));
				a.push_back(enc_real(p.columns[c].w));
			}
			return a;
		}
		case Variant::COLOR: {
			Color c = value;
			Array a = tagged("c");
			a.push_back(enc_real(c.r));
			a.push_back(enc_real(c.g));
			a.push_back(enc_real(c.b));
			a.push_back(enc_real(c.a));
			return a;
		}
		case Variant::RID:
		case Variant::CALLABLE:
		case Variant::SIGNAL:
			// Runtime handles: never part of saved state.
			return Variant();
		case Variant::OBJECT:
			return encode_object(value.get_validated_object(), st, hint);
		case Variant::DICTIONARY: {
			Dictionary d = value;
			Array flat;
			Array keys = d.keys();
			for (int64_t i = 0; i < keys.size(); i++) {
				String h = hint + String("#k") + String::num_int64(i);
				flat.push_back(encode_value(keys[i], st, h));
				flat.push_back(encode_value(d[keys[i]], st, h + "v"));
			}
			Array a = tagged("d");
			a.push_back(flat);
			if (d.is_typed()) {
				a.push_back(d.get_typed_key_builtin());
				a.push_back(String(d.get_typed_key_class_name()));
				a.push_back(encode_value(d.get_typed_key_script(), st, hint + String("#kt")));
				a.push_back(d.get_typed_value_builtin());
				a.push_back(String(d.get_typed_value_class_name()));
				a.push_back(encode_value(d.get_typed_value_script(), st, hint + String("#vt")));
			}
			return a;
		}
		case Variant::ARRAY: {
			Array arr = value;
			Array items;
			for (int64_t i = 0; i < arr.size(); i++) {
				items.push_back(encode_value(arr[i], st, hint + String("#") + String::num_int64(i)));
			}
			Array a = tagged("a");
			a.push_back(items);
			if (arr.is_typed()) {
				a.push_back(arr.get_typed_builtin());
				a.push_back(String(arr.get_typed_class_name()));
				a.push_back(encode_value(arr.get_typed_script(), st, hint + String("#t")));
			}
			return a;
		}
		case Variant::PACKED_BYTE_ARRAY: {
			Array a = tagged("pb");
			a.push_back(b64(value));
			return a;
		}
		case Variant::PACKED_INT32_ARRAY: {
			Array a = tagged("pi32");
			a.push_back(b64(PackedInt32Array(value).to_byte_array()));
			return a;
		}
		case Variant::PACKED_INT64_ARRAY: {
			Array a = tagged("pi64");
			a.push_back(b64(PackedInt64Array(value).to_byte_array()));
			return a;
		}
		case Variant::PACKED_FLOAT32_ARRAY: {
			Array a = tagged("pf");
			a.push_back(b64(PackedFloat32Array(value).to_byte_array()));
			return a;
		}
		case Variant::PACKED_FLOAT64_ARRAY: {
			Array a = tagged("pd");
			a.push_back(b64(PackedFloat64Array(value).to_byte_array()));
			return a;
		}
		case Variant::PACKED_STRING_ARRAY: {
			PackedStringArray s = value;
			Array items;
			for (int64_t i = 0; i < s.size(); i++) items.push_back(s[i]);
			Array a = tagged("ps");
			a.push_back(items);
			return a;
		}
		case Variant::PACKED_VECTOR2_ARRAY:
			return enc_real_vectors<Vector2, 2>("pv2", PackedVector2Array(value).to_byte_array());
		case Variant::PACKED_VECTOR3_ARRAY:
			return enc_real_vectors<Vector3, 3>("pv3", PackedVector3Array(value).to_byte_array());
		case Variant::PACKED_VECTOR4_ARRAY:
			return enc_real_vectors<Vector4, 4>("pv4", PackedVector4Array(value).to_byte_array());
		case Variant::PACKED_COLOR_ARRAY: {
			Array a = tagged("pc");
			a.push_back(b64(PackedColorArray(value).to_byte_array()));
			return a;
		}
		default:
			return Variant();
	}
}

Array encode_properties(Object *object, EncodeState &st, const String &hint_base, bool skip_defaults) {
	Array out;
	if (!object) return out;
	ClassDBSingleton *db = ClassDBSingleton::get_singleton();
	StringName cls = object->get_class();
	Ref<Script> script = object->get_script();
	for (const StoredProperty &p : stored_properties(object)) {
		Variant v = object->get(p.name);
		if (skip_defaults) {
			Variant def;
			bool known = false;
			if (script.is_valid() && p.name != StringName("script")) {
				def = script->get_property_default_value(p.name);
				known = def.get_type() != Variant::NIL || v.get_type() == Variant::NIL;
			}
			if (!known) {
				def = db->class_get_property_default_value(cls, p.name);
				known = def.get_type() != Variant::NIL || v.get_type() == Variant::NIL;
			}
			if (known && values_equal(v, def)) continue;
		}
		out.push_back(String(p.name));
		out.push_back(encode_value(v, st, hint_base + String("/prop:") + String(p.name)));
	}
	return out;
}

// Decode

Variant decode_value(const Variant &json, DecodeState &st) {
	if (st.depth > kMaxDepth) {
		st.fail("Value nested too deeply");
		return Variant();
	}
	struct DepthGuard {
		int &d;
		explicit DepthGuard(int &x) : d(x) { d++; }
		~DepthGuard() { d--; }
	} guard(st.depth);

	switch (json.get_type()) {
		case Variant::NIL:
			return Variant();
		case Variant::BOOL:
			return bool(json);
		case Variant::STRING:
			return String(json);
		case Variant::ARRAY:
			break;
		default:
			st.fail("Untagged value");
			return Variant();
	}

	Array a = json;
	if (a.is_empty() || a[0].get_type() != Variant::STRING) {
		st.fail("Missing type tag");
		return Variant();
	}
	String tag = a[0];
	double r[16];
	int64_t n[4];

	if (tag == "i") {
		if (a.size() == 2 && dec_int(a[1], n[0])) return n[0];
	} else if (tag == "f") {
		if (a.size() == 2 && dec_real(a[1], r[0])) return r[0];
	} else if (tag == "sn") {
		if (a.size() == 2 && a[1].get_type() == Variant::STRING) return StringName(String(a[1]));
	} else if (tag == "np") {
		if (a.size() == 2 && a[1].get_type() == Variant::STRING) return NodePath(String(a[1]));
	} else if (tag == "v2") {
		if (reals(a, 2, r)) return Vector2(real_t(r[0]), real_t(r[1]));
	} else if (tag == "v2i") {
		if (ints(a, 2, n)) return Vector2i(int32_t(n[0]), int32_t(n[1]));
	} else if (tag == "r2") {
		if (reals(a, 4, r)) return Rect2(real_t(r[0]), real_t(r[1]), real_t(r[2]), real_t(r[3]));
	} else if (tag == "r2i") {
		if (ints(a, 4, n)) return Rect2i(int32_t(n[0]), int32_t(n[1]), int32_t(n[2]), int32_t(n[3]));
	} else if (tag == "v3") {
		if (reals(a, 3, r)) return Vector3(real_t(r[0]), real_t(r[1]), real_t(r[2]));
	} else if (tag == "v3i") {
		if (ints(a, 3, n)) return Vector3i(int32_t(n[0]), int32_t(n[1]), int32_t(n[2]));
	} else if (tag == "t2") {
		if (reals(a, 6, r)) {
			Transform2D t;
			for (int c = 0; c < 3; c++) t.columns[c] = Vector2(real_t(r[c * 2]), real_t(r[c * 2 + 1]));
			return t;
		}
	} else if (tag == "v4") {
		if (reals(a, 4, r)) return Vector4(real_t(r[0]), real_t(r[1]), real_t(r[2]), real_t(r[3]));
	} else if (tag == "v4i") {
		if (ints(a, 4, n)) return Vector4i(int32_t(n[0]), int32_t(n[1]), int32_t(n[2]), int32_t(n[3]));
	} else if (tag == "pl") {
		if (reals(a, 4, r)) return Plane(Vector3(real_t(r[0]), real_t(r[1]), real_t(r[2])), real_t(r[3]));
	} else if (tag == "q") {
		if (reals(a, 4, r)) return Quaternion(real_t(r[0]), real_t(r[1]), real_t(r[2]), real_t(r[3]));
	} else if (tag == "ab") {
		if (reals(a, 6, r)) {
			return godot::AABB(Vector3(real_t(r[0]), real_t(r[1]), real_t(r[2])),
					Vector3(real_t(r[3]), real_t(r[4]), real_t(r[5])));
		}
	} else if (tag == "b") {
		if (reals(a, 9, r)) {
			Basis b;
			for (int row = 0; row < 3; row++) {
				b.rows[row] = Vector3(real_t(r[row * 3]), real_t(r[row * 3 + 1]), real_t(r[row * 3 + 2]));
			}
			return b;
		}
	} else if (tag == "t3") {
		if (reals(a, 12, r)) {
			Transform3D t;
			for (int row = 0; row < 3; row++) {
				t.basis.rows[row] = Vector3(real_t(r[row * 3]), real_t(r[row * 3 + 1]), real_t(r[row * 3 + 2]));
			}
			t.origin = Vector3(real_t(r[9]), real_t(r[10]), real_t(r[11]));
			return t;
		}
	} else if (tag == "pj") {
		if (reals(a, 16, r)) {
			Projection p;
			for (int c = 0; c < 4; c++) {
				p.columns[c] = Vector4(real_t(r[c * 4]), real_t(r[c * 4 + 1]), real_t(r[c * 4 + 2]), real_t(r[c * 4 + 3]));
			}
			return p;
		}
	} else if (tag == "c") {
		if (reals(a, 4, r)) return Color(float(r[0]), float(r[1]), float(r[2]), float(r[3]));
	} else if (tag == "a") {
		if ((a.size() == 2 || a.size() == 5) && a[1].get_type() == Variant::ARRAY) {
			Array src = a[1];
			Array out;
			out.resize(src.size());
			for (int64_t i = 0; i < src.size(); i++) out[i] = decode_value(src[i], st);
			if (a.size() == 5) {
				int64_t type = 0;
				if (!dec_int(a[2], type) || type < 0 || type >= Variant::VARIANT_MAX) {
					st.fail("Bad typed array");
					return Variant();
				}
				Variant script = decode_value(a[4], st);
				return Array(out, type, StringName(String(a[3])), script);
			}
			return out;
		}
	} else if (tag == "d") {
		if ((a.size() == 2 || a.size() == 8) && a[1].get_type() == Variant::ARRAY) {
			Array flat = a[1];
			if (flat.size() % 2 != 0) {
				st.fail("Bad dictionary");
				return Variant();
			}
			Dictionary out;
			for (int64_t i = 0; i < flat.size(); i += 2) {
				Variant k = decode_value(flat[i], st);
				out[k] = decode_value(flat[i + 1], st);
			}
			if (a.size() == 8) {
				int64_t kt = 0, vt = 0;
				if (!dec_int(a[2], kt) || !dec_int(a[5], vt) || kt < 0 || vt < 0 || kt >= Variant::VARIANT_MAX ||
						vt >= Variant::VARIANT_MAX) {
					st.fail("Bad typed dictionary");
					return Variant();
				}
				Variant ks = decode_value(a[4], st);
				Variant vs = decode_value(a[7], st);
				return Dictionary(out, kt, StringName(String(a[3])), ks, vt, StringName(String(a[6])), vs);
			}
			return out;
		}
	} else if (tag == "pb") {
		PackedByteArray raw;
		if (a.size() == 2 && unb64(a[1], raw)) return raw;
	} else if (tag == "pi32") {
		PackedByteArray raw;
		if (a.size() == 2 && unb64(a[1], raw) && raw.size() % 4 == 0) return raw.to_int32_array();
	} else if (tag == "pi64") {
		PackedByteArray raw;
		if (a.size() == 2 && unb64(a[1], raw) && raw.size() % 8 == 0) return raw.to_int64_array();
	} else if (tag == "pd") {
		PackedByteArray raw;
		if (a.size() == 2 && unb64(a[1], raw) && raw.size() % 8 == 0) return raw.to_float64_array();
	} else if (tag == "pf") {
		PackedByteArray raw;
		if (a.size() == 2 && unb64(a[1], raw) && raw.size() % 4 == 0) return raw.to_float32_array();
	} else if (tag == "ps") {
		if (a.size() == 2 && a[1].get_type() == Variant::ARRAY) {
			Array src = a[1];
			PackedStringArray out;
			out.resize(src.size());
			for (int64_t i = 0; i < src.size(); i++) out[i] = String(src[i]);
			return out;
		}
	} else if (tag == "pv2" || tag == "pv3" || tag == "pv4") {
		std::vector<double> f;
		int dim = tag == "pv2" ? 2 : (tag == "pv3" ? 3 : 4);
		if (raw_reals(a, f) && f.size() % size_t(dim) == 0) {
			int64_t count = int64_t(f.size()) / dim;
			if (dim == 2) {
				PackedVector2Array out;
				out.resize(count);
				for (int64_t i = 0; i < count; i++) out[i] = Vector2(real_t(f[i * 2]), real_t(f[i * 2 + 1]));
				return out;
			}
			if (dim == 3) {
				PackedVector3Array out;
				out.resize(count);
				for (int64_t i = 0; i < count; i++) {
					out[i] = Vector3(real_t(f[i * 3]), real_t(f[i * 3 + 1]), real_t(f[i * 3 + 2]));
				}
				return out;
			}
			PackedVector4Array out;
			out.resize(count);
			for (int64_t i = 0; i < count; i++) {
				out[i] = Vector4(real_t(f[i * 4]), real_t(f[i * 4 + 1]), real_t(f[i * 4 + 2]), real_t(f[i * 4 + 3]));
			}
			return out;
		}
	} else if (tag == "pc") {
		PackedByteArray raw;
		if (a.size() == 2 && unb64(a[1], raw) && raw.size() % 16 == 0) {
			PackedFloat32Array f = raw.to_float32_array();
			PackedColorArray out;
			out.resize(f.size() / 4);
			for (int64_t i = 0; i < out.size(); i++) out[i] = Color(f[i * 4], f[i * 4 + 1], f[i * 4 + 2], f[i * 4 + 3]);
			return out;
		}
	} else if (tag == "node") {
		if (a.size() == 3 && a[1].get_type() == Variant::STRING && a[2].get_type() == Variant::STRING) {
			Node *node = st.host ? st.host->codec_find_node(a[1], a[2]) : nullptr;
			if (!node) {
				st.missing_node = true;
				st.fail("Referenced node not found");
				return Variant();
			}
			return node;
		}
	} else if (tag == "res") {
		if (a.size() == 3 && a[1].get_type() == Variant::STRING && a[2].get_type() == Variant::STRING) {
			Ref<Resource> res = load_external(a[1], a[2], st);
			return res.is_valid() ? Variant(res) : Variant();
		}
	} else if (tag == "sub") {
		return decode_sub(a, st);
	} else if (tag == "subref") {
		if (a.size() == 2 && a[1].get_type() == Variant::STRING && st.host) {
			Ref<Resource> res = st.host->codec_find_embedded(a[1]);
			if (res.is_valid()) return res;
			st.fail("Unknown embedded resource reference");
			return Variant();
		}
	}

	st.fail(String("Malformed value with tag ") + tag);
	return Variant();
}

bool apply_properties(Object *object, const Array &props, DecodeState &st, Array *deferred_node_refs) {
	if (!object || props.size() % 2 != 0) {
		st.fail("Malformed property list");
		return false;
	}
	bool all_ok = true;
	for (int64_t i = 0; i < props.size(); i += 2) {
		if (props[i].get_type() != Variant::STRING) continue;
		StringName name = String(props[i]);
		if (String(name) == kIdMetaProperty) continue;

		DecodeState sub;
		sub.host = st.host;
		sub.allow_builtin_scripts = st.allow_builtin_scripts;
		sub.depth = st.depth;
		Variant value = decode_value(props[i + 1], sub);
		for (const Ref<Resource> &r : sub.touched) st.touched.push_back(r);
		if (!sub.ok) {
			all_ok = false;
			if (sub.missing_node && deferred_node_refs) {
				Array entry;
				entry.push_back(int64_t(object->get_instance_id()));
				entry.push_back(String(name));
				entry.push_back(props[i + 1]);
				deferred_node_refs->push_back(entry);
			} else {
				st.fail(String(name) + ": " + sub.error);
			}
			continue;
		}
		Variant current = object->get(name);
		if (values_equal(current, value)) continue;
		object->set(name, value);
	}
	return all_ok;
}

} // namespace yhde
