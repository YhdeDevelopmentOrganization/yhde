#include "sync/scene_document.h"

#include "sync/property_watch.h"

#include <godot_cpp/classes/class_db_singleton.hpp>
#include <godot_cpp/classes/control.hpp>
#include <godot_cpp/classes/editor_inspector.hpp>
#include <godot_cpp/classes/editor_interface.hpp>
#include <godot_cpp/classes/editor_selection.hpp>
#include <godot_cpp/classes/file_access.hpp>
#include <godot_cpp/classes/node2d.hpp>
#include <godot_cpp/classes/node3d.hpp>
#include <godot_cpp/classes/packed_scene.hpp>
#include <godot_cpp/classes/resource_loader.hpp>
#include <godot_cpp/classes/script.hpp>
#include <godot_cpp/classes/tile_map.hpp>
#include <godot_cpp/classes/tile_map_layer.hpp>
#include <godot_cpp/classes/tile_set.hpp>
#include <godot_cpp/classes/tile_set_source.hpp>
#include <godot_cpp/core/object.hpp>
#include <godot_cpp/variant/callable.hpp>
#include <godot_cpp/variant/signal.hpp>
#include <godot_cpp/variant/typed_array.hpp>

#include <algorithm>
#include <functional>

using namespace godot;

namespace yhde {

namespace {

const Variant *find_prop(const PropList &list, const StringName &name, size_t hint) {
	if (hint < list.size() && list[hint].first == name) return &list[hint].second;
	for (const auto &p : list) {
		if (p.first == name) return &p.second;
	}
	return nullptr;
}

Variant *find_prop_mut(PropList &list, const StringName &name, size_t hint) {
	return const_cast<Variant *>(find_prop(list, name, hint));
}

void set_prop(PropList &list, const StringName &name, const Variant &value, size_t hint) {
	if (Variant *v = find_prop_mut(list, name, hint)) {
		*v = snapshot_value(value);
	} else {
		list.emplace_back(name, snapshot_value(value));
	}
}

// The stored-property names of an object, reusing the names recorded in its
// shadow unless the engine announced a property-list change. A new script
// changes the list at once while the engine announces it only later, so a
// changed script always means a fresh list.
std::vector<StoredProperty> cached_properties(Object *o, uint64_t oid, const PropList &shadow, bool *fresh) {
	*fresh = true;
	if (take_property_list_change(oid) || shadow.empty()) return stored_properties(o);
	static const StringName script_name("script");
	for (const auto &p : shadow) {
		if (p.first == script_name) {
			if (!values_equal(o->get(script_name), p.second)) return stored_properties(o);
			break;
		}
	}
	*fresh = false;
	std::vector<StoredProperty> out;
	out.reserve(shadow.size());
	for (const auto &p : shadow) out.push_back({ p.first, int(p.second.get_type()) });
	return out;
}

// Drops shadow entries for properties the object no longer stores (a toggle
// can hide its dependent settings); a stale entry would look like an edit.
void prune_props(PropList &list, const std::vector<StoredProperty> &stored) {
	std::unordered_set<std::string> keep;
	keep.reserve(stored.size());
	for (const StoredProperty &p : stored) keep.insert(String(p.name).utf8().get_data());
	list.erase(std::remove_if(list.begin(), list.end(),
					   [&](const auto &e) { return !keep.count(String(e.first).utf8().get_data()); }),
			list.end());
}

bool has_prop(const std::vector<StoredProperty> &list, const StringName &name) {
	for (const StoredProperty &p : list) {
		if (p.name == name) return true;
	}
	return false;
}

PropList read_props(Object *o) {
	PropList out;
	for (const StoredProperty &p : stored_properties(o)) out.emplace_back(p.name, snapshot_value(o->get(p.name)));
	return out;
}

// A shadow property list in the wire form of encode_properties.
Array encode_prop_list(const PropList &list, EncodeState &st, const String &hint_base) {
	Array out;
	for (const auto &p : list) {
		out.push_back(String(p.first));
		out.push_back(encode_value(p.second, st, hint_base + String("/prop:") + String(p.first)));
	}
	return out;
}

// Tile nodes queue their cell changes and fold them in on the next frame
// (erased cells read back as empty entries until then). Settle them before
// their properties are read so a half-applied state is never sent or kept.
void settle_tiles(Node *n) {
	if (TileMapLayer *layer = Object::cast_to<TileMapLayer>(n)) {
		layer->update_internals();
	} else if (TileMap *map = Object::cast_to<TileMap>(n)) {
		map->update_internals();
	}
}

// TileMap grows and shrinks its layer_N/* properties without announcing it:
// always read its list fresh.
bool has_silent_property_list(Node *n) { return Object::cast_to<TileMap>(n) != nullptr; }

bool is_uuid_key(const std::string &k) { return !k.empty() && k[0] != '~'; }

std::string key_of(const Uuid &id) { return id.to_string().utf8().get_data(); }

// Weighted longest increasing subsequence: marks which positions keep their
// relative order (heavy weights for children that cannot be moved).
std::vector<bool> stable_positions(const std::vector<int> &seq, const std::vector<int> &weight) {
	size_t n = seq.size();
	std::vector<long long> best(n, 0);
	std::vector<int> prev(n, -1);
	for (size_t i = 0; i < n; i++) {
		best[i] = weight[i];
		for (size_t j = 0; j < i; j++) {
			if (seq[j] < seq[i] && best[j] + weight[i] > best[i]) {
				best[i] = best[j] + weight[i];
				prev[i] = int(j);
			}
		}
	}
	std::vector<bool> stable(n, false);
	if (n == 0) return stable;
	size_t end = 0;
	for (size_t i = 1; i < n; i++) {
		if (best[i] > best[end]) end = i;
	}
	for (int i = int(end); i >= 0; i = prev[size_t(i)]) stable[size_t(i)] = true;
	return stable;
}

void simulate_move(std::vector<std::string> &list, const std::string &key, int to) {
	auto it = std::find(list.begin(), list.end(), key);
	if (it == list.end()) return;
	list.erase(it);
	to = std::max(0, std::min(to, int(list.size())));
	list.insert(list.begin() + to, key);
}

// Moves that transform `sim` into `target` using Node.move_child semantics.
std::vector<std::pair<std::string, int>> plan_reorder(std::vector<std::string> sim, const std::vector<std::string> &target,
		const std::function<bool(const std::string &)> &movable) {
	std::vector<std::pair<std::string, int>> moves;
	if (sim == target) return moves;

	bool same_set = sim.size() == target.size();
	if (same_set) {
		std::vector<std::string> a = sim, b = target;
		std::sort(a.begin(), a.end());
		std::sort(b.begin(), b.end());
		same_set = a == b;
	}

	if (same_set && target.size() <= 2000) {
		std::unordered_map<std::string, int> pos;
		for (size_t i = 0; i < sim.size(); i++) pos[sim[i]] = int(i);
		std::vector<int> seq(target.size()), weight(target.size());
		for (size_t i = 0; i < target.size(); i++) {
			seq[i] = pos[target[i]];
			weight[i] = movable(target[i]) ? 1 : 100000;
		}
		std::vector<bool> stable = stable_positions(seq, weight);
		std::vector<std::string> trial = sim;
		bool ok = true;
		for (size_t i = 0; i < target.size(); i++) {
			if (stable[i]) continue;
			if (!movable(target[i])) {
				ok = false;
				break;
			}
			moves.emplace_back(target[i], int(i));
			simulate_move(trial, target[i], int(i));
		}
		if (ok && trial == target) return moves;
		moves.clear();
	}

	// Fallback: place every movable child at its final index, in order.
	for (size_t i = 0; i < target.size(); i++) {
		if (!movable(target[i])) continue;
		auto it = std::find(sim.begin(), sim.end(), target[i]);
		if (it != sim.end() && size_t(it - sim.begin()) == i) continue;
		moves.emplace_back(target[i], int(i));
		if (it == sim.end()) {
			sim.insert(sim.begin() + std::min(i, sim.size()), target[i]);
		} else {
			simulate_move(sim, target[i], int(i));
		}
	}
	return moves;
}

} // namespace

String op_key_for(const std::string &type, const Dictionary &payload) {
	if (type == "ChangeProperty" || type == "ChangeResourceProperty") return payload.get("k", String());
	if (type == "MoveNode") return "@parent";
	if (type == "RenameNode") return "@name";
	if (type == "ReorderNode") return "@index";
	if (type == "AddResource") return "@res";
	if (type == "ChangeNodeType") return "@type";
	if (type == "SetSceneRoot") return "@root";
	if (type == "RegisterAsset" || type == "UpdateAsset" || type == "MoveAsset" || type == "DeleteAsset") return "@file";
	return "@node";
}

// Construction

std::unique_ptr<SyncDocument> SyncDocument::for_scene(const String &path, Node *root) {
	std::unique_ptr<SyncDocument> d(new SyncDocument());
	d->kind_ = Kind::Scene;
	d->path_ = path;
	d->ns_ = Uuid::from_name(yhde_namespace(), "scene:" + path);
	d->root_oid_ = root ? root->get_instance_id() : 0;
	return d;
}

std::unique_ptr<SyncDocument> SyncDocument::for_resource(const String &path, const Ref<Resource> &res) {
	std::unique_ptr<SyncDocument> d(new SyncDocument());
	d->kind_ = Kind::Resource;
	d->path_ = path;
	d->ns_ = Uuid::from_name(yhde_namespace(), "resource:" + path);
	d->root_res_ = res;
	d->root_oid_ = res.is_valid() ? res->get_instance_id() : 0;
	d->root_id_ = d->derive("root");
	return d;
}

Node *SyncDocument::root_node() const {
	if (kind_ != Kind::Scene || root_oid_ == 0) return nullptr;
	return Object::cast_to<Node>(ObjectDB::get_instance(root_oid_));
}

Node *SyncDocument::node(const Uuid &id) const {
	auto it = nodes_.find(id);
	if (it == nodes_.end()) return nullptr;
	return Object::cast_to<Node>(ObjectDB::get_instance(it->second.oid));
}

Uuid SyncDocument::node_id(Node *n) const {
	if (!n) return Uuid();
	auto it = node_by_oid_.find(n->get_instance_id());
	return it == node_by_oid_.end() ? Uuid() : it->second;
}

LocalOp SyncDocument::make_op(const char *type, const Uuid &target, const String &key) const {
	LocalOp op;
	op.type = type;
	op.target = target;
	op.key = key;
	op.payload["s"] = path_;
	return op;
}

// CodecHost

String SyncDocument::codec_node_id(Node *n) {
	Uuid id = node_id(n);
	return id.is_nil() ? String() : id.to_string();
}

String SyncDocument::codec_node_path(Node *n) {
	Node *root = root_node();
	if (!root || !n) return String();
	if (n != root && !root->is_ancestor_of(n)) return String();
	return String(root->get_path_to(n));
}

bool SyncDocument::codec_is_embedded(const Ref<Resource> &res) {
	if (res.is_null()) return false;
	if (kind_ == Kind::Resource && res == root_res_) return false;
	String p = res->get_path();
	if (p.is_empty()) return true;
	int sep = p.find("::");
	return sep >= 0 && p.substr(0, sep) == path_;
}

String SyncDocument::codec_known_embedded_id(const Ref<Resource> &res) {
	if (res.is_null()) return String();
	auto it = res_by_oid_.find(res->get_instance_id());
	return it == res_by_oid_.end() ? String() : it->second.to_string();
}

String SyncDocument::codec_embedded_id(const Ref<Resource> &res, const String &hint) {
	uint64_t oid = res->get_instance_id();
	auto it = res_by_oid_.find(oid);
	if (it != res_by_oid_.end()) return it->second.to_string();

	Uuid id;
	if (adopting_) {
		Uuid meta = Uuid::parse_or_nil(String(res->get_meta(kIdMeta, String())));
		if (!meta.is_nil() && !res_.count(meta) && !retired_.count(meta)) {
			id = meta;
		} else {
			Uuid d = derive("res:" + hint);
			id = res_.count(d) ? Uuid::random() : d;
		}
	} else {
		id = Uuid::random();
	}
	if (String(res->get_meta(kIdMeta, String())) != id.to_string()) res->set_meta(kIdMeta, id.to_string());

	ResEntry e;
	e.id = id;
	e.oid = oid;
	e.res = res;
	e.pending = true;
	res_[id] = e;
	res_by_oid_[oid] = id;
	return id.to_string();
}

Node *SyncDocument::codec_find_node(const String &id, const String &path) {
	Uuid u;
	if (Uuid::parse(id, u)) {
		if (Node *n = node(u)) return n;
	}
	Node *root = root_node();
	if (root && !path.is_empty()) return root->get_node_or_null(NodePath(path));
	return nullptr;
}

Ref<Resource> SyncDocument::codec_find_embedded(const String &id) {
	Uuid u;
	if (!Uuid::parse(id, u)) return Ref<Resource>();
	auto it = res_.find(u);
	return it == res_.end() ? Ref<Resource>() : it->second.res;
}

void SyncDocument::codec_register_embedded(const String &id, const Ref<Resource> &res) {
	Uuid u;
	if (!Uuid::parse(id, u) || res.is_null()) return;
	auto old = res_.find(u);
	if (old != res_.end()) res_by_oid_.erase(old->second.oid);
	ResEntry e;
	e.id = u;
	e.oid = res->get_instance_id();
	e.res = res;
	e.pending = true;
	res_[u] = e;
	res_by_oid_[e.oid] = u;
}

// Walking the scene

std::vector<std::string> SyncDocument::child_keys_of(Node *parent, const std::unordered_map<uint64_t, Uuid> &ids) const {
	std::vector<std::string> keys;
	int count = parent->get_child_count();
	keys.reserve(size_t(count));
	for (int i = 0; i < count; i++) {
		Node *c = parent->get_child(i);
		auto it = ids.find(c->get_instance_id());
		if (it != ids.end()) {
			keys.push_back(key_of(it->second));
		} else {
			keys.push_back("~" + std::to_string(c->get_instance_id()));
		}
	}
	return keys;
}

void SyncDocument::walk(WalkMode mode, std::vector<Visit> &out) {
	Node *root = root_node();
	if (!root) return;

	std::unordered_map<uint64_t, Uuid> now;
	std::unordered_set<Uuid, UuidHash> used;

	auto own_id = [&](Node *n, const String &rel, bool &fresh, bool &skip, bool &retyped) -> Uuid {
		uint64_t oid = n->get_instance_id();
		auto it = node_by_oid_.find(oid);
		if (it != node_by_oid_.end() && !used.count(it->second)) {
			fresh = false;
			return it->second;
		}
		fresh = true;
		if (mode == WalkMode::Refresh) {
			skip = true;
			return Uuid();
		}
		Uuid id;
		String meta_text = n->get_meta(kIdMeta, String());
		if (mode == WalkMode::Adopt) {
			Uuid meta = Uuid::parse_or_nil(meta_text);
			// Whether a copied id is present depends on when that other file
			// was last saved on this machine; derive instead, the same way
			// everywhere.
			if (!meta.is_nil() && inherited_meta(n, root) == meta_text) meta = Uuid();
			if (!meta.is_nil() && !used.count(meta) && !retired_.count(meta)) {
				id = meta;
			} else {
				Uuid d = derive("node:" + rel);
				id = (used.count(d) || retired_.count(d)) ? Uuid::random() : d;
			}
		} else {
			// Change Type replaces a node by a new object of another class that
			// keeps its metadata (and so its identity); the old object has left
			// the scene. Anything else new (a paste, a duplicate) gets a new id.
			Uuid meta = Uuid::parse_or_nil(meta_text);
			auto known = nodes_.find(meta);
			bool replaced = false;
			if (!meta.is_nil() && known != nodes_.end() && !used.count(meta) && known->second.cls != n->get_class()) {
				Node *previous = Object::cast_to<Node>(ObjectDB::get_instance(known->second.oid));
				replaced = !previous || (previous != root && !root->is_ancestor_of(previous));
			}
			if (replaced) {
				id = meta;
				retyped = true;
			} else {
				id = Uuid::random();
			}
		}
		if (meta_text != id.to_string()) n->set_meta(kIdMeta, id.to_string());
		return id;
	};

	// Root: its identity is the document's.
	{
		Visit v;
		v.node = root;
		v.oid = root->get_instance_id();
		v.own = true;
		if (root_id_.is_nil()) {
			bool skip = false;
			bool retyped = false;
			root_id_ = own_id(root, ".", v.fresh, skip, retyped);
			if (skip) return;
		} else {
			auto it = node_by_oid_.find(v.oid);
			v.fresh = it == node_by_oid_.end();
			if (String(root->get_meta(kIdMeta, String())) != root_id_.to_string()) {
				root->set_meta(kIdMeta, root_id_.to_string());
			}
		}
		v.id = root_id_;
		used.insert(v.id);
		now[v.oid] = v.id;
		out.push_back(v);
	}

	std::function<void(Node *, const Uuid &)> recurse = [&](Node *parent, const Uuid &pid) {
		int count = parent->get_child_count();
		for (int i = 0; i < count; i++) {
			Node *c = parent->get_child(i);
			Node *owner = c->get_owner();
			bool own = owner == root;
			if (!own) {
				if (!owner || !now.count(owner->get_instance_id()) || !root->is_editable_instance(owner)) continue;
			}
			Visit v;
			v.node = c;
			v.oid = c->get_instance_id();
			v.parent = pid;
			v.own = own;
			if (own) {
				bool skip = false;
				v.id = own_id(c, String(root->get_path_to(c)), v.fresh, skip, v.retyped);
				if (skip) continue;
			} else {
				Uuid owner_id = now[owner->get_instance_id()];
				v.id = Uuid::from_name(owner_id, "inst:" + String(owner->get_path_to(c)));
				auto it = node_by_oid_.find(v.oid);
				v.fresh = it == node_by_oid_.end() || it->second != v.id;
			}
			if (used.count(v.id)) {
				if (!own || mode == WalkMode::Refresh) continue;
				v.id = Uuid::random();
				v.fresh = true;
				c->set_meta(kIdMeta, v.id.to_string());
			}
			used.insert(v.id);
			now[v.oid] = v.id;
			out.push_back(v);
			recurse(c, v.id);
		}
	};
	recurse(root, root_id_);
}

// Snapshots

void SyncDocument::collect_reach(const Variant &v, const String &hint, std::vector<Reach> &reach) const {
	switch (v.get_type()) {
		case Variant::OBJECT: {
			Resource *r = Object::cast_to<Resource>(v.get_validated_object());
			if (r) reach.push_back({ Ref<Resource>(r), hint });
			break;
		}
		case Variant::ARRAY: {
			Array a = v;
			// Only arrays that can hold objects need walking.
			if (a.is_typed() && a.get_typed_builtin() != Variant::OBJECT) break;
			for (int64_t i = 0; i < a.size(); i++) collect_reach(a[i], hint + String("#") + String::num_int64(i), reach);
			break;
		}
		case Variant::DICTIONARY: {
			Dictionary d = v;
			Array keys = d.keys();
			for (int64_t i = 0; i < keys.size(); i++) {
				String h = hint + String("#k") + String::num_int64(i);
				collect_reach(keys[i], h, reach);
				collect_reach(d[keys[i]], h + "v", reach);
			}
			break;
		}
		default:
			break;
	}
}

void SyncDocument::snapshot_node(NodeEntry &e, Node *n, std::vector<Reach> *reach) {
	watch_property_list(n);
	take_property_list_change(n->get_instance_id());
	e.props.clear();
	String base = e.id.to_string() + "/prop:";
	for (const StoredProperty &p : stored_properties(n)) {
		Variant v = n->get(p.name);
		if (reach) collect_reach(v, base + String(p.name), *reach);
		e.props.emplace_back(p.name, snapshot_value(v));
	}
	e.groups = persistent_groups(n);
	e.conns = connections_of(n);
}

void SyncDocument::snapshot_resource(ResEntry &e, std::vector<Reach> *reach) {
	e.props.clear();
	if (e.res.is_null()) return;
	watch_property_list(e.res.ptr());
	take_property_list_change(e.res->get_instance_id());
	String base = e.id.to_string() + "/prop:";
	for (const StoredProperty &p : stored_properties(e.res.ptr())) {
		Variant v = e.res->get(p.name);
		if (reach) collect_reach(v, base + String(p.name), *reach);
		e.props.emplace_back(p.name, snapshot_value(v));
	}
	e.pending = false;
}

bool SyncDocument::is_external_file_resource(const Ref<Resource> &res) const {
	if (res.is_null()) return false;
	String p = res->get_path();
	if (p.is_empty() || p.contains("::") || p == path_ || !is_safe_resource_path(p)) return false;
	String ext = p.get_extension().to_lower();
	if (ext != "tres" && ext != "res") return false;
	return !FileAccess::file_exists(p + ".import");
}

// Groups & connections

PackedStringArray SyncDocument::persistent_groups(Node *n) const {
	PackedStringArray out;
	TypedArray<StringName> groups = n->get_groups();
	for (int64_t i = 0; i < groups.size(); i++) {
		String g = StringName(groups[i]);
		if (g.begins_with("_")) continue; // engine-internal groups
		out.push_back(g);
	}
	out.sort();
	return out;
}

Array SyncDocument::connections_of(Node *n) {
	std::vector<std::pair<std::string, Array>> found;
	TypedArray<Dictionary> sigs = n->get_signal_list();
	EncodeState st;
	st.host = this;
	st.refs_only = true;
	for (int64_t s = 0; s < sigs.size(); s++) {
		StringName sig = Dictionary(sigs[s]).get("name", String());
		TypedArray<Dictionary> conns = n->get_signal_connection_list(sig);
		for (int64_t c = 0; c < conns.size(); c++) {
			Dictionary d = conns[c];
			int64_t flags = d.get("flags", 0);
			if (!(flags & Object::CONNECT_PERSIST)) continue;
			Callable cb = d.get("callable", Callable());
			Node *target = Object::cast_to<Node>(cb.get_object());
			if (!target) continue;
			String tid = codec_node_id(target);
			String tpath = codec_node_path(target);
			if (tid.is_empty() && tpath.is_empty()) continue;
			Array entry;
			entry.push_back(String(sig));
			entry.push_back(tid);
			entry.push_back(tpath);
			entry.push_back(String(cb.get_method()));
			entry.push_back(flags);
			entry.push_back(encode_value(cb.get_bound_arguments(), st, String()));
			entry.push_back(int64_t(cb.get_unbound_arguments_count()));
			std::string sort_key = (String(sig) + "|" + tid + "|" + tpath + "|" + String(cb.get_method())).utf8().get_data();
			found.emplace_back(sort_key, entry);
		}
	}
	std::sort(found.begin(), found.end(), [](const auto &a, const auto &b) { return a.first < b.first; });
	Array out;
	for (auto &f : found) out.push_back(f.second);
	return out;
}

std::unordered_map<uint64_t, Array> SyncDocument::outgoing_connections(const std::vector<Visit> &visits) {
	// Every persistent connection in a scene targets a node of that scene, so
	// asking each node for its *incoming* connections finds them all with one
	// call per node (instead of one per signal per node).
	std::unordered_map<uint64_t, std::vector<std::pair<std::string, Array>>> found;
	EncodeState st;
	st.host = this;
	st.refs_only = true;
	for (const Visit &v : visits) {
		TypedArray<Dictionary> incoming = v.node->get_incoming_connections();
		for (int64_t i = 0; i < incoming.size(); i++) {
			Dictionary d = incoming[i];
			int64_t flags = d.get("flags", 0);
			if (!(flags & Object::CONNECT_PERSIST)) continue;
			Signal sig = d.get("signal", Signal());
			Node *source = Object::cast_to<Node>(sig.get_object());
			if (!source || !node_by_oid_.count(source->get_instance_id())) continue;
			Callable cb = d.get("callable", Callable());
			String tid = codec_node_id(v.node);
			String tpath = codec_node_path(v.node);
			Array entry;
			entry.push_back(String(sig.get_name()));
			entry.push_back(tid);
			entry.push_back(tpath);
			entry.push_back(String(cb.get_method()));
			entry.push_back(flags);
			entry.push_back(encode_value(cb.get_bound_arguments(), st, String()));
			entry.push_back(int64_t(cb.get_unbound_arguments_count()));
			std::string key = (String(sig.get_name()) + "|" + tid + "|" + tpath + "|" + String(cb.get_method())).utf8().get_data();
			found[source->get_instance_id()].emplace_back(key, entry);
		}
	}
	std::unordered_map<uint64_t, Array> out;
	for (auto &kv : found) {
		std::sort(kv.second.begin(), kv.second.end(), [](const auto &a, const auto &b) { return a.first < b.first; });
		Array list;
		for (auto &e : kv.second) list.push_back(e.second);
		out[kv.first] = list;
	}
	return out;
}

void SyncDocument::apply_groups(Node *n, const PackedStringArray &groups) {
	PackedStringArray current = persistent_groups(n);
	for (int64_t i = 0; i < current.size(); i++) {
		if (!groups.has(current[i])) n->remove_from_group(current[i]);
	}
	for (int64_t i = 0; i < groups.size(); i++) {
		if (groups[i].begins_with("_")) continue;
		if (!current.has(groups[i])) n->add_to_group(groups[i], true);
	}
}

void SyncDocument::apply_connections(Node *n, const Array &wanted_raw) {
	// Bring the wire form into the exact local form (JSON numbers arrive as
	// floats; bound arguments are re-encoded locally) so entries compare.
	Array conns;
	{
		EncodeState st;
		st.host = this;
		st.refs_only = true;
		for (int64_t i = 0; i < wanted_raw.size(); i++) {
			if (wanted_raw[i].get_type() != Variant::ARRAY) continue;
			Array w = wanted_raw[i];
			if (w.size() != 7) continue;
			DecodeState ds;
			ds.host = this;
			Variant binds = decode_value(w[5], ds);
			if (binds.get_type() != Variant::ARRAY) binds = Array();
			Array norm;
			norm.push_back(String(w[0]));
			norm.push_back(String(w[1]));
			norm.push_back(String(w[2]));
			norm.push_back(String(w[3]));
			norm.push_back(int64_t(double(w[4])));
			norm.push_back(encode_value(binds, st, String()));
			norm.push_back(int64_t(double(w[6])));
			conns.push_back(norm);
		}
	}

	// Drop persistent connections that are no longer wanted.
	Array current = connections_of(n);
	for (int64_t i = 0; i < current.size(); i++) {
		Array c = current[i];
		bool keep = false;
		for (int64_t j = 0; j < conns.size() && !keep; j++) {
			Array w = conns[j];
			keep = w.size() == 7 && values_equal(w, c);
		}
		if (keep) continue;
		Node *target = codec_find_node(c[1], c[2]);
		if (!target) continue;
		StringName sig = String(c[0]);
		TypedArray<Dictionary> live = n->get_signal_connection_list(sig);
		for (int64_t k = 0; k < live.size(); k++) {
			Dictionary d = live[k];
			Callable cb = d.get("callable", Callable());
			if (cb.get_object() == target && String(cb.get_method()) == String(c[3])) n->disconnect(sig, cb);
		}
	}
	// Add the missing ones.
	current = connections_of(n);
	for (int64_t j = 0; j < conns.size(); j++) {
		if (conns[j].get_type() != Variant::ARRAY) continue;
		Array w = conns[j];
		if (w.size() != 7) continue;
		bool present = false;
		for (int64_t i = 0; i < current.size() && !present; i++) present = values_equal(Array(current[i]), w);
		if (present) continue;
		Node *target = codec_find_node(w[1], w[2]);
		if (!target) continue;
		StringName sig = String(w[0]);
		StringName method = String(w[3]);
		int64_t flags = w[4];
		int64_t unbinds = w[6];
		DecodeState st;
		st.host = this;
		Variant binds = decode_value(w[5], st);
		Callable cb(target, method);
		if (unbinds > 0) {
			cb = cb.unbind(unbinds);
		} else if (binds.get_type() == Variant::ARRAY && !Array(binds).is_empty()) {
			cb = cb.bindv(binds);
		}
		if (!n->has_signal(sig)) continue;
		if (n->is_connected(sig, cb)) continue;
		n->connect(sig, cb, uint32_t(flags | Object::CONNECT_PERSIST));
	}
}

// Adopt / announce

String SyncDocument::inherited_meta(Node *n, Node *root) const {
	auto meta_in = [](Ref<SceneState> st, const NodePath &path) -> String {
		for (; st.is_valid(); st = st->get_base_scene_state()) {
			for (int32_t i = 0; i < st->get_node_count(); i++) {
				if (st->get_node_path(i) != path) continue;
				for (int32_t k = 0; k < st->get_node_property_count(i); k++) {
					if (String(st->get_node_property_name(i, k)) == kIdMetaProperty) return st->get_node_property_value(i, k);
				}
			}
		}
		return String();
	};
	String inst = n != root ? n->get_scene_file_path() : String();
	if (!inst.is_empty()) {
		ResourceLoader *loader = ResourceLoader::get_singleton();
		if (!is_safe_resource_path(inst) || !loader->exists(inst)) return String();
		Ref<PackedScene> ps = loader->load(inst);
		return ps.is_valid() ? meta_in(ps->get_state(), NodePath(".")) : String();
	}
	if (base_state_.is_valid()) return meta_in(base_state_, root->get_path_to(n));
	return String();
}

void SyncDocument::adopt(std::vector<Ref<Resource>> *externals) {
	adopting_ = true;
	base_state_ = Ref<SceneState>();
	if (kind_ == Kind::Scene && is_safe_resource_path(path_) && FileAccess::file_exists(path_)) {
		// A scene that inherits another (its root instances the base).
		Ref<PackedScene> own = ResourceLoader::get_singleton()->load(path_);
		if (own.is_valid() && own->get_state().is_valid()) base_state_ = own->get_state()->get_base_scene_state();
	}
	nodes_.clear();
	node_by_oid_.clear();
	res_.clear();
	res_by_oid_.clear();

	std::vector<Reach> reach;
	if (kind_ == Kind::Scene) {
		std::vector<Visit> visits;
		walk(WalkMode::Adopt, visits);
		std::unordered_map<uint64_t, Uuid> ids;
		for (const Visit &v : visits) ids[v.oid] = v.id;
		for (const Visit &v : visits) {
			NodeEntry e;
			e.id = v.id;
			e.oid = v.oid;
			e.parent = v.parent;
			e.name = v.node->get_name();
			e.cls = v.node->get_class();
			e.instance = v.node == root_node() ? String() : v.node->get_scene_file_path();
			e.own = v.own;
			e.index = v.node->get_index();
			e.child_keys = child_keys_of(v.node, ids);
			nodes_[v.id] = e;
			node_by_oid_[v.oid] = v.id;
		}
		// Properties after all ids exist (node references resolve).
		for (const Visit &v : visits) snapshot_node(nodes_[v.id], v.node, &reach);
	} else if (root_res_.is_valid()) {
		ResEntry e;
		e.id = root_id_;
		e.oid = root_res_->get_instance_id();
		e.res = root_res_;
		res_[e.id] = e;
		res_by_oid_[e.oid] = e.id;
		reach.push_back({ root_res_, root_id_.to_string() });
	}
	std::vector<LocalOp> ignored;
	diff_resources(reach, {}, ignored, externals);
	adopting_ = false;
}

Dictionary SyncDocument::create_payload(Node *n, const NodeEntry &e, EncodeState &st) {
	Dictionary p;
	p["s"] = path_;
	p["p"] = e.parent.is_nil() ? String() : e.parent.to_string();
	p["n"] = String(n->get_name());
	p["c"] = n->get_class();
	String inst = n == root_node() ? String() : n->get_scene_file_path();
	if (!inst.is_empty()) p["inst"] = inst;
	p["props"] = encode_properties(n, st, e.id.to_string(), inst.is_empty());
	PackedStringArray groups = persistent_groups(n);
	if (!groups.is_empty()) p["g"] = encode_value(groups, st, String());
	Array conns = connections_of(n);
	if (!conns.is_empty()) p["cn"] = conns;
	return p;
}

void SyncDocument::announce(std::vector<LocalOp> &out) {
	EncodeState st;
	st.host = this;
	if (kind_ == Kind::Resource) {
		if (root_res_.is_null()) return;
		LocalOp op = make_op("AddResource", root_id_, "@res");
		op.payload["c"] = root_res_->get_class();
		op.payload["props"] = encode_properties(root_res_.ptr(), st, root_id_.to_string(), false);
		out.push_back(op);
		return;
	}
	std::vector<Visit> visits;
	walk(WalkMode::Refresh, visits);
	for (const Visit &v : visits) {
		if (!v.own) continue;
		auto it = nodes_.find(v.id);
		if (it == nodes_.end()) continue;
		LocalOp op = make_op("CreateNode", v.id, "@node");
		op.payload = create_payload(v.node, it->second, st);
		out.push_back(op);
	}
}

void SyncDocument::absorb_node(Node *n, const std::unordered_set<std::string> *only) {
	auto it = node_by_oid_.find(n->get_instance_id());
	if (it == node_by_oid_.end()) return;
	NodeEntry &e = nodes_[it->second];
	bool fresh = false;
	std::vector<StoredProperty> props = cached_properties(n, it->first, e.props, &fresh);
	for (size_t i = 0; i < props.size(); i++) {
		const StringName &name = props[i].name;
		if (only && !only->count(String(name).utf8().get_data())) continue;
		Variant value = n->get(name);
		const Variant *old = find_prop(e.props, name, i);
		if (old && values_equal(value, *old)) continue;
		set_prop(e.props, name, value, i);
	}
	if (fresh && !only) prune_props(e.props, props);
}

// Root changes

bool SyncDocument::rebase_root(Node *now, std::vector<LocalOp> &out) {
	Node *old = root_node();
	if (!now || now == old) return true;
	Uuid meta = Uuid::parse_or_nil(String(now->get_meta(kIdMeta, String())));
	auto it = nodes_.find(meta);
	if (meta.is_nil() || it == nodes_.end()) return false;
	// A reload brings new objects throughout; a root change keeps the others.
	bool known_children = now->get_child_count() == 0;
	for (int i = 0; i < now->get_child_count() && !known_children; i++) {
		known_children = node_by_oid_.count(now->get_child(i)->get_instance_id()) > 0;
	}
	if (!known_children) return false;
	EncodeState st;
	st.host = this;

	if (meta == root_id_) {
		// Change Type of the root: same identity, a new object of another class.
		NodeEntry &e = it->second;
		if (e.cls == now->get_class()) return false;
		LocalOp op = make_op("ChangeNodeType", root_id_, "@type");
		op.payload["c"] = now->get_class();
		op.payload["oc"] = e.cls;
		op.payload["props"] = encode_properties(now, st, root_id_.to_string(), false);
		Array oprops;
		for (const auto &kv : e.props) {
			oprops.push_back(String(kv.first));
			oprops.push_back(encode_value(kv.second, st, root_id_.to_string() + "/prop:" + String(kv.first)));
		}
		op.payload["oprops"] = oprops;
		PackedStringArray groups = persistent_groups(now);
		if (!groups.is_empty()) op.payload["g"] = encode_value(groups, st, String());
		out.push_back(op);
		node_by_oid_.erase(e.oid);
		e.oid = now->get_instance_id();
		e.cls = now->get_class();
		node_by_oid_[e.oid] = root_id_;
		root_oid_ = e.oid;
		snapshot_node(e, now, nullptr);
		return true;
	}

	// Make Scene Root: another node took over; the old root is now its child.
	if (!old || old->get_parent() != now) return false;
	NodeEntry &x = it->second;
	Uuid previous_root = root_id_;
	LocalOp op = make_op("SetSceneRoot", meta, "@root");
	op.payload["or"] = previous_root.to_string();
	op.payload["orp"] = meta.to_string();
	op.payload["ori"] = old->get_index();
	op.payload["op"] = x.parent.to_string();
	op.payload["oi"] = x.index;
	out.push_back(op);

	std::string xkey = key_of(meta);
	auto pit = nodes_.find(x.parent);
	if (pit != nodes_.end()) {
		auto &keys = pit->second.child_keys;
		keys.erase(std::remove(keys.begin(), keys.end(), xkey), keys.end());
	}
	x.child_keys.push_back(key_of(previous_root));
	NodeEntry &r = nodes_[previous_root];
	r.parent = meta;
	r.index = old->get_index();
	x.parent = Uuid();
	x.index = 0;
	root_id_ = meta;
	root_oid_ = now->get_instance_id();
	return true;
}

void SyncDocument::release_from_editor(Node *n) {
	// Like the Scene dock does before deleting or replacing: nothing in the
	// editor may keep pointing at the node (selection, inspector, plugins).
	EditorInterface *ei = EditorInterface::get_singleton();
	if (!ei) return;
	if (EditorSelection *sel = ei->get_selection()) {
		TypedArray<Node> selected = sel->get_selected_nodes();
		for (int64_t i = 0; i < selected.size(); i++) {
			Node *s = Object::cast_to<Node>(selected[i]);
			if (s && (s == n || n->is_ancestor_of(s))) sel->remove_node(s);
		}
	}
	if (EditorInspector *inspector = ei->get_inspector()) {
		Node *edited = Object::cast_to<Node>(inspector->get_edited_object());
		if (edited && (edited == n || n->is_ancestor_of(edited))) ei->inspect_object(nullptr);
	}
}

Node *SyncDocument::find_by_id_meta(const Uuid &id, const String &cls) const {
	Node *root = root_node();
	if (!root) return nullptr;
	String want = id.to_string();
	std::function<Node *(Node *)> search = [&](Node *n) -> Node * {
		if (String(n->get_meta(kIdMeta, String())) == want && (cls.is_empty() || n->get_class() == cls)) return n;
		for (int i = 0; i < n->get_child_count(); i++) {
			if (Node *f = search(n->get_child(i))) return f;
		}
		return nullptr;
	};
	return search(root);
}

bool SyncDocument::apply_change_type(const Uuid &target, const Dictionary &p, ApplyContext &ctx) {
	Node *root = root_node();
	auto it = nodes_.find(target);
	if (!root || it == nodes_.end()) return false;
	String cls = p.get("c", String());
	Node *old = node(target);
	bool attached = old && (old == root || root->is_ancestor_of(old));
	DecodeState st;
	st.host = this;
	st.allow_builtin_scripts = ctx.allow_builtin_scripts;

	Node *result = nullptr;
	if (attached && old->get_class() == cls) {
		result = old; // already that type
	} else if (Node *back = find_by_id_meta(target, cls)) {
		// The editor's own undo put the original object back: follow it.
		result = back;
	}
	if (result) {
		if (result != old) {
			node_by_oid_.erase(it->second.oid);
			it->second.oid = result->get_instance_id();
			it->second.cls = result->get_class();
			node_by_oid_[it->second.oid] = target;
			if (target == root_id_) root_oid_ = it->second.oid;
			ctx.created.insert(it->second.oid);
			ctx.structure_changed = true;
		}
	} else {
		if (!attached) return false;
		bool was_root = old == root;
		if (was_root && old->is_inside_tree()) {
			ctx.errors.push_back(path_ + ": the scene root of an open scene is replaced by reloading it");
			return false;
		}
		ClassDBSingleton *db = ClassDBSingleton::get_singleton();
		if (!db->class_exists(cls) || !db->is_parent_class(cls, "Node") || !db->can_instantiate(cls) ||
				class_is_blocked(cls, ctx.allow_builtin_scripts)) {
			ctx.errors.push_back(String("Unknown node type ") + cls);
			return false;
		}
		result = Object::cast_to<Node>(db->instantiate(cls));
		if (!result) return false;
		release_from_editor(old);
		result->set_name(old->get_name());
		old->replace_by(result, true);
		result->set_meta(kIdMeta, target.to_string());
		if (was_root) root_oid_ = result->get_instance_id();
		node_by_oid_.erase(it->second.oid);
		it->second.oid = result->get_instance_id();
		it->second.cls = cls;
		node_by_oid_[it->second.oid] = target;
		ctx.created.insert(it->second.oid);
		ctx.detached.push_back(old->get_instance_id());
		ctx.structure_changed = true;
	}
	apply_properties(result, p.get("props", Array()), st, &ctx.deferred_refs);
	if (!st.ok) ctx.errors.push_back(String(result->get_name()) + ": " + st.error);
	for (const Ref<Resource> &r : st.touched) ctx.touched_resources.push_back(r);
	if (p.has("g")) {
		DecodeState gs;
		Variant g = decode_value(p["g"], gs);
		if (gs.ok && g.get_type() == Variant::PACKED_STRING_ARRAY) apply_groups(result, g);
	}
	if (p.has("cn") && p["cn"].get_type() == Variant::ARRAY) ctx.deferred_conns.emplace_back(target, Array(p["cn"]));
	ctx.applied++;
	return true;
}

bool SyncDocument::apply_set_root(const Uuid &target, const Dictionary &p, ApplyContext &ctx) {
	Node *root = root_node();
	Node *x = node(target);
	Uuid rid = Uuid::parse_or_nil(p.get("or", String()));
	if (!root || !x || x == root || rid != root_id_ || !root->is_ancestor_of(x)) return false;
	if (root->is_inside_tree()) {
		ctx.errors.push_back(path_ + ": the scene root of an open scene is replaced by reloading it");
		return false;
	}
	Uuid dest_id = Uuid::parse_or_nil(p.get("orp", String()));
	Node *dest = dest_id.is_nil() ? x : node(dest_id);
	if (!dest || !(dest == x || x->is_ancestor_of(dest))) return false;
	int64_t at = int64_t(double(p.get("ori", -1)));

	release_from_editor(x);
	x->get_parent()->remove_child(x);
	dest->add_child(root, true);
	if (at >= 0) dest->move_child(root, int32_t(std::min<int64_t>(at, dest->get_child_count() - 1)));
	// Everything the old root owned now belongs to the new one.
	std::vector<Node *> owned;
	std::function<void(Node *)> collect = [&](Node *n) {
		if (n != x && n->get_owner() == root) owned.push_back(n);
		for (int i = 0; i < n->get_child_count(); i++) collect(n->get_child(i));
	};
	collect(x);
	x->set_owner(nullptr);
	root->set_owner(x);
	for (Node *n : owned) {
		if (n != root) n->set_owner(x);
	}
	x->set_scene_file_path(root->get_scene_file_path());
	root->set_scene_file_path(String());

	NodeEntry &xe = nodes_[target];
	NodeEntry &re = nodes_[rid];
	xe.parent = Uuid();
	re.parent = node_id(dest);
	root_id_ = target;
	root_oid_ = x->get_instance_id();
	ctx.structure_changed = true;
	ctx.applied++;
	return true;
}

void SyncDocument::rebind_objects() {
	Node *root = root_node();
	if (!root) return;
	node_by_oid_.clear();
	auto bind = [&](Node *n, const Uuid &id) {
		auto it = nodes_.find(id);
		if (it == nodes_.end()) return false;
		it->second.oid = n->get_instance_id();
		node_by_oid_[it->second.oid] = id;
		return true;
	};
	bind(root, root_id_);
	std::function<void(Node *)> recurse = [&](Node *parent) {
		for (int i = 0; i < parent->get_child_count(); i++) {
			Node *c = parent->get_child(i);
			Node *owner = c->get_owner();
			Uuid id;
			if (owner == root) {
				id = Uuid::parse_or_nil(String(c->get_meta(kIdMeta, String())));
			} else if (owner && node_by_oid_.count(owner->get_instance_id()) && root->is_editable_instance(owner)) {
				id = Uuid::from_name(node_by_oid_[owner->get_instance_id()], "inst:" + String(owner->get_path_to(c)));
			} else {
				continue;
			}
			if (!id.is_nil()) bind(c, id);
			recurse(c);
		}
	};
	recurse(root);
}

// Undo support

Dictionary SyncDocument::prior_entry(const Uuid &id, EncodeState &st, bool with_subtree) {
	Dictionary d;
	auto it = nodes_.find(id);
	if (it == nodes_.end()) return d;
	const NodeEntry &e = it->second;
	d["id"] = id.to_string();
	d["p"] = e.parent.is_nil() ? String() : e.parent.to_string();
	d["n"] = e.name;
	d["c"] = e.cls;
	d["i"] = e.index;
	if (!e.instance.is_empty()) d["inst"] = e.instance;
	Array props;
	String base = id.to_string() + "/prop:";
	for (const auto &kv : e.props) {
		props.push_back(String(kv.first));
		props.push_back(encode_value(kv.second, st, base + String(kv.first)));
	}
	d["props"] = props;
	if (!e.groups.is_empty()) d["g"] = encode_value(e.groups, st, String());
	if (!e.conns.is_empty()) d["cn"] = e.conns;
	if (with_subtree) {
		// Own descendants in pre-order (parents first, siblings in order).
		Array sub;
		std::function<void(const NodeEntry &)> walk_children = [&](const NodeEntry &pe) {
			for (const std::string &k : pe.child_keys) {
				if (!is_uuid_key(k)) continue;
				Uuid cid = Uuid::parse_or_nil(String::utf8(k.c_str()));
				auto ct = nodes_.find(cid);
				if (ct == nodes_.end() || !ct->second.own) continue;
				sub.push_back(prior_entry(cid, st, false));
				walk_children(ct->second);
			}
		};
		walk_children(e);
		if (!sub.is_empty()) d["sub"] = sub;
	}
	return d;
}

Dictionary SyncDocument::create_from_prior(const Dictionary &prior) const {
	Dictionary c;
	c["s"] = path_;
	c["p"] = prior.get("p", String());
	c["n"] = prior.get("n", String());
	c["c"] = prior.get("c", String());
	if (prior.has("inst")) c["inst"] = prior["inst"];
	c["props"] = prior.get("props", Array());
	if (prior.has("g")) c["g"] = prior["g"];
	if (prior.has("cn")) c["cn"] = prior["cn"];
	return c;
}

Variant SyncDocument::shadow_value(const std::string &type, const Uuid &target, const String &key, EncodeState &st) {
	String hint = target.to_string() + "/prop:" + key;
	if (type == "ChangeProperty") {
		auto it = nodes_.find(target);
		if (it == nodes_.end()) return Variant();
		if (key == "@groups") return encode_value(it->second.groups, st, String());
		if (key == "@conns") return it->second.conns;
		const Variant *v = find_prop(it->second.props, key, 0);
		return v ? encode_value(*v, st, hint) : Variant();
	}
	auto it = res_.find(target);
	if (it == res_.end()) return Variant();
	if (key == kStateKey) return encode_prop_list(it->second.props, st, target.to_string());
	const Variant *v = find_prop(it->second.props, key, 0);
	return v ? encode_value(*v, st, hint) : Variant();
}

std::vector<LocalOp> SyncDocument::inverse_of(const std::string &type, const Uuid &target, const Dictionary &p) {
	std::vector<LocalOp> out;
	EncodeState st;
	st.host = this;
	if (type == "ChangeProperty" || type == "ChangeResourceProperty") {
		if (!p.has("o")) return out; // the property had no earlier value
		String key = p.get("k", String());
		LocalOp inv = make_op(type.c_str(), target, key);
		inv.payload["k"] = key;
		inv.payload["v"] = p["o"];
		Variant current = shadow_value(type, target, key, st);
		inv.payload["o"] = current.get_type() != Variant::NIL ? current : p.get("v", Variant());
		out.push_back(inv);
	} else if (type == "CreateNode") {
		if (!nodes_.count(target)) return out; // already gone
		LocalOp inv = make_op("DeleteNode", target, "@node");
		inv.payload["prior"] = prior_entry(target, st, true);
		out.push_back(inv);
	} else if (type == "DeleteNode") {
		Dictionary prior = p.get("prior", Dictionary());
		if (prior.is_empty() || String(prior.get("p", String())).is_empty()) return out;
		LocalOp top = make_op("CreateNode", target, "@node");
		top.payload = create_from_prior(prior);
		out.push_back(top);
		Array sub = prior.get("sub", Array());
		for (int64_t i = 0; i < sub.size(); i++) {
			if (sub[i].get_type() != Variant::DICTIONARY) continue;
			Dictionary d = sub[i];
			LocalOp c = make_op("CreateNode", Uuid::parse_or_nil(d.get("id", String())), "@node");
			if (c.target.is_nil()) continue;
			c.payload = create_from_prior(d);
			out.push_back(c);
		}
		LocalOp r = make_op("ReorderNode", target, "@index");
		r.payload["i"] = prior.get("i", 0);
		r.payload["oi"] = -1;
		out.push_back(r);
	} else if (type == "MoveNode") {
		String back = p.get("op", String());
		if (back.is_empty()) return out;
		LocalOp m = make_op("MoveNode", target, "@parent");
		m.payload["p"] = back;
		m.payload["op"] = p.get("p", String());
		auto it = nodes_.find(target);
		m.payload["oi"] = it != nodes_.end() ? it->second.index : -1;
		out.push_back(m);
		LocalOp r = make_op("ReorderNode", target, "@index");
		r.payload["i"] = p.get("oi", 0);
		r.payload["oi"] = -1;
		out.push_back(r);
	} else if (type == "RenameNode") {
		String back = p.get("on", String());
		if (back.is_empty()) return out;
		LocalOp r = make_op("RenameNode", target, "@name");
		r.payload["n"] = back;
		r.payload["on"] = p.get("n", String());
		out.push_back(r);
	} else if (type == "ChangeNodeType") {
		String back = p.get("oc", String());
		if (back.is_empty() || !nodes_.count(target)) return out;
		LocalOp t = make_op("ChangeNodeType", target, "@type");
		t.payload["c"] = back;
		t.payload["oc"] = p.get("c", String());
		t.payload["props"] = p.get("oprops", Array());
		Array current;
		for (const auto &kv : nodes_[target].props) {
			current.push_back(String(kv.first));
			current.push_back(encode_value(kv.second, st, target.to_string() + "/prop:" + String(kv.first)));
		}
		t.payload["oprops"] = current;
		out.push_back(t);
	} else if (type == "SetSceneRoot") {
		String back = p.get("or", String());
		if (back.is_empty()) return out;
		LocalOp r = make_op("SetSceneRoot", Uuid::parse_or_nil(back), "@root");
		if (r.target.is_nil()) return out;
		r.payload["or"] = target.to_string();
		r.payload["orp"] = p.get("op", String());
		r.payload["ori"] = p.get("oi", -1);
		r.payload["op"] = p.get("orp", String());
		r.payload["oi"] = p.get("ori", -1);
		out.push_back(r);
	} else if (type == "ReorderNode") {
		int64_t back = int64_t(double(p.get("oi", -1)));
		if (back < 0) return out;
		LocalOp r = make_op("ReorderNode", target, "@index");
		r.payload["i"] = back;
		r.payload["oi"] = p.get("i", 0);
		out.push_back(r);
	}
	return out;
}

void SyncDocument::absorb_prop(const Uuid &target, const String &key) {
	if (Node *n = node(target)) {
		NodeEntry &e = nodes_[target];
		if (key == "@groups") {
			e.groups = persistent_groups(n);
		} else if (key == "@conns") {
			e.conns = connections_of(n);
		} else {
			set_prop(e.props, key, n->get(key), 0);
		}
		return;
	}
	auto it = res_.find(target);
	if (it == res_.end() || it->second.res.is_null()) return;
	if (key == kStateKey) {
		it->second.props = read_props(it->second.res.ptr());
	} else {
		set_prop(it->second.props, key, it->second.res->get(key), 0);
	}
}

// Diff

void SyncDocument::diff(std::vector<LocalOp> &out, std::vector<Ref<Resource>> *externals,
		const PropFilter *filter) {
	EncodeState st;
	st.host = this;
	// Prior values are encoded in full (embedded resources included) so the
	// server can undo exactly.
	EncodeState prior_st;
	prior_st.host = this;

	if (kind_ == Kind::Resource) {
		if (root_res_.is_null()) return;
		std::vector<Reach> reach{ { root_res_, root_id_.to_string() } };
		diff_resources(reach, {}, out, externals, filter);
		return;
	}

	Node *root = root_node();
	if (!root) return;

	std::vector<Visit> visits;
	walk(WalkMode::Diff, visits);

	std::unordered_map<uint64_t, Uuid> ids;
	std::unordered_map<Uuid, const Visit *, UuidHash> cur;
	for (const Visit &v : visits) {
		ids[v.oid] = v.id;
		cur[v.id] = &v;
	}
	if (!visits.empty() && !visits[0].fresh) {
		auto it = nodes_.find(root_id_);
		if (it != nodes_.end() && it->second.cls != root->get_class()) {
			UtilityFunctions::push_warning("YHDE: changing the scene root's type is not synchronized; peers keep ",
					it->second.cls);
		}
	}

	std::vector<Reach> reach;

	// 1. Created nodes (pre-order, parents first). Internal children of new
	//    instances come with the instance and are only snapshotted.
	std::vector<Uuid> created;
	for (const Visit &v : visits) {
		if (!v.fresh) continue;
		if (v.retyped) {
			NodeEntry &e = nodes_[v.id];
			LocalOp op = make_op("ChangeNodeType", v.id, "@type");
			op.payload["c"] = v.node->get_class();
			op.payload["oc"] = e.cls;
			op.payload["props"] = encode_properties(v.node, st, v.id.to_string(), false);
			Array oprops;
			for (const auto &kv : e.props) {
				oprops.push_back(String(kv.first));
				oprops.push_back(encode_value(kv.second, prior_st, v.id.to_string() + "/prop:" + String(kv.first)));
			}
			op.payload["oprops"] = oprops;
			PackedStringArray groups = persistent_groups(v.node);
			if (!groups.is_empty()) op.payload["g"] = encode_value(groups, st, String());
			Array conns = connections_of(v.node);
			if (!conns.is_empty()) op.payload["cn"] = conns;
			out.push_back(op);
			node_by_oid_.erase(e.oid);
			e.oid = v.oid;
			e.cls = v.node->get_class();
			node_by_oid_[v.oid] = v.id;
			continue;
		}
		NodeEntry e;
		e.id = v.id;
		e.oid = v.oid;
		e.parent = v.parent;
		e.name = v.node->get_name();
		e.cls = v.node->get_class();
		e.instance = v.node == root ? String() : v.node->get_scene_file_path();
		e.own = v.own;
		e.index = v.node->get_index();
		// Drop a stale entry for this object (e.g. its identity was reassigned).
		auto stale = node_by_oid_.find(v.oid);
		if (stale != node_by_oid_.end() && stale->second != v.id) {
			nodes_.erase(stale->second);
		}
		nodes_[v.id] = e;
		node_by_oid_[v.oid] = v.id;
		if (v.own && v.node != root) {
			LocalOp op = make_op("CreateNode", v.id, "@node");
			op.payload = create_payload(v.node, nodes_[v.id], st);
			out.push_back(op);
			created.push_back(v.id);
		}
	}
	std::unordered_set<Uuid, UuidHash> created_set(created.begin(), created.end());

	// 2. Moved nodes (reparented; appended at the end, order fixed in step 4).
	std::vector<Uuid> moved;
	for (const Visit &v : visits) {
		if (v.fresh || !v.own || v.node == root) continue;
		NodeEntry &e = nodes_[v.id];
		if (e.parent == v.parent) continue;
		LocalOp op = make_op("MoveNode", v.id, "@parent");
		op.payload["p"] = v.parent.to_string();
		op.payload["op"] = e.parent.to_string();
		op.payload["oi"] = e.index;
		out.push_back(op);
		moved.push_back(v.id);
	}

	// 3. Deleted nodes: only the top-most of each removed subtree.
	std::vector<Uuid> gone;
	for (const auto &kv : nodes_) {
		if (!cur.count(kv.first)) gone.push_back(kv.first);
	}
	std::unordered_set<Uuid, UuidHash> gone_set(gone.begin(), gone.end());
	for (const Uuid &id : gone) {
		const NodeEntry &e = nodes_[id];
		if (!e.own || gone_set.count(e.parent)) continue;
		LocalOp op = make_op("DeleteNode", id, "@node");
		op.payload["prior"] = prior_entry(id, prior_st, true);
		out.push_back(op);
	}
	for (const Uuid &id : gone) {
		auto it = nodes_.find(id);
		if (it == nodes_.end()) continue;
		if (it->second.own) retired_.insert(id);
		node_by_oid_.erase(it->second.oid);
		nodes_.erase(it);
	}

	// 4. Sibling order. Simulate what peers will have after steps 1-3 and emit
	//    the fewest ReorderNode ops that produce the local order.
	std::unordered_set<Uuid, UuidHash> moved_set(moved.begin(), moved.end());
	for (const Visit &pv : visits) {
		std::vector<std::string> target = child_keys_of(pv.node, ids);
		NodeEntry &pe = nodes_[pv.id];
		bool parent_fresh = pv.fresh;
		if (!parent_fresh && target == pe.child_keys) continue;

		std::vector<std::string> sim;
		std::vector<std::string> appended;
		for (const Uuid &c : created) {
			const Visit *cv = cur[c];
			if (cv->parent == pv.id) appended.push_back(key_of(c));
		}
		for (const Uuid &m : moved) {
			const Visit *mv = cur[m];
			if (mv->parent == pv.id) appended.push_back(key_of(m));
		}
		std::unordered_set<std::string> appended_set(appended.begin(), appended.end());
		if (parent_fresh) {
			// A new instance brings its internal children first.
			for (const std::string &k : target) {
				if (!appended_set.count(k)) sim.push_back(k);
			}
		} else {
			std::unordered_set<std::string> target_set(target.begin(), target.end());
			for (const std::string &k : pe.child_keys) {
				if (is_uuid_key(k)) {
					Uuid u = Uuid::parse_or_nil(String::utf8(k.c_str()));
					auto cit = cur.find(u);
					if (cit == cur.end() || cit->second->parent != pv.id || moved_set.count(u) || created_set.count(u)) {
						continue;
					}
				} else if (!target_set.count(k)) {
					continue;
				}
				sim.push_back(k);
			}
		}
		for (const std::string &k : appended) sim.push_back(k);

		auto movable = [&](const std::string &k) {
			if (!is_uuid_key(k)) return false;
			auto it = nodes_.find(Uuid::parse_or_nil(String::utf8(k.c_str())));
			return it != nodes_.end() && it->second.own;
		};
		for (const auto &mv : plan_reorder(sim, target, movable)) {
			Uuid id = Uuid::parse_or_nil(String::utf8(mv.first.c_str()));
			LocalOp op = make_op("ReorderNode", id, "@index");
			op.payload["i"] = mv.second;
			auto it = nodes_.find(id);
			op.payload["oi"] = (it != nodes_.end() && !created_set.count(id)) ? it->second.index : -1;
			out.push_back(op);
		}
	}

	// 5. Renames.
	for (const Visit &v : visits) {
		if (v.fresh || !v.own) continue;
		NodeEntry &e = nodes_[v.id];
		String name = v.node->get_name();
		if (name == e.name) continue;
		LocalOp op = make_op("RenameNode", v.id, "@name");
		op.payload["n"] = name;
		op.payload["on"] = e.name;
		out.push_back(op);
	}

	// 6. Properties, groups and signal connections of existing nodes.
	std::unordered_map<uint64_t, Array> outgoing = outgoing_connections(visits);
	for (const Visit &v : visits) {
		NodeEntry &e = nodes_[v.id];
		if (v.fresh) {
			snapshot_node(e, v.node, &reach);
			continue;
		}
		String base = e.id.to_string() + "/prop:";
		settle_tiles(v.node);
		bool fresh_list = has_silent_property_list(v.node);
		std::vector<StoredProperty> props =
				fresh_list ? stored_properties(v.node) : cached_properties(v.node, v.oid, e.props, &fresh_list);
		bool changed = false;
		auto check = [&](const StringName &name, size_t i) {
			Variant value = v.node->get(name);
			String hint = base + String(name);
			collect_reach(value, hint, reach);
			const Variant *old = find_prop(e.props, name, i);
			if (old && values_equal(value, *old)) return;
			changed = true;
			Verdict verdict = filter ? (*filter)(v.node, name) : Verdict::Emit;
			if (verdict == Verdict::Keep) return;
			if (verdict == Verdict::Absorb) {
				set_prop(e.props, name, value, i);
				return;
			}
			LocalOp op = make_op("ChangeProperty", e.id, String(name));
			op.payload["k"] = String(name);
			op.payload["v"] = encode_value(value, st, hint);
			if (old) op.payload["o"] = encode_value(*old, prior_st, hint); // absent: it had no earlier value
			out.push_back(op);
			set_prop(e.props, name, value, i);
		};
		for (size_t i = 0; i < props.size(); i++) check(props[i].name, i);
		if (changed && !fresh_list) {
			// A change can reveal properties at once (a toggle showing its
			// settings) while the engine announces the new list only later:
			// read the whole list of every object that changed.
			std::vector<StoredProperty> full = stored_properties(v.node);
			for (size_t i = 0; i < full.size(); i++) {
				if (!has_prop(props, full[i].name)) check(full[i].name, i);
			}
			props = std::move(full);
			fresh_list = true;
		}
		if (fresh_list) {
			// Gone and no longer readable (a removed TileMap layer): send null
			// so peers drop it too. Hidden-but-readable ones are just pruned.
			std::vector<StringName> gone;
			for (const auto &sp : e.props) {
				if (sp.second.get_type() != Variant::NIL && !has_prop(props, sp.first) &&
						v.node->get(sp.first).get_type() == Variant::NIL) {
					gone.push_back(sp.first);
				}
			}
			for (const StringName &name : gone) check(name, 0);
			prune_props(e.props, props);
		}
		PackedStringArray groups = persistent_groups(v.node);
		if (groups != e.groups) {
			LocalOp op = make_op("ChangeProperty", e.id, "@groups");
			op.payload["k"] = "@groups";
			op.payload["v"] = encode_value(groups, st, String());
			op.payload["o"] = encode_value(e.groups, st, String());
			out.push_back(op);
			e.groups = groups;
		}
		auto oc = outgoing.find(v.oid);
		Array conns = oc != outgoing.end() ? oc->second : Array();
		if (!values_equal(conns, e.conns)) {
			LocalOp op = make_op("ChangeProperty", e.id, "@conns");
			op.payload["k"] = "@conns";
			op.payload["v"] = conns;
			op.payload["o"] = e.conns;
			out.push_back(op);
			e.conns = conns;
		}
	}

	// 7. Embedded resources (materials, shapes, meshes, curves, …).
	std::unordered_set<uint64_t> fully_encoded;
	for (const std::string &sid : st.emitted) {
		auto it = res_.find(Uuid::parse_or_nil(String::utf8(sid.c_str())));
		if (it != res_.end()) fully_encoded.insert(it->second.oid);
	}
	diff_resources(reach, fully_encoded, out, externals, filter);

	// 8. Structural shadow.
	for (const Visit &v : visits) {
		NodeEntry &e = nodes_[v.id];
		e.oid = v.oid;
		e.parent = v.parent;
		e.name = v.node->get_name();
		e.cls = v.node->get_class();
		e.own = v.own;
		e.index = v.node->get_index();
		e.child_keys = child_keys_of(v.node, ids);
		node_by_oid_[v.oid] = v.id;
	}
}

void SyncDocument::diff_resources(std::vector<Reach> &reach, const std::unordered_set<uint64_t> &fully_encoded,
		std::vector<LocalOp> &out, std::vector<Ref<Resource>> *externals, const PropFilter *filter) {
	EncodeState st;
	st.host = this;
	EncodeState prior_st;
	prior_st.host = this;

	std::unordered_set<uint64_t> visited;
	for (size_t qi = 0; qi < reach.size(); qi++) {
		Ref<Resource> res = reach[qi].res;
		String hint = reach[qi].hint;
		if (res.is_null()) continue;
		uint64_t oid = res->get_instance_id();
		if (!visited.insert(oid).second) continue;

		bool tracked = (kind_ == Kind::Resource && res == root_res_) || codec_is_embedded(res);
		if (!tracked) {
			if (externals && is_external_file_resource(res)) externals->push_back(res);
			continue;
		}
		auto it = res_by_oid_.find(oid);
		if (it == res_by_oid_.end()) {
			codec_embedded_id(res, hint);
			it = res_by_oid_.find(oid);
		}
		ResEntry &e = res_[it->second];
		if (e.pending || fully_encoded.count(oid)) {
			snapshot_resource(e, &reach);
			continue;
		}
		String base = e.id.to_string() + "/prop:";
		bool fresh_list = false;
		std::vector<StoredProperty> props = cached_properties(res.ptr(), oid, e.props, &fresh_list);
		bool changed = false;
		size_t first_op = out.size();
		PropList before; // the shadow before this scan, kept once something changes
		bool saved = false;
		auto check = [&](const StringName &name, size_t i) {
			Variant value = res->get(name);
			String h = base + String(name);
			collect_reach(value, h, reach);
			const Variant *old = find_prop(e.props, name, i);
			if (old && values_equal(value, *old)) return;
			changed = true;
			if (!saved) {
				before = e.props;
				saved = true;
			}
			Verdict verdict = filter ? (*filter)(res.ptr(), name) : Verdict::Emit;
			if (verdict == Verdict::Keep) return;
			if (verdict == Verdict::Absorb) {
				set_prop(e.props, name, value, i);
				return;
			}
			LocalOp op = make_op("ChangeResourceProperty", e.id, String(name));
			op.payload["k"] = String(name);
			op.payload["v"] = encode_value(value, st, h);
			if (old) op.payload["o"] = encode_value(*old, prior_st, h);
			out.push_back(op);
			set_prop(e.props, name, value, i);
		};
		for (size_t i = 0; i < props.size(); i++) check(props[i].name, i);
		if (changed && !fresh_list) {
			std::vector<StoredProperty> full = stored_properties(res.ptr());
			for (size_t i = 0; i < full.size(); i++) {
				if (!has_prop(props, full[i].name)) check(full[i].name, i);
			}
			props = std::move(full);
			fresh_list = true;
		}
		// Entries that are gone (a deleted tile, a removed TileSet layer) cannot
		// be sent as single properties: setting one to null removes nothing.
		// Send the whole state instead. A property that still reads back is
		// only hidden (a toggle), not removed.
		const PropList &prior = saved ? before : e.props;
		bool removed = false;
		if (fresh_list) {
			for (const auto &bp : prior) {
				if (bp.second.get_type() != Variant::NIL && !has_prop(props, bp.first) &&
						res->get(bp.first).get_type() == Variant::NIL) {
					removed = true;
					break;
				}
			}
		}
		if (removed) {
			static const StringName state_name(kStateKey);
			Verdict verdict = filter ? (*filter)(res.ptr(), state_name) : Verdict::Emit;
			if (verdict == Verdict::Keep) {
				if (saved) e.props = std::move(before); // not now: the next scan sees the removal again
				continue;
			}
			out.erase(out.begin() + std::ptrdiff_t(first_op), out.end());
			if (verdict == Verdict::Emit) {
				LocalOp op = make_op("ChangeResourceProperty", e.id, kStateKey);
				op.payload["k"] = String(kStateKey);
				op.payload["v"] = encode_properties(res.ptr(), st, e.id.to_string(), false);
				op.payload["o"] = encode_prop_list(prior, prior_st, e.id.to_string());
				out.push_back(op);
			}
		}
		if (fresh_list) prune_props(e.props, props);
	}

	// Newly registered resources discovered while encoding need a baseline.
	for (auto &kv : res_) {
		if (kv.second.pending && visited.count(kv.second.oid)) snapshot_resource(kv.second, nullptr);
	}

	// Forget resources nothing references any more.
	for (auto it = res_.begin(); it != res_.end();) {
		bool keep = visited.count(it->second.oid) || (kind_ == Kind::Resource && it->first == root_id_);
		if (!keep && it->second.pending) keep = true; // registered this pass, not yet reachable
		if (keep) {
			++it;
		} else {
			res_by_oid_.erase(it->second.oid);
			it = res_.erase(it);
		}
	}
}

// Remote application

void SyncDocument::touch(Node *n, ApplyContext &ctx) {
	uint64_t oid = n->get_instance_id();
	if (ctx.before.count(oid) || ctx.created.count(oid)) return;
	ctx.before[oid] = read_props(n);
}

void SyncDocument::forget_subtree(Node *n) {
	auto it = node_by_oid_.find(n->get_instance_id());
	if (it != node_by_oid_.end()) {
		retired_.insert(it->second);
		nodes_.erase(it->second);
		node_by_oid_.erase(it);
	}
	int count = n->get_child_count();
	for (int i = 0; i < count; i++) forget_subtree(n->get_child(i));
}

bool SyncDocument::apply(const std::string &type, const Uuid &target, const Dictionary &p, ApplyContext &ctx) {
	if (type == "ChangeResourceProperty") return apply_resource_property(target, p, ctx);
	if (type == "AddResource") {
		if (kind_ != Kind::Resource || root_res_.is_null() || p.get("props", Variant()).get_type() != Variant::ARRAY) {
			return false;
		}
		DecodeState st;
		st.host = this;
		st.allow_builtin_scripts = ctx.allow_builtin_scripts;
		apply_properties(root_res_.ptr(), p["props"], st, nullptr);
		if (!st.ok) ctx.errors.push_back(st.error);
		ctx.touched_resources.push_back(root_res_);
		for (const Ref<Resource> &r : st.touched) ctx.touched_resources.push_back(r);
		ctx.applied++;
		return true;
	}
	if (kind_ != Kind::Scene) return false;
	if (type == "CreateNode") return apply_create(target, p, ctx);
	if (type == "ChangeProperty") return apply_property(target, p, ctx);
	if (type == "ChangeNodeType") return apply_change_type(target, p, ctx);
	if (type == "SetSceneRoot") return apply_set_root(target, p, ctx);

	Node *n = node(target);
	if (!n) return false; // already gone, or never synchronized here: nothing to do
	Node *root = root_node();

	if (type == "DeleteNode") {
		if (n == root) return false;
		release_from_editor(n);
		Node *parent = n->get_parent();
		forget_subtree(n);
		if (parent) parent->remove_child(n);
		ctx.detached.push_back(n->get_instance_id());
		ctx.structure_changed = true;
		ctx.applied++;
		return true;
	}
	if (type == "MoveNode") {
		Node *parent = node(Uuid::parse_or_nil(p.get("p", String())));
		if (!parent || n == root || parent == n || n->is_ancestor_of(parent)) return false;
		touch(n, ctx);
		append_to(n, parent);
		ctx.structure_changed = true;
		ctx.applied++;
		return true;
	}
	if (type == "RenameNode") {
		String name = p.get("n", String());
		if (name.is_empty()) return false;
		n->set_name(name);
		ctx.desired_names.emplace_back(target, name);
		ctx.structure_changed = true;
		ctx.applied++;
		return true;
	}
	if (type == "ReorderNode") {
		Node *parent = n->get_parent();
		if (!parent) return false;
		int64_t index = p.get("i", 0);
		index = std::max<int64_t>(0, std::min<int64_t>(index, parent->get_child_count() - 1));
		parent->move_child(n, int32_t(index));
		ctx.structure_changed = true;
		ctx.applied++;
		return true;
	}
	return false;
}

void SyncDocument::append_to(Node *n, Node *parent) {
	Node *root = root_node();
	if (n->get_parent() != parent) {
		n->reparent(parent, false);
		if (root && n->get_owner() != root) n->set_owner(root);
	} else {
		parent->move_child(n, parent->get_child_count() - 1);
	}
}

bool SyncDocument::apply_position(const std::string &type, const Uuid &target, const Dictionary &p, ApplyContext &ctx) {
	if (kind_ != Kind::Scene) return false;
	Node *n = node(target);
	Node *root = root_node();
	if (!n || !root || n == root) return false;
	if (type == "ReorderNode") return apply(type, target, p, ctx);
	Node *parent = node(Uuid::parse_or_nil(p.get("p", String())));
	if (!parent || parent == n || n->is_ancestor_of(parent)) return false;
	append_to(n, parent);
	ctx.structure_changed = true;
	return true;
}

bool SyncDocument::apply_create(const Uuid &target, const Dictionary &p, ApplyContext &ctx) {
	Node *root = root_node();
	if (!root) return false;
	Array props = p.get("props", Array());
	DecodeState st;
	st.host = this;
	st.allow_builtin_scripts = ctx.allow_builtin_scripts;

	Node *n = node(target);
	String parent_text = p.get("p", String());
	Node *parent = parent_text.is_empty() ? nullptr : node(Uuid::parse_or_nil(parent_text));

	if (n) {
		// Replay of a node we already have: converge its state and, like
		// every create, append it to its parent at this point of the log.
		touch(n, ctx);
		if (parent && n != root && !n->is_ancestor_of(parent)) {
			append_to(n, parent);
			ctx.structure_changed = true;
		}
	} else {
		if (parent_text.is_empty()) {
			ctx.errors.push_back(path_ + ": already has a different root node");
			return false;
		}
		if (!parent) {
			ctx.errors.push_back(path_ + ": parent of a new node is missing");
			return false;
		}
		// The editor's own undo may have put this very node back already
		// (undo of a delete keeps the node object): adopt it instead of
		// creating a twin.
		for (int i = 0; i < parent->get_child_count(); i++) {
			Node *c = parent->get_child(i);
			if (String(c->get_meta(kIdMeta, String())) != target.to_string() || node_by_oid_.count(c->get_instance_id())) continue;
			NodeEntry e;
			e.id = target;
			e.oid = c->get_instance_id();
			e.parent = node_id(parent);
			e.name = c->get_name();
			e.cls = c->get_class();
			e.instance = c->get_scene_file_path();
			e.own = true;
			e.index = c->get_index();
			nodes_[target] = e;
			node_by_oid_[e.oid] = target;
			retired_.erase(target);
			ctx.created.insert(e.oid);
			ctx.structure_changed = true;
			append_to(c, parent);
			// Leaving the tree cleared its owner; it belongs to the scene again.
			if (root && c->get_owner() != root) c->set_owner(root);
			n = c;
			break;
		}
	}
	if (!n) {
		String inst = p.get("inst", String());
		String cls = p.get("c", String());
		if (!inst.is_empty()) {
			if (!is_safe_resource_path(inst) || !ResourceLoader::get_singleton()->exists(inst)) {
				ctx.errors.push_back(String("Missing scene ") + inst);
				return false;
			}
			Ref<PackedScene> scene = ResourceLoader::get_singleton()->load(inst);
			if (scene.is_null()) {
				ctx.errors.push_back(String("Could not load ") + inst);
				return false;
			}
			n = scene->instantiate(PackedScene::GEN_EDIT_STATE_INSTANCE);
		} else {
			ClassDBSingleton *db = ClassDBSingleton::get_singleton();
			if (!db->class_exists(cls) || !db->is_parent_class(cls, "Node") || !db->can_instantiate(cls) ||
					class_is_blocked(cls, ctx.allow_builtin_scripts)) {
				ctx.errors.push_back(String("Unknown node type ") + cls);
				return false;
			}
			Object *o = db->instantiate(cls);
			n = Object::cast_to<Node>(o);
		}
		if (!n) {
			ctx.errors.push_back(String("Could not create ") + cls);
			return false;
		}
		String name = p.get("n", String());
		if (!name.is_empty()) n->set_name(name);
		n->set_meta(kIdMeta, target.to_string());
		parent->add_child(n, true);
		n->set_owner(root);

		NodeEntry e;
		e.id = target;
		e.oid = n->get_instance_id();
		e.parent = node_id(parent);
		e.name = n->get_name();
		e.cls = n->get_class();
		e.instance = inst;
		e.own = true;
		e.index = n->get_index();
		nodes_[target] = e;
		node_by_oid_[e.oid] = target;
		retired_.erase(target);
		ctx.created.insert(e.oid);
		ctx.structure_changed = true;
		if (!name.is_empty()) ctx.desired_names.emplace_back(target, name);
	}

	apply_properties(n, props, st, &ctx.deferred_refs);
	if (!st.ok) ctx.errors.push_back(String(n->get_name()) + ": " + st.error);
	for (const Ref<Resource> &r : st.touched) ctx.touched_resources.push_back(r);

	if (p.has("g")) {
		DecodeState gs;
		Variant g = decode_value(p["g"], gs);
		if (gs.ok && g.get_type() == Variant::PACKED_STRING_ARRAY) apply_groups(n, g);
	}
	if (p.has("cn") && p["cn"].get_type() == Variant::ARRAY) ctx.deferred_conns.emplace_back(target, Array(p["cn"]));
	ctx.applied++;
	return true;
}

bool SyncDocument::apply_property(const Uuid &target, const Dictionary &p, ApplyContext &ctx) {
	Node *n = node(target);
	if (!n) return false;
	String key = p.get("k", String());
	if (key.is_empty()) return false;

	if (key == "@groups") {
		DecodeState gs;
		Variant g = decode_value(p.get("v", Variant()), gs);
		if (!gs.ok || g.get_type() != Variant::PACKED_STRING_ARRAY) return false;
		touch(n, ctx);
		apply_groups(n, g);
		ctx.applied++;
		return true;
	}
	if (key == "@conns") {
		Variant v = p.get("v", Variant());
		if (v.get_type() != Variant::ARRAY) return false;
		touch(n, ctx);
		ctx.deferred_conns.emplace_back(target, Array(v));
		ctx.applied++;
		return true;
	}

	DecodeState st;
	st.host = this;
	st.allow_builtin_scripts = ctx.allow_builtin_scripts;
	Variant value = decode_value(p.get("v", Variant()), st);
	for (const Ref<Resource> &r : st.touched) ctx.touched_resources.push_back(r);
	touch(n, ctx);
	if (!st.ok) {
		if (st.missing_node) {
			Array entry;
			entry.push_back(int64_t(n->get_instance_id()));
			entry.push_back(key);
			entry.push_back(p.get("v", Variant()));
			ctx.deferred_refs.push_back(entry);
			ctx.applied++;
			return true;
		}
		ctx.errors.push_back(String(n->get_name()) + "." + key + ": " + st.error);
		return false;
	}
	TileMap *map = Object::cast_to<TileMap>(n);
	if (map && value.get_type() == Variant::NIL && key.begins_with("layer_")) {
		// layer_N/* gone: the sender has N layers left (layers are positional).
		String index = key.get_slice("/", 0).trim_prefix("layer_");
		if (index.is_valid_int()) {
			int keep = int(index.to_int());
			while (keep >= 0 && map->get_layers_count() > keep) map->remove_layer(map->get_layers_count() - 1);
			ctx.applied++;
			return true;
		}
	}
	if (map && value.get_type() == Variant::STRING && key.begins_with("layer_") && key.ends_with("/name")) {
		// Layer names are child node names: when layers shift, the name may
		// still be held by the layer that gets renamed next. Move that one
		// aside (its own op follows) instead of letting the engine pick "Layer3".
		int index = int(key.get_slice("/", 0).trim_prefix("layer_").to_int());
		String wanted = value;
		for (int i = 0; i < map->get_layers_count(); i++) {
			if (i != index && map->get_layer_name(i) == wanted) map->set_layer_name(i, "~yhde" + String::num_int64(i));
		}
	}
	if (!values_equal(n->get(key), value)) n->set(key, value);
	ctx.applied++;
	return true;
}

bool SyncDocument::apply_resource_property(const Uuid &target, const Dictionary &p, ApplyContext &ctx) {
	Ref<Resource> res;
	if (kind_ == Kind::Resource && target == root_id_) {
		res = root_res_;
	} else {
		res = codec_find_embedded(target.to_string());
	}
	if (res.is_null()) return false;
	String key = p.get("k", String());
	if (key.is_empty()) return false;
	DecodeState st;
	st.host = this;
	st.allow_builtin_scripts = ctx.allow_builtin_scripts;
	if (key == kStateKey) {
		apply_state(res.ptr(), p.get("v", Variant()), st);
		for (const Ref<Resource> &r : st.touched) ctx.touched_resources.push_back(r);
		if (!st.ok) ctx.errors.push_back(res->get_class() + ": " + st.error);
		ctx.touched_resources.push_back(res);
		ctx.applied++;
		return true;
	}
	Variant value = decode_value(p.get("v", Variant()), st);
	for (const Ref<Resource> &r : st.touched) ctx.touched_resources.push_back(r);
	if (!st.ok) {
		ctx.errors.push_back(res->get_class() + "." + key + ": " + st.error);
		return false;
	}
	if (!values_equal(res->get(key), value)) res->set(key, value);
	ctx.touched_resources.push_back(res);
	ctx.applied++;
	return true;
}

// Replaces a resource's whole stored state (kStateKey): the engine's own
// reset (what an in-place reload uses) drops entries the list no longer has.
bool SyncDocument::apply_state(Resource *res, const Variant &encoded, DecodeState &st) {
	if (encoded.get_type() != Variant::ARRAY) {
		st.fail("Malformed resource state");
		return false;
	}
	// A tile source's reset detaches it from its TileSet, and without the
	// TileSet its tiles have no physics/terrain/custom data layers to set.
	TileSet *owner = nullptr;
	int owner_id = -1;
	if (TileSetSource *src = Object::cast_to<TileSetSource>(res)) {
		std::vector<Ref<Resource>> candidates;
		if (root_res_.is_valid()) candidates.push_back(root_res_);
		for (const auto &kv : res_) candidates.push_back(kv.second.res);
		for (const Ref<Resource> &c : candidates) {
			TileSet *ts = Object::cast_to<TileSet>(c.ptr());
			if (!ts) continue;
			for (int i = 0; i < ts->get_source_count() && !owner; i++) {
				int id = ts->get_source_id(i);
				if (ts->get_source(id).ptr() == src) {
					owner = ts;
					owner_id = id;
				}
			}
			if (owner) break;
		}
	}
	res->reset_state();
	if (TileSet *ts = Object::cast_to<TileSet>(res)) {
		while (ts->get_patterns_count() > 0) ts->remove_pattern(0); // the reset keeps them
	}
	if (owner) {
		Ref<TileSetSource> keep(Object::cast_to<TileSetSource>(res));
		owner->remove_source(owner_id);
		owner->add_source(keep, owner_id);
	}
	return apply_properties(res, encoded, st, nullptr);
}

void SyncDocument::finish_batch(ApplyContext &ctx) {
	Node *root = root_node();

	// Node references that pointed at nodes created later in the batch.
	for (int64_t i = 0; i < ctx.deferred_refs.size(); i++) {
		Array entry = ctx.deferred_refs[i];
		Object *o = ObjectDB::get_instance(uint64_t(int64_t(entry[0])));
		if (!o) continue;
		DecodeState st;
		st.host = this;
		st.allow_builtin_scripts = ctx.allow_builtin_scripts;
		Variant v = decode_value(entry[2], st);
		if (!st.ok) {
			ctx.errors.push_back(String(entry[1]) + ": " + st.error);
			continue;
		}
		if (Node *n = Object::cast_to<Node>(o)) touch(n, ctx);
		o->set(String(entry[1]), v);
	}

	for (auto &dc : ctx.deferred_conns) {
		if (Node *n = node(dc.first)) apply_connections(n, dc.second);
	}

	// Names can collide transiently (swaps, create-then-rename); retry.
	for (int pass = 0; pass < 3; pass++) {
		bool pending = false;
		for (auto &dn : ctx.desired_names) {
			Node *n = node(dn.first);
			if (!n || String(n->get_name()) == dn.second) continue;
			n->set_name(dn.second);
			pending = pending || String(n->get_name()) != dn.second;
		}
		if (!pending) break;
	}

	std::vector<Reach> reach;

	if (kind_ == Kind::Scene && root && ctx.structure_changed) {
		std::vector<Visit> visits;
		walk(WalkMode::Refresh, visits);
		std::unordered_map<uint64_t, Uuid> ids;
		for (const Visit &v : visits) ids[v.oid] = v.id;
		for (const Visit &v : visits) {
			NodeEntry &e = nodes_[v.id];
			bool fresh_internal = v.fresh && !v.own;
			e.id = v.id;
			e.oid = v.oid;
			e.parent = v.parent;
			e.name = v.node->get_name();
			e.cls = v.node->get_class();
			e.own = v.own;
			e.index = v.node->get_index();
			e.child_keys = child_keys_of(v.node, ids);
			node_by_oid_[v.oid] = v.id;
			if (fresh_internal) snapshot_node(e, v.node, &reach);
		}
	}

	for (uint64_t oid : ctx.created) {
		Node *n = Object::cast_to<Node>(ObjectDB::get_instance(oid));
		auto it = node_by_oid_.find(oid);
		if (!n || it == node_by_oid_.end()) continue;
		settle_tiles(n);
		snapshot_node(nodes_[it->second], n, &reach);
	}

	// Only properties the batch actually changed move the shadow forward, so a
	// local edit that is still in flight (e.g. a drag) is not swallowed.
	for (auto &kv : ctx.before) {
		if (ctx.created.count(kv.first)) continue;
		Node *n = Object::cast_to<Node>(ObjectDB::get_instance(kv.first));
		auto it = node_by_oid_.find(kv.first);
		if (!n || it == node_by_oid_.end()) continue;
		NodeEntry &e = nodes_[it->second];
		String base = e.id.to_string() + "/prop:";
		settle_tiles(n);
		std::vector<StoredProperty> props = stored_properties(n);
		for (size_t i = 0; i < props.size(); i++) {
			Variant after = n->get(props[i].name);
			collect_reach(after, base + String(props[i].name), reach);
			const Variant *before = find_prop(kv.second, props[i].name, i);
			if (before && values_equal(after, *before)) continue;
			set_prop(e.props, props[i].name, after, i);
		}
		prune_props(e.props, props);
		e.groups = persistent_groups(n);
		e.conns = connections_of(n);
		if (Node3D *n3 = Object::cast_to<Node3D>(n)) n3->update_gizmos();
	}

	for (const Ref<Resource> &r : ctx.touched_resources) {
		if (r.is_null()) continue;
		auto it = res_by_oid_.find(r->get_instance_id());
		if (it == res_by_oid_.end()) continue;
		ResEntry &e = res_[it->second];
		snapshot_resource(e, &reach);
	}
	// Index loop: snapshotting appends nested resources to `reach`.
	std::unordered_set<uint64_t> seen;
	for (size_t i = 0; i < reach.size(); i++) {
		Ref<Resource> res = reach[i].res;
		String hint = reach[i].hint;
		if (res.is_null() || !codec_is_embedded(res) || !seen.insert(res->get_instance_id()).second) continue;
		auto it = res_by_oid_.find(res->get_instance_id());
		if (it == res_by_oid_.end()) {
			codec_embedded_id(res, hint);
			it = res_by_oid_.find(res->get_instance_id());
		}
		ResEntry &e = res_[it->second];
		if (e.pending) snapshot_resource(e, &reach);
	}
	for (auto &kv : res_) {
		if (kv.second.pending) snapshot_resource(kv.second, nullptr);
	}
}

// Presence helpers

bool SyncDocument::transform_in_flight(Node *n) const {
	auto it = node_by_oid_.find(n ? n->get_instance_id() : 0);
	if (it == node_by_oid_.end()) return false;
	const NodeEntry &e = nodes_.at(it->second);
	static const StringName k2d[] = { "position", "rotation", "scale", "skew" };
	static const StringName k3d[] = { "transform" };
	static const StringName kctl[] = { "offset_left", "offset_top", "offset_right", "offset_bottom", "rotation", "scale" };
	const StringName *keys = nullptr;
	size_t count = 0;
	if (Object::cast_to<Node2D>(n)) {
		keys = k2d;
		count = 4;
	} else if (Object::cast_to<Node3D>(n)) {
		keys = k3d;
		count = 1;
	} else if (Object::cast_to<Control>(n)) {
		keys = kctl;
		count = 6;
	}
	for (size_t i = 0; i < count; i++) {
		const Variant *old = find_prop(e.props, keys[i], 0);
		if (old && !values_equal(n->get(keys[i]), *old)) return true;
	}
	return false;
}

void SyncDocument::revert_property(const Uuid &target, const String &key, const Variant &encoded) {
	DecodeState st;
	st.host = this;
	if (key == kStateKey) {
		Ref<Resource> res = (kind_ == Kind::Resource && target == root_id_) ? root_res_ : codec_find_embedded(target.to_string());
		if (res.is_null()) return;
		apply_state(res.ptr(), encoded, st);
		auto it = res_.find(target);
		if (it != res_.end()) it->second.props = read_props(res.ptr());
		return;
	}
	Variant v = decode_value(encoded, st);
	if (!st.ok) return;
	if (Node *n = node(target)) {
		if (key.begins_with("@")) return;
		n->set(key, v);
		auto it = nodes_.find(target);
		if (it != nodes_.end()) set_prop(it->second.props, key, n->get(key), 0);
		return;
	}
	Ref<Resource> res = (kind_ == Kind::Resource && target == root_id_) ? root_res_ : codec_find_embedded(target.to_string());
	if (res.is_valid()) {
		res->set(key, v);
		auto it = res_.find(target);
		if (it != res_.end()) set_prop(it->second.props, key, res->get(key), 0);
	}
}

} // namespace yhde
