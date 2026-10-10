#pragma once

#include "core/uuid.h"
#include "sync/scene_document.h"

#include <godot_cpp/classes/node.hpp>
#include <godot_cpp/classes/packed_scene.hpp>
#include <godot_cpp/classes/scene_state.hpp>
#include <godot_cpp/classes/resource.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/string.hpp>

#include <functional>
#include <map>
#include <memory>
#include <set>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace yhde {

// A committed operation on its way into the local project.
struct RemoteOp {
	int64_t seq = 0;
	std::string type;
	Uuid target;
	godot::String doc;
	godot::Dictionary payload;
	// This client authored it.
	bool own = false;
	Uuid client_op_ref;
	// ...and the edit is already present in memory.
	bool own_applied = false;
	godot::String actor; // display name for the activity feed
};

// How a scan treats the property changes it finds (editor_client.md).
enum class ScanKind {
	Action,   // inside an undo-history commit: the action's own changes are emitted
	FollowUp, // a few frames after an action: deferred parts emitted, engine consequences absorbed
	Idle,     // periodic: edits made without the undo history emitted, engine drift absorbed
	Settle,   // after opening a scene or applying a remote batch: all property drift absorbed
};

// Keeps every open scene (2D, 3D, UI: the engine does not care) and every
// referenced external resource file in sync with the operation log.
class SyncEngine {
public:
	struct Hooks {
		// `group` ties the op to the editor action that produced it (0: none).
		std::function<void(LocalOp &&op, uint64_t group)> emit;
		std::function<bool(const godot::String &doc, const Uuid &target, const godot::String &key)> masked;
		std::function<void(const godot::String &doc, int64_t seq, bool persisted)> applied;
		std::function<void(const godot::String &text, int level)> notice;
		std::function<void(const RemoteOp &op, const godot::String &subject)> activity;
		std::function<int64_t(const godot::String &doc)> doc_applied;
		// Our own operation reached its place in the log (drop it from the
		// pending queue exactly there, so masking follows log order).
		std::function<void(const Uuid &client_op_ref)> own_committed;
		// A document's file now holds everything applied to it (saved by us).
		std::function<void(const godot::String &doc)> persisted;
		// A document is reloaded from its file: local edits still pending are
		// not in it and must be applied when they commit.
		std::function<void(const godot::String &doc)> reloading;
		// The engine wrote a document's file itself (its bytes reflect the log).
		std::function<void(const godot::String &path)> file_written;
		// A document is about to be tracked: its file must be known to the log
		// before any operation on it.
		std::function<void(const godot::String &path)> before_document;
	};

	void set_hooks(Hooks hooks) { hooks_ = std::move(hooks); }
	void set_allow_builtin_scripts(bool allow) { allow_builtin_scripts_ = allow; }
	// Files travel through the asset plane: new scenes and resources are not
	// announced node by node (their bytes arrive instead).
	void set_assets_active(bool active) { assets_active_ = active; }
	// A document's file was replaced by other bytes: reload it wherever it is open.
	void on_file_replaced(const godot::String &path);
	bool allow_builtin_scripts() const { return allow_builtin_scripts_; }

	void start();
	void stop();
	bool active() const { return active_; }

	// Per-frame: follow open scenes, run follow-up, idle and settle scans.
	void process(double now);
	void request_refresh() { refresh_requested_ = true; }
	// Scans the given scene documents (and resource files) right now and
	// returns the operations found; used for editor actions, emits nothing.
	std::vector<LocalOp> scan(const std::vector<godot::String> &scenes, ScanKind kind);
	// Undo/redo of one editor action: applies the inverse of `group` locally
	// (the editor's own undo may already have done most of it) and returns the
	// operations to send, plus anything else the editor's undo changed.
	std::vector<LocalOp> revert(const std::vector<LocalOp> &group);
	// Emits a scan's operations through the emit hook.
	void emit_group(std::vector<LocalOp> &ops, uint64_t group);
	// Deferred parts of an action (and its engine consequences) show up a few
	// frames later; they are sorted out by a follow-up scan tied to its group.
	void schedule_followup(const std::vector<godot::String> &scenes, uint64_t group);
	// Paths of the open, synchronized scenes.
	std::vector<godot::String> live_scene_paths() const;
	godot::Node *live_root(const godot::String &path) const;

	void apply_batch(std::vector<RemoteOp> &ops);

	void on_scene_saved(const godot::String &path);
	// The inspector started editing an object: baseline external resource
	// files before their first edit so that edit is not missed.
	void on_inspected(godot::Object *object);
	void on_resource_saved(const godot::Ref<godot::Resource> &res);
	// Returns the paths of documents that were closed since the last call.
	std::vector<godot::String> take_closed_docs();

	SyncDocument *current_doc();
	// Roots of all open, synchronized scenes.
	std::vector<godot::Node *> live_roots() const;
	SyncDocument *find_doc(const godot::String &path);
	bool is_live(const godot::String &path) const;
	godot::String current_scene_path() const { return current_path_; }
	// The edited scene has never been saved, so it has no shared identity yet.
	bool current_scene_unsaved() const { return current_unsaved_; }

	void revert(const godot::String &doc, const Uuid &target, const godot::String &key, const godot::Variant &encoded);

