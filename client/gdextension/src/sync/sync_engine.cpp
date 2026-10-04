#include "sync/sync_engine.h"

#include <godot_cpp/classes/animation.hpp>
#include <godot_cpp/classes/animation_library.hpp>
#include <godot_cpp/classes/animation_mixer.hpp>
#include <godot_cpp/classes/class_db_singleton.hpp>
#include <godot_cpp/classes/control.hpp>
#include <godot_cpp/classes/editor_file_system.hpp>
#include <godot_cpp/classes/editor_inspector.hpp>
#include <godot_cpp/classes/editor_interface.hpp>
#include <godot_cpp/classes/editor_selection.hpp>
#include <godot_cpp/classes/file_access.hpp>
#include <godot_cpp/classes/graph_edit.hpp>
#include <godot_cpp/classes/input.hpp>
#include <godot_cpp/classes/node2d.hpp>
#include <godot_cpp/classes/node3d.hpp>
#include <godot_cpp/classes/packed_scene.hpp>
#include <godot_cpp/classes/resource_loader.hpp>
#include <godot_cpp/classes/resource_saver.hpp>
#include <godot_cpp/classes/script.hpp>
#include <godot_cpp/classes/skeleton3d.hpp>
#include <godot_cpp/classes/time.hpp>
#include <godot_cpp/core/object.hpp>
#include <godot_cpp/variant/typed_array.hpp>

#include <godot_cpp/classes/os.hpp>
#include <godot_cpp/variant/utility_functions.hpp>

#include <algorithm>
#include <set>

using namespace godot;

