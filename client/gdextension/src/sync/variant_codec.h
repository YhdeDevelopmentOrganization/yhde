#pragma once

#include <godot_cpp/classes/node.hpp>
#include <godot_cpp/classes/resource.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/string.hpp>
#include <godot_cpp/variant/string_name.hpp>
#include <godot_cpp/variant/variant.hpp>

#include <string>
#include <unordered_set>
#include <vector>

// Lossless, type-tagged JSON encoding for every Godot Variant type.
//
// Operation payloads are JSON (the server stores them as JSONB). Godot's own
// text serializers either lose types (JSON), or can instantiate arbitrary
// objects including scripts (str_to_var): unacceptable for data that arrives
// from other clients. This codec is explicit about every type, bit-exact for
// floats, and only ever creates objects under a strict policy.
//
// Encoded forms (JSON):
//   null / true / false / "string"
//   ["i", n]            int (n is a string beyond ±2^53)
//   ["f", n]            float ("inf", "-inf", "nan" as strings)
//   ["sn", s] ["np", s] StringName, NodePath
//   ["v2", x, y] …      math types, one tag per type
//   ["a", [..], t, cls, script]           Array (typed info optional)
//   ["d", [k,v,..], kt,kc,ks, vt,vc,vs]   Dictionary (typed info optional)
//   ["pb", b64] ["pi32", b64] ["pi64", b64] ["pf", b64] ["pd", b64]
//   ["ps", [..]] ["pv2"|"pv3"|"pv4", b64, real_size] ["pc", b64]
//                       packed arrays (raw little-endian bytes, base64)
//   ["node", id, path]  reference to a node of the same scene
//   ["res", path, uid]  external resource file
//   ["sub", id, class, [k, v, ...]]  embedded (built-in) resource
//   ["subref", id]      embedded resource already emitted in this value
namespace yhde {

// Implemented by the document that owns the objects being encoded/decoded.
class CodecHost {
public:
	virtual ~CodecHost() = default;

	// Encoding: id of a node inside this document ("" when it is not tracked).
	virtual godot::String codec_node_id(godot::Node *node) = 0;
	virtual godot::String codec_node_path(godot::Node *node) = 0;
	// Encoding: stable id for a resource embedded in this document. `hint` is a
	// deterministic location ("<owner id>/prop:name") used when the resource has
	// never been seen before.
	virtual godot::String codec_embedded_id(const godot::Ref<godot::Resource> &res, const godot::String &hint) = 0;
	// Id of an embedded resource already tracked, or "" (never registers one).
	virtual godot::String codec_known_embedded_id(const godot::Ref<godot::Resource> &res) = 0;
	// True when the resource is saved inside this document (built-in).
	virtual bool codec_is_embedded(const godot::Ref<godot::Resource> &res) = 0;

	// Decoding.
	virtual godot::Node *codec_find_node(const godot::String &id, const godot::String &path) = 0;
	virtual godot::Ref<godot::Resource> codec_find_embedded(const godot::String &id) = 0;
	virtual void codec_register_embedded(const godot::String &id, const godot::Ref<godot::Resource> &res) = 0;
};

struct EncodeState {
	CodecHost *host = nullptr;
	// Describe embedded resources by reference only (used for "old" values,
	// which must never register new identities).
	bool refs_only = false;
	std::unordered_set<std::string> emitted;
	int depth = 0;
};

struct DecodeState {
	CodecHost *host = nullptr;
	bool allow_builtin_scripts = false;
	bool ok = true;
	// A ["node"] reference could not be resolved yet (it may be created later
	// in the same batch): the caller should retry the property after the batch.
	bool missing_node = false;
	godot::String error;
	int depth = 0;
	// Embedded resources touched while decoding (for shadow refresh).
	std::vector<godot::Ref<godot::Resource>> touched;

	void fail(const godot::String &why) {
		if (ok) error = why;
		ok = false;
	}
};

// One property as reported by get_property_list(), filtered to what a scene
// file would store.
struct StoredProperty {
	godot::StringName name;
	int type = 0;
};

// Properties that are saved with the object, "script" first. Excludes YHDE's
// own identity metadata and bookkeeping fields that must not be synchronized.
std::vector<StoredProperty> stored_properties(godot::Object *object);

godot::Variant encode_value(const godot::Variant &value, EncodeState &st, const godot::String &hint);
godot::Variant decode_value(const godot::Variant &json, DecodeState &st);

// Serializes every stored property as ["name", value, "name", value, ...].
// With `skip_defaults`, properties equal to the class/script defaults are
// omitted (compact CreateNode payloads for freshly instantiated nodes).
godot::Array encode_properties(godot::Object *object, EncodeState &st, const godot::String &hint_base,
		bool skip_defaults);

// Applies ["name", value, ...]; returns false if any value failed to decode.
// Unchanged values are not re-set (avoids spurious `changed` signals).
bool apply_properties(godot::Object *object, const godot::Array &props, DecodeState &st,
		godot::Array *deferred_node_refs);

// Deep equality that treats NaN == NaN and compares objects by identity.
bool values_equal(const godot::Variant &a, const godot::Variant &b);

// Deep copy for shadow state (Arrays/Dictionaries are reference types).
godot::Variant snapshot_value(const godot::Variant &v);

// Resource classification.
bool is_safe_resource_path(const godot::String &path);
godot::String resource_file_of(const godot::Ref<godot::Resource> &res);
bool class_is_blocked(const godot::StringName &cls, bool allow_builtin_scripts);

// JSON text helpers (full float precision).
godot::String to_json(const godot::Variant &v);
bool from_json(const godot::String &text, godot::Variant &out);

// Hidden (underscore) metadata that stores an object's YHDE id in the file.
constexpr const char *kIdMeta = "_yhde_id";
// The same metadata as a property name (how scene files store it).
constexpr const char *kIdMetaProperty = "metadata/_yhde_id";
// ChangeResourceProperty key that replaces a resource's whole stored state
// (its value is a property list), for edits that remove entries a single
// property cannot express: a deleted tile, a removed TileSet layer…
constexpr const char *kStateKey = "@state";

} // namespace yhde