	double last_scan_ms() const { return last_scan_ms_; }

private:
	void refresh_open_scenes(bool announce_new);
	SyncDocument *resource_doc(const godot::String &path, bool create_if_missing, const godot::String &create_class);
	PropFilter make_filter(ScanKind kind);
	void refresh_touch();
	bool is_view_state(godot::Object *owner, const godot::StringName &property) const;
	bool is_engine_driven(godot::Node *node, const godot::StringName &property);
	void rebuild_animated(double now);
	void run_idle_scan();
	void settle(double now);
	void absorb_drifters(double now);
	// `scene` is the open scene the nodes came from; `remap` (for a node
	// replaced by graft) is its path from that scene's root.
	void bury(const std::vector<uint64_t> &nodes, bool immediately, godot::Node *scene = nullptr,
			const godot::NodePath &remap = godot::NodePath());
	void fix_restored_selection(godot::Node *current_root);
	void remember_selection(godot::Node *current_root);
	double last_selection_note_ = -100.0;
	void sweep_graveyard(double now, bool all);
	void emit_all(std::vector<LocalOp> &ops);

	void apply_scene_ops(const godot::String &path, std::vector<RemoteOp *> &ops);
	// Load the file, apply, save it back (documents not open in the editor).
	void apply_to_file(const godot::String &path, std::vector<RemoteOp *> &ops);

	// Instances of a scene that changed are rebuilt in every other open scene
	// (editor_client.md): the open scenes are packed first (the engine then
	// knows exactly what they override), the source's cached PackedScene is
	// updated in place, and each instance subtree is re-instantiated and
	// swapped in. Other nodes keep their objects (and their undo history).
	struct InstanceTarget {
		godot::String scene;
		godot::Ref<godot::PackedScene> packed; // the open scene, packed before the change
		std::vector<godot::String> paths;      // its instances containing the source
		bool inherited = false;                // its root inherits the source
	};
	std::vector<InstanceTarget> prepare_instance_refresh(const godot::String &source);
	void finish_instance_refresh(std::vector<InstanceTarget> &targets);
	void refresh_live_source(const godot::String &source);
	void graft(godot::Node *live_root, godot::Node *old, godot::Node *fresh, const godot::Ref<godot::SceneState> &state,
			const godot::String &path);
	void schedule_instance_refresh(const godot::String &source);
	godot::Node *open_root(const godot::String &path) const;
	void apply_resource_ops(const godot::String &path, std::vector<RemoteOp *> &ops);
	int apply_to_doc(SyncDocument &doc, std::vector<RemoteOp *> &ops, bool live, bool persisted);
	godot::String describe(SyncDocument *doc, const RemoteOp &op) const;

	Hooks hooks_;
	bool active_ = false;
	bool debug_ = false; // YHDE_DEBUG environment variable
	bool allow_builtin_scripts_ = false;
	bool assets_active_ = false;
	bool refresh_requested_ = true;
	double last_refresh_ = 0.0;
	double last_idle_ = 0.0;
	double last_action_ = -100.0;
	double last_scan_ms_ = 0.0;
	uint64_t edited_root_oid_ = 0;
	// Documents whose engine-driven changes (layout, recomputed transforms)
	// are absorbed silently shortly after adoption or a remote batch.
	std::map<godot::String, double> settle_at_;
	struct FollowUp {
		std::vector<godot::String> scenes;
		uint64_t group = 0;
		int frames = 0; // frames still to wait
	};
	std::vector<FollowUp> followups_;
	// Per scan: what the user is touching right now.
	std::unordered_set<uint64_t> selected_;
	uint64_t inspected_ = 0;
	bool gesture_ = false;
	// Properties that animation tracks drive (animation preview is engine drift).
	std::unordered_map<uint64_t, std::unordered_set<std::string>> animated_;
	double animated_built_ = -100.0;
	// Nodes that change every frame on their own (tool scripts, playing
	// animations): their shadow follows them each frame so an editor action
	// never carries their drift.
	struct Drifter {
		uint64_t node = 0;
		bool whole = false; // every property (tool script) or only animated ones
	};
	std::vector<Drifter> drifters_;
	double drifters_built_ = -100.0;
	// Open scenes edited here or by a peer, whose instances elsewhere follow.
	std::map<godot::String, double> instance_refresh_at_;
	// Detached nodes waiting to be freed. The editor keeps the selection of a
	// scene tab that is not shown as raw pointers (EditorData::EditedScene::
	// selection) and uses them when the tab is shown again or every scene is
	// saved. So a node from a background tab stays alive until that tab has
	// been shown (and its restored selection fixed) or closed, then for the
	// grace time.
	struct Grave {
		uint64_t node = 0;
		double after = 0.0;
		uint64_t scene = 0;   // root of the scene tab it came from (0: none)
		bool shown = true;    // that tab was current since the node left
		godot::NodePath remap; // graft: the replacement's path from the root
	};
	std::vector<Grave> graveyard_;
	// What each scene tab had selected while it was shown, by path from its
	// root: restored when the editor could not restore it itself.
	std::map<uint64_t, std::vector<godot::NodePath>> tab_selection_;
	double now_ = 0.0;

	std::map<godot::String, std::unique_ptr<SyncDocument>> scenes_;
	std::map<godot::String, std::unique_ptr<SyncDocument>> resources_;
	std::unordered_map<uint64_t, godot::String> root_paths_; // root object -> last known path ("" = unsaved)
	std::vector<godot::String> closed_;
	godot::String current_path_;
	bool current_unsaved_ = false;
};

} // namespace yhde