namespace yhde {

namespace {

constexpr double kRefreshInterval = 0.5; // how often open tabs are re-checked
constexpr double kSettleDelay = 0.3;     // engine drift window after adopt/apply
constexpr int kFollowUpFrames = 2;       // deferred parts of an action land within a frame or two
constexpr double kDrifterRebuild = 0.5;  // how often the set of self-changing nodes is refreshed
constexpr double kIdleInterval = 1.0;    // edits made outside the undo history
constexpr double kIdleQuiet = 0.25;      // no idle scan right after an action
constexpr double kAnimatedRebuild = 2.0; // animation track targets are re-read this often
constexpr double kGraveGrace = 5.0;      // let editor docks drop pointers to deleted nodes
constexpr double kInstanceRefresh = 0.3; // instances follow their scene after edits settle

// Properties that hold view or playback state: where a text box or graph is
// scrolled to, which tab is showing, the frame an animation preview reached.
// The engine rewrites them on its own; the user sets them through actions.
bool is_view_property(Object *owner, const String &name) {
	if (name == "scroll_horizontal" || name == "scroll_vertical" || name == "scroll_offset" || name == "split_offset" ||
			name == "split_offsets" || name == "current_tab" || name == "current_animation" ||
			name == "assigned_animation" || name == "current_animation_position") {
		return true;
	}
	if (name == "frame" || name == "frame_progress") {
		String cls = owner->get_class();
		return cls == "AnimatedSprite2D" || cls == "AnimatedSprite3D";
	}
	return false;
}

// Computed from other stored properties, never an edit of its own.
bool is_derived_property(const String &name) {
	return name == "anchors_preset";
}

// Layout and transform properties: engine consequences when they change
// outside an editor action (containers sorting children, anchors following a
// resized parent, attachments following bones).
bool is_placement_property(Node *n, const String &name) {
	if (Object::cast_to<Control>(n)) {
		return name.begins_with("offset_") || name.begins_with("anchor_") || name == "position" || name == "size" ||
				name == "rotation" || name == "scale" || name == "layout_mode" || name == "anchors_preset";
	}
	if (Object::cast_to<Node2D>(n)) {
		return name == "position" || name == "rotation" || name == "scale" || name == "skew" || name == "transform";
	}
	if (Object::cast_to<Node3D>(n)) {
		if (Object::cast_to<Skeleton3D>(n) && name.begins_with("bones/")) return true;
		return name == "transform" || name == "position" || name == "rotation" || name == "scale" ||
				name == "quaternion" || name == "basis";
	}
	return false;
}

bool contains_instance_of(Node *n, const String &source) {
	if (n->get_scene_file_path() == source) return true;
	for (int i = 0; i < n->get_child_count(); i++) {
		if (contains_instance_of(n->get_child(i), source)) return true;
	}
	return false;
}

bool inherits_from(const Ref<SceneState> &state, const String &source) {
	Ref<SceneState> base = state.is_valid() ? state->get_base_scene_state() : Ref<SceneState>();
	while (base.is_valid()) {
		if (base->get_path() == source) return true;
		base = base->get_base_scene_state();
	}
	return false;
}

bool is_scene_path(const String &p) {
	String ext = p.get_extension().to_lower();
	return ext == "tscn" || ext == "scn";
}

bool is_resource_path(const String &p) {
	String ext = p.get_extension().to_lower();
	return ext == "tres" || ext == "res";
}

bool is_external_resource(const Ref<Resource> &res) {
	if (res.is_null()) return false;
	String p = res->get_path();
	return !p.is_empty() && !p.contains("::") && is_resource_path(p) && is_safe_resource_path(p) &&
			!FileAccess::file_exists(p + ".import");
}

String humanize(const String &key) {
	String k = key;
	if (k.begins_with("@")) k = k.substr(1);
	int slash = k.rfind("/");
	if (slash >= 0) k = k.substr(slash + 1);
	return k.replace("_", " ");
}

} // namespace

void SyncEngine::start() {
	active_ = true;
	debug_ = OS::get_singleton()->has_environment("YHDE_DEBUG");
	refresh_requested_ = true;
	followups_.clear();
	scenes_.clear();
	resources_.clear();
	root_paths_.clear();
	closed_.clear();
	// Scenes that are already open count as known: only scenes saved for the
	// first time *during* the session are announced to peers.
	EditorInterface *ei = EditorInterface::get_singleton();
	if (ei) {
		TypedArray<Node> roots = ei->get_open_scene_roots();
		for (int64_t i = 0; i < roots.size(); i++) {
			Node *root = Object::cast_to<Node>(roots[i]);
			if (root) root_paths_[root->get_instance_id()] = root->get_scene_file_path();
		}
	}
}

void SyncEngine::stop() {
	sweep_graveyard(now_, true);
	active_ = false;
	settle_at_.clear();
	followups_.clear();
	animated_.clear();
	scenes_.clear();
	resources_.clear();
	root_paths_.clear();
	closed_.clear();
	current_path_ = String();
}

// Scanning

bool SyncEngine::is_view_state(Object *owner, const StringName &property) const {
	String name = property;
	if (name == "zoom") return Object::cast_to<GraphEdit>(owner) != nullptr;
	return Object::cast_to<Node>(owner) != nullptr && is_view_property(owner, name);
}

bool SyncEngine::is_engine_driven(Node *n, const StringName &property) {
	if (is_view_state(n, property) || is_placement_property(n, property)) return true;
	// Tool scripts run in every editor and set what they set on their own.
	Ref<Script> script = n->get_script();
	if (script.is_valid() && script->is_tool()) return true;
	auto it = animated_.find(n->get_instance_id());
	return it != animated_.end() && it->second.count(String(property).utf8().get_data());
}

void SyncEngine::rebuild_animated(double now) {
	if (now - animated_built_ < kAnimatedRebuild) return;
	animated_built_ = now;
	animated_.clear();
	for (const auto &kv : scenes_) {
		Node *root = kv.second->root_node();
		if (!root) continue;
		TypedArray<Node> mixers = root->find_children("*", "AnimationMixer", true, false);
		for (int64_t m = 0; m < mixers.size(); m++) {
			AnimationMixer *mixer = Object::cast_to<AnimationMixer>(mixers[m]);
			if (!mixer) continue;
			Node *base = mixer->get_node_or_null(mixer->get_root_node());
			if (!base) continue;
			TypedArray<StringName> libs = mixer->get_animation_library_list();
			for (int64_t l = 0; l < libs.size(); l++) {
				Ref<AnimationLibrary> lib = mixer->get_animation_library(libs[l]);
				if (lib.is_null()) continue;
				TypedArray<StringName> names = lib->get_animation_list();
				for (int64_t a = 0; a < names.size(); a++) {
					Ref<Animation> anim = lib->get_animation(names[a]);
					if (anim.is_null()) continue;
					for (int32_t t = 0; t < anim->get_track_count(); t++) {
						NodePath path = anim->track_get_path(t);
						Node *target = base->get_node_or_null(NodePath(String(path).get_slice(":", 0)));
						if (!target) continue;
						std::unordered_set<std::string> &props = animated_[target->get_instance_id()];
						switch (anim->track_get_type(t)) {
							case Animation::TYPE_POSITION_3D:
								props.insert("transform");
								props.insert("position");
								break;
							case Animation::TYPE_ROTATION_3D:
								props.insert("transform");
								props.insert("quaternion");
								props.insert("rotation");
								break;
							case Animation::TYPE_SCALE_3D:
								props.insert("transform");
								props.insert("scale");
								break;
							case Animation::TYPE_BLEND_SHAPE:
								props.insert(("blend_shapes/" + String(path).get_slice(":", 1)).utf8().get_data());
								break;
							case Animation::TYPE_VALUE:
							case Animation::TYPE_BEZIER: {
								String prop = String(path).get_slice(":", 1);
								if (!prop.is_empty()) props.insert(prop.utf8().get_data());
								break;
							}
							default:
								break;
						}
					}
				}
			}
		}
	}
}

void SyncEngine::refresh_touch() {
	selected_.clear();
	inspected_ = 0;
	if (EditorInterface *ei = EditorInterface::get_singleton()) {
		if (EditorSelection *sel = ei->get_selection()) {
			TypedArray<Node> selected = sel->get_selected_nodes();
			for (int64_t i = 0; i < selected.size(); i++) {
				if (Node *n = Object::cast_to<Node>(selected[i])) selected_.insert(n->get_instance_id());
			}
		}
		if (EditorInspector *inspector = ei->get_inspector()) {
			if (Object *o = inspector->get_edited_object()) inspected_ = o->get_instance_id();
		}
	}
	Input *input = Input::get_singleton();
	gesture_ = input && (input->is_mouse_button_pressed(MOUSE_BUTTON_LEFT) || input->is_mouse_button_pressed(MOUSE_BUTTON_RIGHT));
}

PropFilter SyncEngine::make_filter(ScanKind kind) {
	refresh_touch();
	switch (kind) {
		case ScanKind::Action:
			// Everything the action changed, including what the engine did
			// synchronously because of it (peers may not derive it identically).
			return [](Object *, const StringName &p) {
				return is_derived_property(p) ? Verdict::Absorb : Verdict::Emit;
			};
		case ScanKind::FollowUp:
		case ScanKind::Idle:
			return [this](Object *o, const StringName &p) {
				if (is_derived_property(p)) return Verdict::Absorb;
				Node *n = Object::cast_to<Node>(o);
				if (!n) return Verdict::Emit; // resources do not drift; edits outside the history (shader code…) are real
				if (gesture_ && selected_.count(n->get_instance_id())) return Verdict::Keep;
				return is_engine_driven(n, p) ? Verdict::Absorb : Verdict::Emit;
			};
		case ScanKind::Settle:
			return [this](Object *o, const StringName &) {
				uint64_t id = o->get_instance_id();
				return (selected_.count(id) || id == inspected_) ? Verdict::Keep : Verdict::Absorb;
			};
	}
	return nullptr;
}

std::vector<LocalOp> SyncEngine::scan(const std::vector<String> &paths, ScanKind kind) {
	std::vector<LocalOp> ops;
	if (!active_) return ops;
	uint64_t t0 = Time::get_singleton()->get_ticks_usec();
	if (kind != ScanKind::Action) rebuild_animated(now_);
	PropFilter filter = make_filter(kind);
	std::vector<Ref<Resource>> externals;

	std::set<String> done;
	for (const String &path : paths) {
		if (!done.insert(path).second) continue;
		auto it = scenes_.find(path);
		if (it == scenes_.end()) continue;
		// Change Type of the root or Make Scene Root swap the root object.
		Node *current = open_root(path);
		if (current && current != it->second->root_node() && !it->second->rebase_root(current, ops)) continue; // a reload
		if (it->second->root_node()) it->second->diff(ops, &externals, &filter);
	}

	// A .tres opened straight from the FileSystem dock is edited in the inspector.
	if (EditorInterface *ei = EditorInterface::get_singleton()) {
		if (EditorInspector *inspector = ei->get_inspector()) {
			Resource *r = Object::cast_to<Resource>(inspector->get_edited_object());
			if (r) externals.push_back(Ref<Resource>(r));
		}
	}
	std::set<String> seen;
	for (const Ref<Resource> &r : externals) {
		if (!is_external_resource(r)) continue;
		String path = r->get_path();
		if (!seen.insert(path).second) continue;
		bool existed = resources_.count(path) > 0;
		SyncDocument *rdoc = resource_doc(path, true, String());
		if (rdoc && existed) rdoc->diff(ops, nullptr, &filter);
	}
	// Resource files edited earlier but no longer referenced by a scene.
	for (auto &kv : resources_) {
		if (!seen.count(kv.first)) kv.second->diff(ops, nullptr, &filter);
	}

	if (kind == ScanKind::Action) last_action_ = now_;
	// Instances of an edited scene in the other open scenes follow it.
	for (const LocalOp &op : ops) {
		String doc = op.payload.get("s", String());
		if (is_scene_path(doc) && !instance_refresh_at_.count(doc)) schedule_instance_refresh(doc);
	}
	last_scan_ms_ = double(Time::get_singleton()->get_ticks_usec() - t0) / 1000.0;
	if (debug_ && !ops.empty()) {
		static const char *names[] = { "action", "follow-up", "idle", "settle" };
		UtilityFunctions::print("[yhde] ", names[int(kind)], " scan: ", int64_t(ops.size()), " ops in ", last_scan_ms_, "ms");
		if (kind == ScanKind::Idle || kind == ScanKind::FollowUp) {
			for (const LocalOp &op : ops) {
				UtilityFunctions::print("[yhde]   ", String::utf8(op.type.c_str()), " ", op.key, " v=", to_json(op.payload.get("v", Variant())),
						" o=", to_json(op.payload.get("o", Variant())));
			}
		}
	}
	return ops;
}

std::vector<LocalOp> SyncEngine::revert(const std::vector<LocalOp> &group) {
	std::vector<LocalOp> inverses;
	std::vector<String> scenes;
	for (auto it = group.rbegin(); it != group.rend(); ++it) {
		String path = it->payload.get("s", String());
		SyncDocument *doc = find_doc(path);
		if (!doc) continue;
		for (LocalOp &op : doc->inverse_of(it->type, it->target, it->payload)) inverses.push_back(std::move(op));
		if (doc->kind() == SyncDocument::Kind::Scene && std::find(scenes.begin(), scenes.end(), path) == scenes.end()) {
			scenes.push_back(path);
		}
	}

	// Root swaps were already undone by the editor itself (its undo restores
	// the edited scene root); follow them instead of applying them again.
	for (const String &path : scenes) {
		SyncDocument *doc = find_doc(path);
		Node *current = open_root(path);
		std::vector<LocalOp> ignored;
		if (doc && current && current != doc->root_node()) doc->rebase_root(current, ignored);
	}

	std::map<String, ApplyContext> contexts;
	std::vector<String> order;
	for (const LocalOp &op : inverses) {
		String path = op.payload.get("s", String());
		SyncDocument *doc = find_doc(path);
		if (!doc) continue;
		if (op.type == "SetSceneRoot" || (op.type == "ChangeNodeType" && op.target == doc->root_id())) continue;
		if (!contexts.count(path)) {
			order.push_back(path);
			contexts[path].allow_builtin_scripts = allow_builtin_scripts_;
		}
		doc->apply(op.type, op.target, op.payload, contexts[path]);
	}
	for (const String &path : order) {
		// Nodes this detaches stay owned by the editor's undo history.
		find_doc(path)->finish_batch(contexts[path]);
		if (hooks_.notice) {
			for (int64_t i = 0; i < contexts[path].errors.size(); i++) hooks_.notice(contexts[path].errors[i], 1);
		}
	}
	for (const LocalOp &op : inverses) {
		if (op.type != "ChangeProperty" && op.type != "ChangeResourceProperty") continue;
		if (SyncDocument *doc = find_doc(op.payload.get("s", String()))) doc->absorb_prop(op.target, op.key);
	}

	std::vector<LocalOp> rest = scan(scenes, ScanKind::Action);
	for (LocalOp &op : rest) inverses.push_back(std::move(op));
	return inverses;
}

void SyncEngine::emit_group(std::vector<LocalOp> &ops, uint64_t group) {
	if (hooks_.emit) {
		for (LocalOp &op : ops) hooks_.emit(std::move(op), group);
	}
	ops.clear();
}

void SyncEngine::schedule_followup(const std::vector<String> &scenes, uint64_t group) {
	FollowUp f;
	f.scenes = scenes;
	f.group = group;
	f.frames = kFollowUpFrames;
	followups_.push_back(std::move(f));
}

void SyncEngine::absorb_drifters(double now) {
	SyncDocument *doc = current_doc();
	Node *root = doc ? doc->root_node() : nullptr;
	if (!root) {
		drifters_.clear();
		return;
	}
	if (now - drifters_built_ >= kDrifterRebuild) {
		drifters_built_ = now;
		rebuild_animated(now);
		drifters_.clear();
		TypedArray<Node> all = root->find_children("*", "", true, false);
		all.push_front(root);
		for (int64_t i = 0; i < all.size(); i++) {
			Node *n = Object::cast_to<Node>(all[i]);
			if (!n) continue;
			Ref<Script> script = n->get_script();
			if (script.is_valid() && script->is_tool()) drifters_.push_back({ n->get_instance_id(), true });
		}
		for (const auto &kv : animated_) drifters_.push_back({ kv.first, false });
	}
	if (drifters_.empty()) return;
	refresh_touch();
	for (const Drifter &d : drifters_) {
		Node *n = Object::cast_to<Node>(ObjectDB::get_instance(d.node));
		if (!n) continue;
		// A drag in progress is the user's, not drift.
		if (gesture_ && selected_.count(d.node)) continue;
		if (d.whole) {
			doc->absorb_node(n, nullptr);
		} else {
			auto it = animated_.find(d.node);
			if (it != animated_.end()) doc->absorb_node(n, &it->second);
		}
	}
}

std::vector<String> SyncEngine::live_scene_paths() const {
	std::vector<String> out;
	for (const auto &kv : scenes_) {
		if (kv.second->root_node()) out.push_back(kv.first);
	}
	return out;
}

Node *SyncEngine::open_root(const String &path) const {
	EditorInterface *ei = EditorInterface::get_singleton();
	if (!ei) return nullptr;
	TypedArray<Node> roots = ei->get_open_scene_roots();
	for (int64_t i = 0; i < roots.size(); i++) {
		Node *root = Object::cast_to<Node>(roots[i]);
		if (root && root->get_scene_file_path() == path) return root;
	}
	return nullptr;
}

Node *SyncEngine::live_root(const String &path) const {
	auto it = scenes_.find(path);
	return it != scenes_.end() ? it->second->root_node() : nullptr;
}

void SyncEngine::run_idle_scan() {
	std::vector<String> paths;
	if (!current_path_.is_empty()) paths.push_back(current_path_);
	std::vector<LocalOp> ops = scan(paths, ScanKind::Idle);
	emit_group(ops, 0);
}

void SyncEngine::settle(double now) {
	for (auto it = settle_at_.begin(); it != settle_at_.end();) {
		if (now < it->second) {
			++it;
			continue;
		}
		String path = it->first;
		it = settle_at_.erase(it);
		SyncDocument *doc = find_doc(path);
		if (!doc) continue;
		PropFilter filter = make_filter(ScanKind::Settle);
		std::vector<LocalOp> ops;
		doc->diff(ops, nullptr, &filter);
		// Structure is never absorbed (a node must not be lost).
		if (debug_ && !ops.empty()) UtilityFunctions::print("[yhde] settle scan kept ", int64_t(ops.size()), " structural ops in ", path);
		emit_group(ops, 0);
	}
}

void SyncEngine::bury(const std::vector<uint64_t> &nodes, bool immediately) {
	for (uint64_t id : nodes) graveyard_.emplace_back(id, immediately ? 0.0 : now_ + kGraveGrace);
}

void SyncEngine::sweep_graveyard(double now, bool all) {
	for (auto it = graveyard_.begin(); it != graveyard_.end();) {
		if (!all && now < it->second) {
			++it;
			continue;
		}
		// Only free what is still alive and still detached: the editor's undo
		// history may own it (it frees by id, so this never double-frees) or
		// may have re-attached it.
		Node *n = Object::cast_to<Node>(ObjectDB::get_instance(it->first));
		if (n && !n->get_parent() && !n->is_inside_tree()) n->queue_free();
		it = graveyard_.erase(it);
	}
}

void SyncEngine::process(double now) {
	if (!active_) return;
	now_ = now;
	if (!graveyard_.empty()) sweep_graveyard(now, false);
	// Adopt a newly opened/switched scene on the very next frame, before the
	// user can touch it (its first edit must not end up in the baseline).
	if (EditorInterface *ei = EditorInterface::get_singleton()) {
		Node *edited = ei->get_edited_scene_root();
		uint64_t oid = edited ? edited->get_instance_id() : 0;
		if (oid != edited_root_oid_) {
			edited_root_oid_ = oid;
			refresh_requested_ = true;
		}
	}
	if (refresh_requested_ || now - last_refresh_ >= kRefreshInterval) {
		refresh_requested_ = false;
		last_refresh_ = now;
		refresh_open_scenes(true);
	}
	absorb_drifters(now);
	if (!followups_.empty()) {
		std::vector<FollowUp> due;
		for (auto it = followups_.begin(); it != followups_.end();) {
			if (--it->frames <= 0) {
				due.push_back(std::move(*it));
				it = followups_.erase(it);
			} else {
				++it;
			}
		}
		for (FollowUp &f : due) {
			std::vector<LocalOp> ops = scan(f.scenes, ScanKind::FollowUp);
			emit_group(ops, f.group);
		}
	}
	if (!settle_at_.empty()) settle(now);
	if (!instance_refresh_at_.empty() && !gesture_) {
		std::vector<String> due;
		for (auto it = instance_refresh_at_.begin(); it != instance_refresh_at_.end();) {
			if (now >= it->second) {
				due.push_back(it->first);
				it = instance_refresh_at_.erase(it);
			} else {
				++it;
			}
		}
		for (const String &source : due) refresh_live_source(source);
	}
	if (followups_.empty() && now - last_idle_ >= kIdleInterval && now - last_action_ >= kIdleQuiet) {
		last_idle_ = now;
		run_idle_scan();
	}
}

std::vector<String> SyncEngine::take_closed_docs() {
	std::vector<String> out;
	out.swap(closed_);
	return out;
}

SyncDocument *SyncEngine::current_doc() {
	if (current_path_.is_empty()) return nullptr;
	return find_doc(current_path_);
}

std::vector<Node *> SyncEngine::live_roots() const {
	std::vector<Node *> out;
	for (const auto &kv : scenes_) {
		if (Node *root = kv.second->root_node()) out.push_back(root);
	}
	return out;
}

SyncDocument *SyncEngine::find_doc(const String &path) {
	auto it = scenes_.find(path);
	if (it != scenes_.end()) return it->second.get();
	auto rt = resources_.find(path);
	return rt != resources_.end() ? rt->second.get() : nullptr;
}

bool SyncEngine::is_live(const String &path) const {
	auto it = scenes_.find(path);
	if (it != scenes_.end()) return it->second->root_node() != nullptr;
	return resources_.count(path) > 0;
}

void SyncEngine::emit_all(std::vector<LocalOp> &ops) {
	emit_group(ops, 0);
}

void SyncEngine::refresh_open_scenes(bool announce_new) {
	EditorInterface *ei = EditorInterface::get_singleton();
	if (!ei) return;
	TypedArray<Node> roots = ei->get_open_scene_roots();
	Node *edited = ei->get_edited_scene_root();
	current_path_ = String();
	current_unsaved_ = edited && edited->get_scene_file_path().is_empty();

	std::set<String> open_paths;
	std::set<uint64_t> open_roots;
	for (int64_t i = 0; i < roots.size(); i++) {
		Node *root = Object::cast_to<Node>(roots[i]);
		if (!root) continue;
		uint64_t oid = root->get_instance_id();
		open_roots.insert(oid);
		String path = root->get_scene_file_path();
		auto prev = root_paths_.find(oid);
		bool seen_before = prev != root_paths_.end();
		String prev_path = seen_before ? prev->second : String();
		root_paths_[oid] = path;
		if (path.is_empty() || !is_safe_resource_path(path)) continue; // unsaved: no shared identity yet
		open_paths.insert(path);
		if (root == edited) current_path_ = path;

		auto it = scenes_.find(path);
		if (it != scenes_.end() && it->second->root_oid() == oid) continue;
		if (it != scenes_.end()) {
			// Same file, new root: the scene was reloaded from disk. Anything
			// applied only in memory is gone and must be replayed.
			closed_.push_back(path);
		}

		if (hooks_.before_document) hooks_.before_document(path);
		std::unique_ptr<SyncDocument> doc = SyncDocument::for_scene(path, root);
		std::vector<Ref<Resource>> externals;
		doc->adopt(&externals);
		for (const Ref<Resource> &r : externals) {
			if (is_external_resource(r)) resource_doc(r->get_path(), true, String());
		}
		bool is_new_file = seen_before && prev_path != path;
		settle_at_[path] = now_ + kSettleDelay;
		if (announce_new && is_new_file && !assets_active_) {
			std::vector<LocalOp> ops;
			doc->announce(ops);
			if (hooks_.notice) hooks_.notice(String("Shared new scene ") + path.get_file(), 0);
			scenes_[path] = std::move(doc);
			emit_all(ops);
		} else {
			scenes_[path] = std::move(doc);
		}
	}

	for (auto it = scenes_.begin(); it != scenes_.end();) {
		if (open_paths.count(it->first)) {
			++it;
		} else {
			closed_.push_back(it->first);
			it = scenes_.erase(it);
		}
	}
	for (auto it = root_paths_.begin(); it != root_paths_.end();) {
		if (open_roots.count(it->first)) {
			++it;
		} else {
			it = root_paths_.erase(it);
		}
	}
}

SyncDocument *SyncEngine::resource_doc(const String &path, bool create_if_missing, const String &create_class) {
	ResourceLoader *loader = ResourceLoader::get_singleton();
	Ref<Resource> cached = loader->has_cached(path) ? loader->get_cached_ref(path) : Ref<Resource>();
	auto it = resources_.find(path);
	if (it != resources_.end()) {
		if (cached.is_null() || cached == it->second->root_resource()) return it->second.get();
		resources_.erase(it); // the file was reloaded: track the new instance
	}
	if (!create_if_missing || !is_safe_resource_path(path) || !is_resource_path(path)) return nullptr;

	Ref<Resource> res = cached;
	if (res.is_null() && FileAccess::file_exists(path)) res = loader->load(path);
	if (res.is_null() && !create_class.is_empty()) {
		ClassDBSingleton *db = ClassDBSingleton::get_singleton();
		if (db->class_exists(create_class) && db->is_parent_class(create_class, "Resource") &&
				db->can_instantiate(create_class) && !class_is_blocked(create_class, allow_builtin_scripts_)) {
			res = db->instantiate(create_class);
			if (res.is_valid()) res->take_over_path(path);
		}
	}
	if (res.is_null()) return nullptr;

	if (hooks_.before_document && FileAccess::file_exists(path)) hooks_.before_document(path);
	std::unique_ptr<SyncDocument> doc = SyncDocument::for_resource(path, res);
	doc->adopt();
	SyncDocument *raw = doc.get();
	resources_[path] = std::move(doc);
	return raw;
}

void SyncEngine::on_scene_saved(const String &path) {
	refresh_requested_ = true;
	refresh_open_scenes(true);
}

void SyncEngine::on_inspected(Object *object) {
	if (!active_) return;
	Resource *r = Object::cast_to<Resource>(object);
	if (r && is_external_resource(Ref<Resource>(r))) resource_doc(r->get_path(), true, String());
}

void SyncEngine::on_resource_saved(const Ref<Resource> &res) {
	if (!active_ || !is_external_resource(res)) return;
	String path = res->get_path();
	if (resources_.count(path)) return;
	SyncDocument *doc = resource_doc(path, true, String());
	if (!doc || assets_active_) return;
	std::vector<LocalOp> ops;
	doc->announce(ops);
	emit_all(ops);
}

void SyncEngine::revert(const String &doc, const Uuid &target, const String &key, const Variant &encoded) {
	if (SyncDocument *d = find_doc(doc)) d->revert_property(target, key, encoded);
}

String SyncEngine::describe(SyncDocument *doc, const RemoteOp &op) const {
	String name;
	if (doc) {
		if (Node *n = doc->node(op.target)) name = n->get_name();
	}
	if (name.is_empty()) name = op.payload.get("n", String());
	if (name.is_empty() && op.payload.has("prior")) name = Dictionary(op.payload["prior"]).get("n", String());
	if (name.is_empty()) name = op.doc.get_file().get_basename();

	if (op.payload.has("u") && op.payload["u"].get_type() == Variant::DICTIONARY) {
		String kind = Dictionary(op.payload["u"]).get("k", String("undo"));
		RemoteOp plain = op;
		plain.payload = op.payload.duplicate();
		plain.payload.erase("u");
		return (kind == "redo" ? String("redid: ") : String("undid: ")) + describe(doc, plain);
	}
	if (op.type == "CreateNode") return String("added ") + name;
	if (op.type == "DeleteNode") return String("deleted ") + name;
	if (op.type == "MoveNode") return String("moved ") + name;
	if (op.type == "RenameNode") return String("renamed ") + String(op.payload.get("on", name)) + String::utf8(" to ") + String(op.payload.get("n", String()));
	if (op.type == "ReorderNode") return String("reordered ") + name;
	if (op.type == "ChangeProperty") return String("changed ") + humanize(op.payload.get("k", String())) + " on " + name;
	if (op.type == "ChangeResourceProperty") return String("changed ") + humanize(op.payload.get("k", String())) + " in " + op.doc.get_file();
	if (op.type == "AddResource") return String("created ") + op.doc.get_file();
	if (op.type == "ChangeNodeType") return String("changed ") + name + " to " + String(op.payload.get("c", String()));
	if (op.type == "SetSceneRoot") return String("made ") + name + " the scene root";
	return String::utf8(op.type.c_str());
}

// Remote application

void SyncEngine::apply_batch(std::vector<RemoteOp> &ops) {
	if (!active_ || ops.empty()) return;
	refresh_open_scenes(true);
	// Edits made outside the undo history (e.g. shader code) that the idle scan
	// has not picked up yet must go out before the batch could overwrite them.
	if (now_ - last_idle_ >= kIdleQuiet) {
		last_idle_ = now_;
		run_idle_scan();
	}

	std::vector<String> order;
	std::map<String, std::vector<RemoteOp *>> groups;
	for (RemoteOp &op : ops) {
		auto &g = groups[op.doc];
		if (g.empty()) order.push_back(op.doc);
		g.push_back(&op);
	}
	for (const String &path : order) {
		std::vector<RemoteOp *> &group = groups[path];
		if (is_scene_path(path)) {
			apply_scene_ops(path, group);
		} else if (is_resource_path(path)) {
			apply_resource_ops(path, group);
		} else {
			if (hooks_.notice) hooks_.notice(String("Ignored change to unsupported file ") + path, 1);
			for (RemoteOp *op : group) hooks_.applied(path, op->seq, true);
		}
	}
}

int SyncEngine::apply_to_doc(SyncDocument &doc, std::vector<RemoteOp *> &ops, bool live, bool persisted) {
	uint64_t t0 = Time::get_singleton()->get_ticks_usec();
	ApplyContext ctx;
	ctx.allow_builtin_scripts = allow_builtin_scripts_;
	int64_t already = hooks_.doc_applied ? hooks_.doc_applied(doc.path()) : 0;
	for (RemoteOp *op : ops) {
		if (op->own && hooks_.own_committed) hooks_.own_committed(op->client_op_ref);
		// Sibling order is decided by the log alone: every client replays
		// create/move/reorder in sequence order, its own included.
		bool positional = op->type == "CreateNode" || op->type == "MoveNode" || op->type == "ReorderNode";
		bool skip = op->seq <= already;
		bool positional_only = false;
		if (!skip && op->own_applied && live) {
			if (positional) {
				positional_only = true;
			} else {
				skip = true;
			}
		}
		if (!skip && live && !op->own && !positional && hooks_.masked) {
			skip = hooks_.masked(doc.path(), op->target, op_key_for(op->type, op->payload));
		}
		if (skip) continue;
		if (positional_only) {
			doc.apply_position(op->type, op->target, op->payload, ctx);
			continue;
		}
		String subject = describe(&doc, *op);
		uint64_t op_t0 = debug_ ? Time::get_singleton()->get_ticks_usec() : 0;
		if (doc.apply(op->type, op->target, op->payload, ctx) && !op->own_applied && hooks_.activity) {
			hooks_.activity(*op, subject);
		}
		if (debug_) {
			uint64_t us = Time::get_singleton()->get_ticks_usec() - op_t0;
			if (us > 20000) UtilityFunctions::print("[yhde] slow op #", op->seq, " ", subject, " ", int64_t(us / 1000), "ms");
		}
	}
	uint64_t t1 = Time::get_singleton()->get_ticks_usec();
	doc.finish_batch(ctx);
	bury(ctx.detached, !live);
	if (debug_) {
		UtilityFunctions::print("[yhde] applied ", int64_t(ops.size()), " ops to ", doc.path(), live ? " (live)" : " (file)", " apply=",
				int64_t((t1 - t0) / 1000), "ms finish=", int64_t((Time::get_singleton()->get_ticks_usec() - t1) / 1000), "ms nodes=",
				int64_t(doc.node_count()), " res=", int64_t(doc.resource_count()));
	}
	if (hooks_.notice) {
		for (int64_t i = 0; i < ctx.errors.size(); i++) hooks_.notice(ctx.errors[i], 1);
	}
	(void)persisted;
	return ctx.applied + (ctx.structure_changed ? 1 : 0);
}

void SyncEngine::apply_scene_ops(const String &path, std::vector<RemoteOp *> &ops) {
	auto it = scenes_.find(path);
	if (it != scenes_.end() && it->second->root_node()) {
		SyncDocument &doc = *it->second;
		// A peer replaced the root of a scene open here. The editor offers no
		// way to swap an open tab's root in place, so: keep what is applied so
		// far, apply the rest to the file, and reload the tab from it.
		int64_t already = hooks_.doc_applied ? hooks_.doc_applied(path) : 0;
		size_t split = ops.size();
		for (size_t i = 0; i < ops.size(); i++) {
			const RemoteOp *op = ops[i];
			if (op->own_applied || op->seq <= already) continue;
			if (op->type == "SetSceneRoot" || (op->type == "ChangeNodeType" && op->target == doc.root_id())) {
				split = i;
				break;
			}
		}
		std::vector<RemoteOp *> live(ops.begin(), ops.begin() + long(split));
		int changed = live.empty() ? 0 : apply_to_doc(doc, live, true, false);
		if (split == ops.size()) {
			for (RemoteOp *op : ops) hooks_.applied(path, op->seq, false);
			if (changed > 0) {
				settle_at_[path] = now_ + kSettleDelay;
				schedule_instance_refresh(path);
				if (path == current_path_) EditorInterface::get_singleton()->mark_scene_as_unsaved();
			}
			return;
		}
		Ref<PackedScene> packed;
		packed.instantiate();
		if (packed->pack(doc.root_node()) != OK || ResourceSaver::get_singleton()->save(packed, path) != OK) {
			if (hooks_.notice) hooks_.notice(String("Could not save ") + path + " to apply a root change", 2);
			for (RemoteOp *op : ops) hooks_.applied(path, op->seq, false);
			return;
		}
		if (hooks_.file_written) hooks_.file_written(path);
		for (RemoteOp *op : live) hooks_.applied(path, op->seq, true);
		std::vector<RemoteOp *> rest(ops.begin() + long(split), ops.end());
		apply_to_file(path, rest);
		EditorInterface::get_singleton()->reload_scene_from_path(path);
		refresh_requested_ = true;
		return;
	}
	apply_to_file(path, ops);
}

void SyncEngine::apply_to_file(const String &path, std::vector<RemoteOp *> &ops) {
	// Not open: edit the file on disk (load -> apply -> save).
	Node *root = nullptr;
	if (!is_safe_resource_path(path)) {
		if (hooks_.notice) hooks_.notice(String("Refused unsafe path ") + path, 2);
	} else if (FileAccess::file_exists(path)) {
		Ref<PackedScene> scene = ResourceLoader::get_singleton()->load(path, "", ResourceLoader::CACHE_MODE_IGNORE);
		if (scene.is_valid()) root = scene->instantiate(PackedScene::GEN_EDIT_STATE_MAIN);
	} else {
		// A scene a peer just created: build it from its root.
		RemoteOp *first = ops.front();
		if (first->type == "CreateNode" && String(first->payload.get("p", String())).is_empty()) {
			String inst = first->payload.get("inst", String());
			String cls = first->payload.get("c", String());
			if (!inst.is_empty() && is_safe_resource_path(inst) && ResourceLoader::get_singleton()->exists(inst)) {
				Ref<PackedScene> base = ResourceLoader::get_singleton()->load(inst);
				if (base.is_valid()) root = base->instantiate(PackedScene::GEN_EDIT_STATE_MAIN_INHERITED);
			} else {
				ClassDBSingleton *db = ClassDBSingleton::get_singleton();
				if (db->class_exists(cls) && db->is_parent_class(cls, "Node") && db->can_instantiate(cls)) {
					root = Object::cast_to<Node>(db->instantiate(cls));
				}
			}
			if (root) {
				String name = first->payload.get("n", String());
				if (!name.is_empty()) root->set_name(name);
				root->set_meta(kIdMeta, first->target.to_string());
			}
		}
	}

	if (!root) {
		// The file is not in this project (yet): the changes stay in the log
		// and arrive with the file (e.g. through Git). Do not replay forever.
		if (hooks_.notice) hooks_.notice(String("Skipped changes to ") + path + " (not in this project)", 1);
		for (RemoteOp *op : ops) hooks_.applied(path, op->seq, true);
		return;
	}

	std::unique_ptr<SyncDocument> doc = SyncDocument::for_scene(path, root);
	doc->adopt();
	apply_to_doc(*doc, ops, false, true);
	// A root change may have replaced the root object.
	root = doc->root_node();

	// Open scenes that instance this one are packed before its cached scene
	// changes (so their overrides are known), then rebuilt from it.
	std::vector<InstanceTarget> targets = prepare_instance_refresh(path);
	ResourceLoader *loader = ResourceLoader::get_singleton();
	Ref<PackedScene> packed;
	if (loader->has_cached(path)) packed = loader->get_cached_ref(path);
	bool in_place = packed.is_valid();
	if (!in_place) packed.instantiate();
	Error err = packed->pack(root);
	if (err == OK) {
		if (!in_place) packed->take_over_path(path);
		err = ResourceSaver::get_singleton()->save(packed, path);
	}
	root->queue_free();
	if (err == OK && hooks_.file_written) hooks_.file_written(path);
	for (RemoteOp *op : ops) hooks_.applied(path, op->seq, err == OK);
	if (err != OK) {
		if (hooks_.notice) hooks_.notice(String("Could not save ") + path, 2);
		return;
	}
	if (EditorFileSystem *fs = EditorInterface::get_singleton()->get_resource_filesystem()) fs->update_file(path);
	finish_instance_refresh(targets);
}

void SyncEngine::on_file_replaced(const String &path) {
	if (!active_) return;
	ResourceLoader *loader = ResourceLoader::get_singleton();
	if (is_scene_path(path)) {
		if (Node *root = open_root(path)) {
			(void)root;
			// Its tab is rebuilt from the new file and adopted again.
			if (hooks_.reloading) hooks_.reloading(path);
			EditorInterface::get_singleton()->reload_scene_from_path(path);
			refresh_requested_ = true;
			return;
		}
		std::vector<InstanceTarget> targets = prepare_instance_refresh(path);
		if (loader->has_cached(path)) {
			Ref<PackedScene> cached = loader->get_cached_ref(path);
			Ref<PackedScene> fresh = loader->load(path, "", ResourceLoader::CACHE_MODE_IGNORE);
			if (cached.is_valid() && fresh.is_valid()) cached->copy_from_resource(fresh);
		}
		finish_instance_refresh(targets);
		return;
	}
	if (is_resource_path(path)) {
		auto it = resources_.find(path);
		if (it != resources_.end() && hooks_.reloading) hooks_.reloading(path);
		if (loader->has_cached(path)) {
			Ref<Resource> cached = loader->get_cached_ref(path);
			Ref<Resource> fresh = loader->load(path, "", ResourceLoader::CACHE_MODE_IGNORE);
			if (cached.is_valid() && fresh.is_valid() && cached->get_class() == fresh->get_class()) {
				cached->copy_from_resource(fresh);
				cached->emit_changed();
			}
		}
		if (it != resources_.end()) {
			// Track it again from the new content (a fresh baseline).
			resources_.erase(path);
			resource_doc(path, true, String());
		}
	}
}

// Live instances

void SyncEngine::schedule_instance_refresh(const String &source) {
	if (is_scene_path(source)) instance_refresh_at_[source] = now_ + kInstanceRefresh;
}

std::vector<SyncEngine::InstanceTarget> SyncEngine::prepare_instance_refresh(const String &source) {
	std::vector<InstanceTarget> out;
	ResourceLoader *loader = ResourceLoader::get_singleton();
	for (auto &kv : scenes_) {
		if (kv.first == source) continue;
		Node *root = kv.second->root_node();
		if (!root) continue;
		InstanceTarget t;
		t.scene = kv.first;
		Ref<PackedScene> own = loader->load(kv.first);
		if (own.is_valid() && inherits_from(own->get_state(), source)) {
			t.inherited = true;
		} else {
			std::function<void(Node *)> find = [&](Node *n) {
				for (int i = 0; i < n->get_child_count(); i++) {
					Node *c = n->get_child(i);
					if (c->get_owner() == root && !c->get_scene_file_path().is_empty() && contains_instance_of(c, source)) {
						t.paths.push_back(String(root->get_path_to(c)));
						continue; // rebuilt with everything under it
					}
					find(c);
				}
			};
			find(root);
		}
		if (!t.inherited && t.paths.empty()) continue;
		if (t.inherited) {
			out.push_back(std::move(t));
			continue;
		}
		t.packed.instantiate();
		if (t.packed->pack(root) != OK) {
			if (hooks_.notice) hooks_.notice(String("Could not refresh instances in ") + kv.first.get_file(), 1);
			continue;
		}
		out.push_back(std::move(t));
	}
	return out;
}

void SyncEngine::refresh_live_source(const String &source) {
	Node *src_root = live_root(source);
	if (!src_root) return;
	std::vector<InstanceTarget> targets = prepare_instance_refresh(source);
	if (targets.empty()) return;
	// The cached scene is updated in place: instances nested inside other
	// scenes (which hold this object) follow it too.
	Ref<PackedScene> cached = ResourceLoader::get_singleton()->load(source);
	if (cached.is_null() || cached->pack(src_root) != OK) return;
	finish_instance_refresh(targets);
}

void SyncEngine::finish_instance_refresh(std::vector<InstanceTarget> &targets) {
	for (InstanceTarget &t : targets) {
		auto it = scenes_.find(t.scene);
		if (it == scenes_.end()) continue;
		SyncDocument &doc = *it->second;
		Node *live = doc.root_node();
		if (!live) continue;
		if (t.inherited) {
			// Its root comes from the changed scene: reload the tab from its
			// file, the editor's own way (a fresh load everywhere gives the same
			// result; what was only in memory is replayed from the log).
			if (hooks_.reloading) hooks_.reloading(t.scene);
			EditorInterface::get_singleton()->reload_scene_from_path(t.scene);
			refresh_requested_ = true;
			continue;
		}
		Node *copy = t.packed->instantiate(PackedScene::GEN_EDIT_STATE_MAIN);
		if (!copy) continue;
		Ref<SceneState> state = t.packed->get_state();
		for (const String &p : t.paths) {
			Node *old = live->get_node_or_null(NodePath(p));
			Node *fresh = copy->get_node_or_null(NodePath(p));
			if (old && fresh) graft(live, old, fresh, state, p);
		}
		memdelete(copy);
		doc.rebind_objects();
		// New defaults inside the instances come from the scene that changed.
		PropFilter absorb = [](Object *, const StringName &) { return Verdict::Absorb; };
		std::vector<LocalOp> ops;
		doc.diff(ops, nullptr, &absorb);
		emit_group(ops, 0);
		if (debug_) UtilityFunctions::print("[yhde] refreshed ", int64_t(t.paths.size()), " instance(s) in ", t.scene);
	}
}

void SyncEngine::graft(Node *live, Node *old, Node *fresh, const Ref<SceneState> &state, const String &path) {
	Node *parent = old->get_parent();
	Node *copy_root = fresh->get_owner();
	int index = old->get_index();
	bool editable = live->is_editable_instance(old);

	// Keep what the user had selected or inspected inside it.
	std::vector<NodePath> reselect;
	NodePath reinspect;
	EditorInterface *ei = EditorInterface::get_singleton();
	EditorSelection *sel = ei ? ei->get_selection() : nullptr;
	if (sel) {
		TypedArray<Node> selected = sel->get_selected_nodes();
		for (int64_t i = 0; i < selected.size(); i++) {
			Node *n = Object::cast_to<Node>(selected[i]);
			if (n && (n == old || old->is_ancestor_of(n))) reselect.push_back(live->get_path_to(n));
		}
	}
	if (ei && ei->get_inspector()) {
		Node *n = Object::cast_to<Node>(ei->get_inspector()->get_edited_object());
		if (n && (n == old || old->is_ancestor_of(n))) reinspect = live->get_path_to(n);
	}
	SyncDocument::release_from_editor(old);

	fresh->get_parent()->remove_child(fresh);
	parent->remove_child(old);
	parent->add_child(fresh);
	parent->move_child(fresh, index);
	std::function<void(Node *)> adopt_owner = [&](Node *n) {
		Node *o = n->get_owner();
		if (!o || o == copy_root) n->set_owner(live);
		for (int i = 0; i < n->get_child_count(); i++) adopt_owner(n->get_child(i));
	};
	adopt_owner(fresh);
	if (editable) live->set_editable_instance(fresh, true);

	// Connections crossing the rebuilt subtree's border: re-made between the
	// live nodes (the copy's endpoints are discarded).
	String prefix = path + String("/");
	auto inside = [&](const String &p) { return p == path || p.begins_with(prefix); };
	for (int32_t i = 0; i < state->get_connection_count(); i++) {
		String src = state->get_connection_source(i);
		String tgt = state->get_connection_target(i);
		bool si = inside(src);
		bool ti = inside(tgt);
		if (si == ti) continue;
		Node *s = live->get_node_or_null(NodePath(src));
		Node *t = live->get_node_or_null(NodePath(tgt));
		if (!s || !t) continue;
		StringName sig = state->get_connection_signal(i);
		StringName method = state->get_connection_method(i);
		TypedArray<Dictionary> existing = s->get_signal_connection_list(sig);
		for (int64_t k = 0; k < existing.size(); k++) {
			Callable c = Dictionary(existing[k]).get("callable", Callable());
			Node *target = Object::cast_to<Node>(c.get_object());
			if (!target || String(c.get_method()) != String(method) || target == t) continue;
			bool stale = target == old || old->is_ancestor_of(target) || !(target == live || live->is_ancestor_of(target));
			if (stale) s->disconnect(sig, c);
		}
		Callable cb(t, method);
		int32_t unbinds = state->get_connection_unbinds(i);
		Array binds = state->get_connection_binds(i);
		if (unbinds > 0) {
			cb = cb.unbind(unbinds);
		} else if (!binds.is_empty()) {
			cb = cb.bindv(binds);
		}
		if (!s->is_connected(sig, cb)) s->connect(sig, cb, uint32_t(state->get_connection_flags(i)) | Object::CONNECT_PERSIST);
	}
	bury({ old->get_instance_id() }, false);

	if (sel) {
		for (const NodePath &p : reselect) {
			if (Node *n = live->get_node_or_null(p)) sel->add_node(n);
		}
	}
	if (!reinspect.is_empty()) {
		if (Node *n = live->get_node_or_null(reinspect)) ei->edit_node(n);
	}
}

void SyncEngine::apply_resource_ops(const String &path, std::vector<RemoteOp *> &ops) {
	String create_class;
	if (!ops.empty() && ops.front()->type == "AddResource") create_class = ops.front()->payload.get("c", String());
	SyncDocument *doc = resource_doc(path, true, create_class);
	if (!doc || doc->root_resource().is_null()) {
		if (hooks_.notice) hooks_.notice(String("Could not open ") + path + " to apply changes", 1);
		for (RemoteOp *op : ops) hooks_.applied(path, op->seq, false);
		return;
	}
	bool any_remote = false;
	for (RemoteOp *op : ops) any_remote = any_remote || !op->own_applied;
	apply_to_doc(*doc, ops, true, true);

	Error err = OK;
	if (any_remote) {
		err = ResourceSaver::get_singleton()->save(doc->root_resource(), path);
		if (err == OK && hooks_.file_written) hooks_.file_written(path);
		if (err == OK) {
			if (EditorFileSystem *fs = EditorInterface::get_singleton()->get_resource_filesystem()) fs->update_file(path);
		} else if (hooks_.notice) {
			hooks_.notice(String("Could not save ") + path, 2);
		}
	}
	for (RemoteOp *op : ops) hooks_.applied(path, op->seq, any_remote && err == OK);
}

} // namespace yhde
