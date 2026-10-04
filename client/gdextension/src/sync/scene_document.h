#pragma once

#include "core/uuid.h"
#include "sync/variant_codec.h"

#include <godot_cpp/classes/node.hpp>
#include <godot_cpp/classes/resource.hpp>
#include <godot_cpp/classes/scene_state.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/packed_string_array.hpp>

#include <functional>
#include <memory>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace yhde {

// An operation produced by diffing local state (before it gets an op_id).
struct LocalOp {
	std::string type;
	Uuid target;
	godot::String key; // what this op overwrites; used to mask stale remote values
	godot::Dictionary payload;
};

// What a scan does with one changed property it found.
enum class Verdict {
	Emit,   // a user edit: send it and move the shadow forward
	Absorb, // engine-driven (layout, scrolling, animation preview…): shadow only
	Keep,   // not now (e.g. a drag in progress): leave the shadow as it is
};
// Decides per changed property; `owner` is the Node or Resource that changed.
using PropFilter = std::function<Verdict(godot::Object *owner, const godot::StringName &property)>;

using PropList = std::vector<std::pair<godot::StringName, godot::Variant>>;

// Scratch state for applying one batch of remote operations to a document.
struct ApplyContext {
	bool allow_builtin_scripts = false;
	godot::Array deferred_refs; // [object id, property, encoded value]
	std::vector<std::pair<Uuid, godot::Array>> deferred_conns;
	std::vector<std::pair<Uuid, godot::String>> desired_names;
	std::unordered_map<uint64_t, PropList> before; // node object id -> props before the batch
	std::unordered_set<uint64_t> created;
	std::vector<godot::Ref<godot::Resource>> touched_resources;
	// Nodes detached by remote deletes. The editor frees nodes lazily (its undo
	// history keeps them alive and several docks hold raw pointers), so these
	// are freed by the engine after a grace period, never immediately.
	std::vector<uint64_t> detached;
	bool structure_changed = false;
	godot::PackedStringArray errors;
	int applied = 0;
};

// One synchronized document: a scene (root Node) or an external resource file
// (root Resource). Owns identity (UUIDs) and the shadow copy of the last
// synchronized state; turns local edits into operations and remote operations
// into edits (operation_system.md).
class SyncDocument : public CodecHost {
public:
	enum class Kind { Scene, Resource };

	static std::unique_ptr<SyncDocument> for_scene(const godot::String &path, godot::Node *root);
	static std::unique_ptr<SyncDocument> for_resource(const godot::String &path, const godot::Ref<godot::Resource> &res);

	Kind kind() const { return kind_; }
	const godot::String &path() const { return path_; }
	godot::Node *root_node() const;
	godot::Ref<godot::Resource> root_resource() const { return root_res_; }
	uint64_t root_oid() const { return root_oid_; }
	const Uuid &root_id() const { return root_id_; }

	// First sight: assign ids (stored metadata, else derived deterministically
	// from the location) and snapshot everything. Emits nothing.
	void adopt(std::vector<godot::Ref<godot::Resource>> *externals = nullptr);
	// Compare live state with the shadow; emit operations and update the shadow.
	// External resource files reachable from this document are reported.
	// Without a filter every changed property is emitted; structure (nodes
	// created, deleted, moved, renamed, reordered) is always emitted.
	void diff(std::vector<LocalOp> &out, std::vector<godot::Ref<godot::Resource>> *externals,
			const PropFilter *filter = nullptr);
	// Describe the whole document as creation operations (new files).
	void announce(std::vector<LocalOp> &out);
	// The scene's root object is no longer the one we track. For Change Type of
	// the root (same identity, new class) or Make Scene Root (another node took
	// over) it emits the operation and moves the shadow. A reload from disk
	// returns false (new objects throughout).
	bool rebase_root(godot::Node *current_root, std::vector<LocalOp> &out);
	// Instance subtrees were rebuilt (new objects, same identities): point the
	// ids at the new objects. Their changed values are absorbed by a scan.
	void rebind_objects();
	// Moves the shadow of one node to its live values without emitting
	// (engine drift of nodes that change every frame). `only` limits it to
	// some properties.
	void absorb_node(godot::Node *node, const std::unordered_set<std::string> *only);

	// Remote operations.
	bool apply(const std::string &type, const Uuid &target, const godot::Dictionary &payload, ApplyContext &ctx);
	// Replays only the sibling-order effect of our own create/move/reorder
	// when it reaches its place in the log.
	bool apply_position(const std::string &type, const Uuid &target, const godot::Dictionary &payload, ApplyContext &ctx);
	void finish_batch(ApplyContext &ctx);

	// Lookups for presence overlays.
	godot::Node *node(const Uuid &id) const;
	Uuid node_id(godot::Node *n) const;
	// True when a node's transform differs from the last synchronized value
	// (i.e. the user is dragging it right now).
	bool transform_in_flight(godot::Node *n) const;

	// Undo: the operations that reverse one operation of this document, using
	// the prior state it carries and the current (shadow) state.
	std::vector<LocalOp> inverse_of(const std::string &type, const Uuid &target, const godot::Dictionary &payload);
	// Moves one property's shadow to its live value (after applying an undo
	// locally, which the editor's own undo may already have done).
	void absorb_prop(const Uuid &target, const godot::String &key);

	// Reverts one property to the last value the server confirmed (used when
	// the server rejects our change).
	void revert_property(const Uuid &target, const godot::String &key, const godot::Variant &encoded);

