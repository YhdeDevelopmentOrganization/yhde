#include "diagnostics/yhde_diagnostics.h"

#include "core/uuid.h"
#include "sync/scene_document.h"
#include "sync/variant_codec.h"

#include <godot_cpp/classes/class_db_singleton.hpp>
#include <godot_cpp/classes/node.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/variant/utility_functions.hpp>

#include <unordered_map>

namespace godot {

namespace {

// Stand-alone codec host: embedded resources are tracked in a flat map.
class ScratchHost : public yhde::CodecHost {
public:
	std::unordered_map<uint64_t, String> ids;
	std::unordered_map<std::string, Ref<Resource>> by_id;
	Node *root = nullptr;

	String codec_node_id(Node *node) override { return String(); }
	String codec_node_path(Node *node) override {
		if (!root || !node) return String();
		if (node != root && !root->is_ancestor_of(node)) return String();
		return String(root->get_path_to(node));
	}
	String codec_embedded_id(const Ref<Resource> &res, const String &hint) override {
		auto it = ids.find(res->get_instance_id());
		if (it != ids.end()) return it->second;
		String id = yhde::Uuid::random().to_string();
		ids[res->get_instance_id()] = id;
		by_id[id.utf8().get_data()] = res;
		return id;
	}
	String codec_known_embedded_id(const Ref<Resource> &res) override {
		auto it = ids.find(res->get_instance_id());
		return it == ids.end() ? String() : it->second;
	}
	bool codec_is_embedded(const Ref<Resource> &res) override {
		return res.is_valid() && (res->get_path().is_empty() || res->get_path().contains("::"));
	}
	Node *codec_find_node(const String &id, const String &path) override {
		return root && !path.is_empty() ? root->get_node_or_null(NodePath(path)) : nullptr;
	}
	Ref<Resource> codec_find_embedded(const String &id) override {
		auto it = by_id.find(id.utf8().get_data());
		return it == by_id.end() ? Ref<Resource>() : it->second;
	}
	void codec_register_embedded(const String &id, const Ref<Resource> &res) override {
		by_id[id.utf8().get_data()] = res;
	}
};

} // namespace

Dictionary YhdeDiagnostics::roundtrip(const Variant &value, bool allow_builtin_scripts) {
	ScratchHost enc_host;
	yhde::EncodeState es;
	es.host = &enc_host;
	Variant encoded = yhde::encode_value(value, es, "diag");
	String json = yhde::to_json(encoded);

	Dictionary out;
	out["json"] = json;
	Variant parsed;
	if (!yhde::from_json(json, parsed)) {
		out["ok"] = false;
		out["error"] = "JSON did not parse";
		return out;
	}
	ScratchHost dec_host; // fresh: embedded resources must be rebuilt, not reused
	yhde::DecodeState ds;
	ds.host = &dec_host;
	ds.allow_builtin_scripts = allow_builtin_scripts;
	Variant decoded = yhde::decode_value(parsed, ds);
	out["ok"] = ds.ok;
	out["error"] = ds.error;
	out["value"] = decoded;
	return out;
}

Dictionary YhdeDiagnostics::roundtrip_object(Object *object) {
	Dictionary out;
	PackedStringArray mismatches;
	Array pairs; // [name, original, rebuilt] for callers that judge tolerance
	if (!object) {
		out["ok"] = false;
		return out;
	}
	ScratchHost enc_host;
	enc_host.root = Object::cast_to<Node>(object);
	yhde::EncodeState es;
	es.host = &enc_host;
	Array props = yhde::encode_properties(object, es, "diag", false);
	String json = yhde::to_json(props);
	Variant parsed;
	yhde::from_json(json, parsed);

	Variant made = ClassDBSingleton::get_singleton()->instantiate(object->get_class());
	Object *copy = made;
	ScratchHost dec_host;
	yhde::DecodeState ds;
	ds.host = &dec_host;
	yhde::apply_properties(copy, parsed, ds, nullptr);

	for (const yhde::StoredProperty &p : yhde::stored_properties(object)) {
		Variant a = object->get(p.name);
		Variant b = copy->get(p.name);
		bool same = yhde::values_equal(a, b);
		if (!same && a.get_type() == Variant::OBJECT && b.get_type() == Variant::OBJECT) {
			// Embedded resources are rebuilt as new objects: compare by class.
			Object *oa = a.get_validated_object();
			Object *ob = b.get_validated_object();
			same = oa && ob && oa->get_class() == ob->get_class();
		}
		if (!same) {
			mismatches.push_back(String(p.name) + String(": ") + UtilityFunctions::var_to_str(a) + String(" -> ") + UtilityFunctions::var_to_str(b));
			Array pair;
			pair.push_back(String(p.name));
			pair.push_back(a);
			pair.push_back(b);
			pairs.push_back(pair);
		}
	}
	out["ok"] = ds.ok && mismatches.is_empty();
	out["error"] = ds.error;
	out["mismatches"] = mismatches;
	out["pairs"] = pairs;
	out["json_bytes"] = json.utf8().length();
	if (Node *n = Object::cast_to<Node>(copy)) {
		n->queue_free();
	}
	return out;
}

bool YhdeDiagnostics::values_equal(const Variant &a, const Variant &b) {
	return yhde::values_equal(a, b);
}

PackedStringArray YhdeDiagnostics::stored_properties(Object *object) {
	PackedStringArray out;
	for (const yhde::StoredProperty &p : yhde::stored_properties(object)) out.push_back(String(p.name));
	return out;
}

YhdeDiagnostics::YhdeDiagnostics() {}
YhdeDiagnostics::~YhdeDiagnostics() {}

int64_t YhdeDiagnostics::mirror_open(const String &path, Object *a, Object *b) {
	std::unique_ptr<yhde::SyncDocument> da, db;
	if (Node *na = Object::cast_to<Node>(a)) {
		da = yhde::SyncDocument::for_scene(path, na);
		db = yhde::SyncDocument::for_scene(path, Object::cast_to<Node>(b));
	} else {
		da = yhde::SyncDocument::for_resource(path, Ref<Resource>(Object::cast_to<Resource>(a)));
		db = yhde::SyncDocument::for_resource(path, Ref<Resource>(Object::cast_to<Resource>(b)));
	}
	da->adopt();
	db->adopt();
	mirrors_.emplace_back(std::move(da), std::move(db));
	return int64_t(mirrors_.size()) - 1;
}

Dictionary YhdeDiagnostics::mirror_step(int64_t handle) {
	Dictionary out;
	if (handle < 0 || handle >= int64_t(mirrors_.size()) || !mirrors_[size_t(handle)].first) return out;
	yhde::SyncDocument &a = *mirrors_[size_t(handle)].first;
	yhde::SyncDocument &b = *mirrors_[size_t(handle)].second;
	std::vector<yhde::LocalOp> ops;
	a.diff(ops, nullptr, nullptr);
	Array sent;
	PackedStringArray errors;
	yhde::ApplyContext ctx;
	for (const yhde::LocalOp &op : ops) {
		Dictionary wire;
		wire["t"] = String::utf8(op.type.c_str());
		wire["id"] = op.target.to_string();
		wire["p"] = op.payload;
		String json = yhde::to_json(wire);
		sent.push_back(json);
		Variant parsed;
		if (!yhde::from_json(json, parsed)) {
			errors.push_back("JSON did not parse: " + json);
			continue;
		}
		Dictionary d = parsed;
		yhde::Uuid target;
		yhde::Uuid::parse(d["id"], target);
		if (!b.apply(op.type, target, d["p"], ctx)) errors.push_back("not applied: " + json.left(300));
	}
	b.finish_batch(ctx);
	for (int64_t i = 0; i < ctx.errors.size(); i++) errors.push_back(ctx.errors[i]);
	std::vector<yhde::LocalOp> echo_ops;
	b.diff(echo_ops, nullptr, nullptr);
	Array echo;
	for (const yhde::LocalOp &op : echo_ops) {
		Dictionary wire;
		wire["t"] = String::utf8(op.type.c_str());
		wire["p"] = op.payload;
		echo.push_back(yhde::to_json(wire).left(400));
	}
	out["ops"] = sent;
	out["errors"] = errors;
	out["echo"] = echo;
	return out;
}

void YhdeDiagnostics::mirror_close(int64_t handle) {
	if (handle < 0 || handle >= int64_t(mirrors_.size())) return;
	mirrors_[size_t(handle)].first.reset();
	mirrors_[size_t(handle)].second.reset();
}

void YhdeDiagnostics::_bind_methods() {
	ClassDB::bind_method(D_METHOD("mirror_open", "path", "a", "b"), &YhdeDiagnostics::mirror_open);
	ClassDB::bind_method(D_METHOD("mirror_step", "handle"), &YhdeDiagnostics::mirror_step);
	ClassDB::bind_method(D_METHOD("mirror_close", "handle"), &YhdeDiagnostics::mirror_close);
	ClassDB::bind_method(D_METHOD("roundtrip", "value", "allow_builtin_scripts"), &YhdeDiagnostics::roundtrip);
	ClassDB::bind_method(D_METHOD("roundtrip_object", "object"), &YhdeDiagnostics::roundtrip_object);
	ClassDB::bind_method(D_METHOD("values_equal", "a", "b"), &YhdeDiagnostics::values_equal);
	ClassDB::bind_method(D_METHOD("stored_properties", "object"), &YhdeDiagnostics::stored_properties);
}

} // namespace godot