	// CodecHost
	godot::String codec_node_id(godot::Node *node) override;
	godot::String codec_node_path(godot::Node *node) override;
	godot::String codec_embedded_id(const godot::Ref<godot::Resource> &res, const godot::String &hint) override;
	godot::String codec_known_embedded_id(const godot::Ref<godot::Resource> &res) override;
	bool codec_is_embedded(const godot::Ref<godot::Resource> &res) override;
	godot::Node *codec_find_node(const godot::String &id, const godot::String &path) override;
	godot::Ref<godot::Resource> codec_find_embedded(const godot::String &id) override;
	void codec_register_embedded(const godot::String &id, const godot::Ref<godot::Resource> &res) override;

	size_t node_count() const { return nodes_.size(); }
	size_t resource_count() const { return res_.size(); }

private:
	struct NodeEntry {
		Uuid id;
		uint64_t oid = 0;
		Uuid parent;
		godot::String name;
		godot::String cls;
		godot::String instance;
		bool own = true;
		int index = 0;
		PropList props;
		godot::PackedStringArray groups;
		godot::Array conns;
		std::vector<std::string> child_keys;
	};

	struct ResEntry {
		Uuid id;
		uint64_t oid = 0;
		godot::Ref<godot::Resource> res;
		PropList props;
		bool pending = true; // needs a snapshot
	};

	struct Visit {
		godot::Node *node = nullptr;
		uint64_t oid = 0;
		Uuid id;
		Uuid parent;
		bool own = true;
		bool fresh = false;
		bool retyped = false; // same identity, new object of another class (Change Type)
	};

	enum class WalkMode { Adopt, Diff, Refresh };

	// Resource found while scanning property values, with its id hint.
	struct Reach {
		godot::Ref<godot::Resource> res;
		godot::String hint;
	};

	SyncDocument() = default;

	void walk(WalkMode mode, std::vector<Visit> &out);
	std::vector<std::string> child_keys_of(godot::Node *parent, const std::unordered_map<uint64_t, Uuid> &ids) const;

	void snapshot_node(NodeEntry &e, godot::Node *n, std::vector<Reach> *reach);
	void snapshot_resource(ResEntry &e, std::vector<Reach> *reach);
	void collect_reach(const godot::Variant &v, const godot::String &hint, std::vector<Reach> &reach) const;
	void diff_resources(std::vector<Reach> &reach, const std::unordered_set<uint64_t> &fresh,
			std::vector<LocalOp> &out, std::vector<godot::Ref<godot::Resource>> *externals,
			const PropFilter *filter = nullptr);
	bool is_external_file_resource(const godot::Ref<godot::Resource> &res) const;

	godot::PackedStringArray persistent_groups(godot::Node *n) const;
	godot::Array connections_of(godot::Node *n);
	std::unordered_map<uint64_t, godot::Array> outgoing_connections(const std::vector<Visit> &visits);
	void apply_groups(godot::Node *n, const godot::PackedStringArray &groups);
	void apply_connections(godot::Node *n, const godot::Array &wanted_raw);

	godot::Dictionary create_payload(godot::Node *n, const NodeEntry &e, EncodeState &st);
	// Full prior state of a node (and its own subtree) from the shadow, enough
	// to re-create it exactly.
	godot::Dictionary prior_entry(const Uuid &id, EncodeState &st, bool with_subtree);
	godot::Dictionary create_from_prior(const godot::Dictionary &prior) const;
	godot::Variant shadow_value(const std::string &type, const Uuid &target, const godot::String &key, EncodeState &st);
	LocalOp make_op(const char *type, const Uuid &target, const godot::String &key) const;

	void touch(godot::Node *n, ApplyContext &ctx);
	void append_to(godot::Node *n, godot::Node *parent);
	void forget_subtree(godot::Node *n);
	bool apply_create(const Uuid &target, const godot::Dictionary &p, ApplyContext &ctx);
	bool apply_change_type(const Uuid &target, const godot::Dictionary &p, ApplyContext &ctx);
	bool apply_set_root(const Uuid &target, const godot::Dictionary &p, ApplyContext &ctx);
public:
	static void release_from_editor(godot::Node *n);

private:
	godot::Node *find_by_id_meta(const Uuid &id, const godot::String &cls) const;
	bool apply_property(const Uuid &target, const godot::Dictionary &p, ApplyContext &ctx);
	bool apply_resource_property(const Uuid &target, const godot::Dictionary &p, ApplyContext &ctx);
	bool apply_state(godot::Resource *res, const godot::Variant &encoded, DecodeState &st);

	Uuid derive(const godot::String &what) const { return Uuid::from_name(ns_, what); }
	// The id a node carries only because it came from the scene it instances
	// (or the base scene this one inherits); such an id belongs to that other
	// document and is not adopted.
	godot::String inherited_meta(godot::Node *n, godot::Node *root) const;
	godot::Ref<godot::SceneState> base_state_;

	Kind kind_ = Kind::Scene;
	godot::String path_;
	Uuid ns_;
	Uuid root_id_;
	uint64_t root_oid_ = 0;
	godot::Ref<godot::Resource> root_res_;
	bool adopting_ = false;

	std::unordered_map<Uuid, NodeEntry, UuidHash> nodes_;
	std::unordered_map<uint64_t, Uuid> node_by_oid_;
	std::unordered_map<Uuid, ResEntry, UuidHash> res_;
	std::unordered_map<uint64_t, Uuid> res_by_oid_;
	std::unordered_set<Uuid, UuidHash> retired_;
};

godot::String op_key_for(const std::string &type, const godot::Dictionary &payload);

} // namespace yhde
