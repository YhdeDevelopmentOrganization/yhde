#include "yhde_session.h"

#include "core/url.h"
#include "sync/variant_codec.h"

#include <godot_cpp/classes/control.hpp>
#include <godot_cpp/classes/dir_access.hpp>
#include <godot_cpp/classes/editor_inspector.hpp>
#include <godot_cpp/classes/editor_interface.hpp>
#include <godot_cpp/classes/editor_paths.hpp>
#include <godot_cpp/classes/project_settings.hpp>
#include <godot_cpp/classes/editor_selection.hpp>
#include <godot_cpp/classes/editor_undo_redo_manager.hpp>
#include <godot_cpp/classes/file_access.hpp>
#include <godot_cpp/classes/json.hpp>
#include <godot_cpp/classes/undo_redo.hpp>
#include <godot_cpp/variant/callable.hpp>
#include <godot_cpp/classes/node2d.hpp>
#include <godot_cpp/classes/node3d.hpp>
#include <godot_cpp/classes/os.hpp>
#include <godot_cpp/classes/sub_viewport.hpp>
#include <godot_cpp/classes/time.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/variant/callable_method_pointer.hpp>
#include <godot_cpp/variant/typed_array.hpp>
#include <godot_cpp/variant/utility_functions.hpp>

#include <algorithm>
#include <cmath>
#include <random>
#include <unordered_set>

namespace godot {

using yhde::Uuid;
namespace proto = yhde::proto;

namespace {

// A new server seeds one project and one branch (see
// server/src/YHDE.Server/Persistence/Migrations/001_initial_schema.sql).
const char *kDefaultUrl = "ws://127.0.0.1:5000/ws";
const char *kDefaultProject = "ffffffff-0000-0000-0000-000000000001";
const char *kDefaultBranch = "ffffffff-0000-0000-0000-000000000002";
// Keep in step with addons/yhde/plugin.cfg and the server's ServerInfo.Version.
const char *kAddonVersion = "0.6.6";
constexpr size_t kMaxSocialBody = 64 * 1024;

constexpr double kPresenceInterval = 1.0 / 15.0;
constexpr double kPresenceKeepAlive = 5.0;
constexpr double kSummonSeconds = 20.0; // how long a summon stays in presence
constexpr double kAckInterval = 1.0;
constexpr double kGapTimeout = 3.0;
constexpr size_t kMaxOpsPerFrame = 4000;
// Time for applying the log per frame: a few heavy operations must not freeze
// the editor. A catch-up (the person waits for it anyway) may take longer.
constexpr double kApplyBudgetLive = 0.008;
constexpr double kApplyBudgetSync = 0.05;
// Repeated re-syncs wait longer each time (with jitter, so many editors with
// the same gap do not all ask at once), up to this.
constexpr double kResyncBackoffMax = 30.0;
// Leaves room for the frame envelope under the server's message cap.
constexpr size_t kMaxPayloadBytes = proto::kMaxOutboundFrameBytes - 64 * 1024;
constexpr int kMaxSelection = 64;
constexpr int kMaxDrags = 24;

std::string to_std(const String &s) {
	CharString c = s.utf8();
	return std::string(c.get_data(), size_t(c.length()));
}

String from_std(const std::string &s) {
	return String::utf8(s.c_str(), int64_t(s.size()));
}

// Access keys go into an HTTP header: printable ASCII, no spaces.
bool valid_access_key(const String &key) {
	if (key.is_empty() || key.length() > 512) return false;
	for (int64_t i = 0; i < key.length(); i++) {
		char32_t c = key[i];
		if (c < 0x21 || c > 0x7E) return false;
	}
	return true;
}

void restrict_to_owner(const String &path, bool directory) {
	if (OS::get_singleton()->get_name() == "Windows") return; // per-user profile already
	BitField<FileAccess::UnixPermissionFlags> mode = 0;
	mode.set_flag(FileAccess::UNIX_READ_OWNER);
	mode.set_flag(FileAccess::UNIX_WRITE_OWNER);
	if (directory) mode.set_flag(FileAccess::UNIX_EXECUTE_OWNER);
	FileAccess::set_unix_permissions(path, mode);
}

Array floats(std::initializer_list<real_t> values) {
	Array a;
	for (real_t v : values) a.push_back(double(v));
	return a;
}

bool read_floats(const Variant &v, int offset, int count, double *out) {
	if (v.get_type() != Variant::ARRAY) return false;
	Array a = v;
	if (a.size() < offset + count) return false;
	for (int i = 0; i < count; i++) {
		Variant e = a[offset + i];
		if (e.get_type() != Variant::FLOAT && e.get_type() != Variant::INT) return false;
		out[i] = double(e);
		if (!std::isfinite(out[i])) return false;
	}
	return true;
}

Transform3D transform3d_from(const double *f) {
	Transform3D t;
	for (int r = 0; r < 3; r++) t.basis.rows[r] = Vector3(real_t(f[r * 3]), real_t(f[r * 3 + 1]), real_t(f[r * 3 + 2]));
	t.origin = Vector3(real_t(f[9]), real_t(f[10]), real_t(f[11]));
	return t;
}

Array transform3d_floats(const Transform3D &t) {
	Array a;
	for (int r = 0; r < 3; r++) {
		a.push_back(double(t.basis.rows[r].x));
		a.push_back(double(t.basis.rows[r].y));
		a.push_back(double(t.basis.rows[r].z));
	}
	a.push_back(double(t.origin.x));
	a.push_back(double(t.origin.y));
	a.push_back(double(t.origin.z));
	return a;
}

} // namespace

// Lifecycle

YhdeSession::YhdeSession() {
	set_process(true);
	// Run after every scene node (tool scripts, animations) so their per-frame
	// drift is absorbed before the next editor action can pick it up.
	set_process_priority(1 << 20);

	yhde::YhdeNetworking::Callbacks cb;
	cb.on_open = [this]() { on_open(); };
	cb.on_closed = [this](const String &reason) { on_closed(reason); };
	cb.on_frame = [this](const proto::Frame &f) { on_frame(f); };
	net_.set_callbacks(cb);

	yhde::SyncEngine::Hooks hooks;
	hooks.emit = [this](yhde::LocalOp &&op, uint64_t group) { emit_local(std::move(op), group); };
	hooks.masked = [this](const String &doc, const Uuid &target, const String &key) {
		return queue_.masks(doc, target, key);
	};
	hooks.applied = [this](const String &doc, int64_t seq, bool persisted) { cache_.note_applied(doc, seq, persisted); };
	hooks.notice = [this](const String &text, int level) { emit_notice(text, level); };
	hooks.persisted = [this](const String &doc) { cache_.note_saved(doc); };
	hooks.reloading = [this](const String &doc) { queue_.mark_not_applied(doc); };
	hooks.doc_applied = [this](const String &doc) { return cache_.doc_applied(doc); };
	hooks.own_committed = [this](const Uuid &ref) { queue_.take(ref, nullptr); };
	hooks.activity = [this](const yhde::RemoteOp &op, const String &subject) {
		String node = op.target.to_string();
		bool prop = op.type == "ChangeProperty" || op.type == "ChangeResourceProperty";
		String key;
		String where;
		if (prop) {
			key = String(op.payload.get("k", String())).trim_prefix("@").get_file().replace("_", " ");
			int cut = std::max(subject.rfind(" on "), subject.rfind(" in "));
			if (cut >= 0) where = subject.substr(cut);
		}
		for (ActivityItem &item : activity_batch_) {
			if (item.actor == op.actor && item.doc == op.doc && item.node == node) {
				item.changes++;
				item.properties_only = item.properties_only && prop;
				if (!prop) item.subject = subject;
				if (prop && !item.props.has(key) && item.props.size() < 16) item.props.push_back(key);
				return;
			}
		}
		if (activity_batch_.size() >= 64) return; // the feed is a summary, not a log
		ActivityItem item;
		item.actor = op.actor;
		item.doc = op.doc;
		item.subject = subject;
		item.node = node;
		item.where = where;
		if (prop) item.props.push_back(key);
		item.changes = 1;
		item.properties_only = prop;
		activity_batch_.push_back(item);
	};
	hooks.file_written = [this](const String &path) { asset_.absorb(path); };
	hooks.before_document = [this](const String &path) { asset_.ensure_registered(path); };
	activity_hook_ = hooks.activity;
	engine_.set_hooks(hooks);

	yhde::AssetSync::Hooks ah;
	ah.emit = [this](yhde::LocalOp &&op, const std::string &blob, bool before_doc) {
		return emit_local(std::move(op), 0, Uuid(), true, blob, before_doc);
	};
	ah.drop = [this](const Uuid &ref) { queue_.take(ref, nullptr); };
	ah.notice = [this](const String &text, int level) { emit_notice(text, level); };
	ah.is_document = [this](const String &path) { return engine_.find_doc(path) != nullptr; };
	ah.replaced = [this](const String &path, int64_t seq) {
		engine_.on_file_replaced(path);
		cache_.note_applied(path, seq, true);
	};
	ah.activity = [this](const yhde::RemoteOp &op, const String &subject) {
		if (activity_hook_) activity_hook_(op, subject);
	};
	asset_.set_hooks(ah);

	yhde::TextSync::Hooks th;
	th.emit = [this](yhde::LocalOp &&op) { return emit_local(std::move(op), 0); };
	th.shared = [this](const String &path, int64_t *seq) { return asset_.text_base(path, seq); };
	th.file_written = [this](const String &path) { asset_.absorb(path); };
	th.notice = [this](const String &text, int level) { emit_notice(text, level); };
	text_.set_hooks(th);
}

YhdeSession::~YhdeSession() {
	if (state_ != STATE_OFFLINE) {
		cache_.flush_if_dirty();
		queue_.flush_if_dirty();
		net_.stop();
	}
}

void YhdeSession::_notification(int what) {
	if (what == NOTIFICATION_EXIT_TREE && state_ != STATE_OFFLINE) stop();
}

double YhdeSession::now() const {
	return double(Time::get_singleton()->get_ticks_msec()) / 1000.0;
}

bool YhdeSession::start(const String &url, const String &project_id, const String &branch_id, const String &display_name) {
	if (state_ != STATE_OFFLINE) stop();

	String typed = url.strip_edges();
	// Godot refuses an uppercase scheme ("WS://"), so it is written in lowercase.
	String u = from_std(yhde::url::normalized(to_std(typed)));
	yhde::url::Parts parts = yhde::url::parse(to_std(u));
	if (!parts.ok || !(parts.scheme == "ws" || parts.scheme == "wss")) {
		emit_notice(String("The server address \"") + typed + "\" is not valid: it must look like wss://example.com/ws", NOTICE_ERROR);
		return false;
	}
	if (!Uuid::parse(project_id.strip_edges(), project_id_) || !Uuid::parse(branch_id.strip_edges(), branch_id_)) {
		emit_notice("Project and branch must be UUIDs", NOTICE_ERROR);
		return false;
	}
	EditorInterface *ei = EditorInterface::get_singleton();
	if (!ei) return false;

	String authorization;
	Dictionary keys = load_access_keys();
	String key = keys.get(u, keys.get(typed, String()));
	if (!key.is_empty()) {
		if (parts.scheme == "ws" && !yhde::url::is_loopback(to_std(u))) {
			// The key is a bearer token: over ws:// anyone on the way can read it.
			// YHDE_ALLOW_INSECURE_KEY=1 is for testing on a private network only.
			if (!OS::get_singleton()->has_environment("YHDE_ALLOW_INSECURE_KEY")) {
				emit_notice(String::utf8("Not connecting: your sign-in would travel unencrypted over ws:// to ") + from_std(parts.host) +
								". Use the server's wss:// address.",
						NOTICE_ERROR);
				return false;
			}
			emit_notice(String::utf8("The access key travels unencrypted over ws:// (YHDE_ALLOW_INSECURE_KEY is set)"), NOTICE_WARNING);
		}
		authorization = String("Bearer ") + key;
	}

	url_ = u;
	offline_reason_ = String();
	load_member_id();
	server_capabilities_.clear();
	server_version_.clear();
	display_name_ = display_name.strip_edges().substr(0, 48);
	if (display_name_.is_empty()) display_name_ = "Anonymous";

	storage_dir_ = ei->get_editor_paths()->get_project_settings_dir().path_join("yhde");
	asset_.start(to_std(u), to_std(authorization));
	switch_project(project_id_, branch_id_);
	engine_.set_assets_active(true);
	held_.clear();
	project_name_ = String();

	buffer_.clear();
	syncing_ = false;
	session_id_ = Uuid();
	presence_.clear();
	follow_target_ = String();
	last_presence_json_ = String();
	last_ack_sent_ = cache_.applied_seq();

	audit_ = OS::get_singleton()->has_environment("YHDE_AUDIT");
	debug_ = OS::get_singleton()->has_environment("YHDE_DEBUG");
	asset_.set_debug(debug_);
	audit_log_.clear();
	engine_.start();
	connect_editor_signals();
	net_.start(url_, authorization);
	set_state(STATE_CONNECTING);
	emit_signal("peers_changed");
	return true;
}

// Local state (cache, queue, files, live text) is kept per project and branch.
void YhdeSession::switch_project(const Uuid &project, const Uuid &branch) {
	project_id_ = project;
	branch_id_ = branch;
	String stem = project_id_.to_string() + "-" + branch_id_.to_string();
	String dir = storage_dir_;
	text_.flush_if_dirty();
	cache_.configure(dir.path_join(stem + ".cache.json"));
	cache_.load();
	fresh_join_ = cache_.applied_seq() == 0;
	join_pending_ = false;
	queue_.configure(dir.path_join(stem + ".queue.json"));
	queue_.load();
	if (!queue_.empty()) {
		emit_notice(String::num_int64(int64_t(queue_.size())) + " unsent change(s) from last time will be sent", NOTICE_INFO);
	}
	asset_.set_project(to_std(project_id_.to_string()));
	asset_.configure(dir.path_join(stem + ".assets.json"));
	asset_.load();
	if (asset_.state_lost()) {
		// Without it every file here would look like a new local change:
		// start over as a first join, which asks before anything is shared.
		emit_notice("YHDE's record of this project's files was damaged, so it checks them again as on a first join.", NOTICE_WARNING);
		cache_.reset();
		fresh_join_ = true;
	}
	for (const yhde::PendingOp &op : queue_.ops()) {
		if (!yhde::AssetSync::is_asset_op(op.type)) continue;
		Variant payload;
		if (yhde::from_json(from_std(op.payload_json), payload) && payload.get_type() == Variant::DICTIONARY) {
			asset_.restore_pending(op.type, payload, op.client_op_ref);
		}
	}
	asset_.queue_offline_changes();
	text_.configure(dir.path_join(stem + ".text"));
	last_ack_sent_ = cache_.applied_seq();
}

void YhdeSession::confirm_join() {
	if (!join_pending_) return;
	join_pending_ = false;
	fresh_join_ = false;
	drain();
	send_pending();
}

// Before a folder's first sync with a project: does it look like that game?
// (Files it has that the project has too, against what it has in total.)
bool YhdeSession::check_join() {
	std::set<String> project_files;
	for (const auto &kv : buffer_) {
		const proto::CommittedOp &op = kv.second;
		if (!yhde::AssetSync::is_asset_op(op.type)) continue;
		Variant v;
		if (!yhde::from_json(from_std(op.payload_json), v) || v.get_type() != Variant::DICTIONARY) continue;
		Dictionary p = v;
		String s = p.get("s", String());
		if (op.type == "DeleteAsset") {
			project_files.erase(s);
		} else {
			project_files.insert(s);
			if (op.type == "MoveAsset") project_files.erase(String(p.get("f", String())));
		}
	}
	if (project_files.empty()) return true; // a new project: this folder fills it
	int64_t local = 0;
	int64_t shared = 0;
	std::vector<String> dirs = { "res://" };
	while (!dirs.empty() && local < 20000) {
		String dir = dirs.back();
		dirs.pop_back();
		PackedStringArray sub = DirAccess::get_directories_at(dir);
		for (int64_t i = 0; i < sub.size(); i++) {
			if (sub[i].begins_with(".") || (dir == "res://" && sub[i] == "addons")) continue;
			dirs.push_back(dir.path_join(sub[i]));
		}
		PackedStringArray files = DirAccess::get_files_at(dir);
		for (int64_t i = 0; i < files.size(); i++) {
			String path = dir.path_join(files[i]);
			if (path == "res://project.godot" || path.ends_with(".import") || path.ends_with(".uid") || yhde::AssetSync::excluded(path)) continue;
			local++;
			if (project_files.count(path)) shared++;
		}
	}
	if (local < 8 || shared * 2 >= local) return true;
	Dictionary info;
	info["project"] = project_name_;
	info["local_files"] = local;
	info["project_files"] = int64_t(project_files.size());
	info["shared_files"] = shared;
	join_pending_ = true;
	emit_signal("join_check", info);
	return false;
}

void YhdeSession::stop() {
	if (state_ == STATE_OFFLINE) return;
	text_.flush_if_dirty();
	text_.unbind_all();
	join_pending_ = false;
	send_ack(true);
	net_.stop();
	engine_.stop();
	asset_.stop();
	held_.clear();
	disconnect_editor_signals();
	cache_.flush_if_dirty();
	queue_.flush_if_dirty();
	presence_.clear();
	buffer_.clear();
	follow_target_ = String();
	set_state(STATE_OFFLINE);
	emit_signal("peers_changed");
	emit_signal("presence_changed");
}

void YhdeSession::connect_editor_signals() {
	if (signals_connected_) return;
	EditorInterface *ei = EditorInterface::get_singleton();
	if (EditorUndoRedoManager *ur = ei->get_editor_undo_redo()) {
		ur->connect("history_changed", callable_mp(this, &YhdeSession::on_manager_history_changed));
		ur->connect("version_changed", callable_mp(this, &YhdeSession::on_manager_version_changed));
	}
	if (EditorInspector *inspector = ei->get_inspector()) {
		inspector->connect("edited_object_changed", callable_mp(this, &YhdeSession::on_inspected_changed));
	}
	signals_connected_ = true;
	watch_scene_histories();
}

void YhdeSession::disconnect_editor_signals() {
	if (!signals_connected_) return;
	EditorInterface *ei = EditorInterface::get_singleton();
	if (ei) {
		if (EditorUndoRedoManager *ur = ei->get_editor_undo_redo()) {
			ur->disconnect("history_changed", callable_mp(this, &YhdeSession::on_manager_history_changed));
			ur->disconnect("version_changed", callable_mp(this, &YhdeSession::on_manager_version_changed));
		}
		if (EditorInspector *inspector = ei->get_inspector()) {
			inspector->disconnect("edited_object_changed", callable_mp(this, &YhdeSession::on_inspected_changed));
		}
	}
	for (auto &kv : histories_) {
		UndoRedo *h = Object::cast_to<UndoRedo>(ObjectDB::get_instance(kv.second.object));
		Callable cb = callable_mp(this, &YhdeSession::on_history_event).bind(kv.first);
		if (h && h->is_connected("version_changed", cb)) h->disconnect("version_changed", cb);
	}
	histories_.clear();
	history_paths_.clear();
	groups_.clear();
	signals_connected_ = false;
}

void YhdeSession::watch_scene_histories(bool look_for_new) {
	EditorInterface *ei = EditorInterface::get_singleton();
	EditorUndoRedoManager *ur = ei ? ei->get_editor_undo_redo() : nullptr;
	if (!ur) return;
	std::vector<int64_t> ids;
	std::vector<int64_t> open;
	ids.push_back(EditorUndoRedoManager::GLOBAL_HISTORY);
	open.push_back(EditorUndoRedoManager::GLOBAL_HISTORY);
	history_paths_.clear();
	const String front = engine_.current_scene_path();
	for (const String &path : engine_.live_scene_paths()) {
		Node *root = engine_.live_root(path);
		if (!root) continue;
		int64_t id = ur->get_object_history_id(root);
		history_paths_[id] = path;
		open.push_back(id);
		// Godot makes a scene's history on the first action in it, and asking
		// for one it hasn't made logs an error. Actions happen in the scene in
		// front, so new histories are looked for only there.
		if (histories_.count(id) || (look_for_new && path == front)) ids.push_back(id);
	}
	// A closed scene's history is gone: stop watching it.
	for (auto it = histories_.begin(); it != histories_.end();) {
		if (std::find(open.begin(), open.end(), it->first) != open.end()) {
			++it;
			continue;
		}
		if (UndoRedo *old = Object::cast_to<UndoRedo>(ObjectDB::get_instance(it->second.object))) {
			Callable cb = callable_mp(this, &YhdeSession::on_history_event).bind(it->first);
			if (old->is_connected("version_changed", cb)) old->disconnect("version_changed", cb);
		}
		it = histories_.erase(it);
	}
	for (int64_t id : ids) {
		UndoRedo *h = ur->get_history_undo_redo(int32_t(id));
		if (!h) continue;
		auto it = histories_.find(id);
		if (it != histories_.end() && it->second.object == h->get_instance_id()) continue;
		// A history seen for the first time: its existing actions predate us.
		HistoryState hs;
		hs.object = h->get_instance_id();
		hs.version = h->get_version();
		hs.count = h->get_history_count();
		hs.current = h->get_current_action();
		hs.actions.resize(size_t(std::max(0, hs.count)));
		histories_[id] = std::move(hs);
		h->connect("version_changed", callable_mp(this, &YhdeSession::on_history_event).bind(id));
	}
}

std::vector<String> YhdeSession::docs_for_history(int64_t history_id) const {
	std::vector<String> out;
	auto it = history_paths_.find(history_id);
	if (it != history_paths_.end()) {
		out.push_back(it->second);
	} else if (!engine_.current_scene_path().is_empty()) {
		// Global history (resources, project-wide edits): the scene in front.
		out.push_back(engine_.current_scene_path());
	}
	return out;
}

void YhdeSession::on_history_event(int64_t history_id) {
	handle_history_event(history_id, false);
}

void YhdeSession::on_manager_history_changed() {
	catch_up_histories(true);
}

// Undo and redo never make a new history: only the known ones are checked.
void YhdeSession::on_manager_version_changed() {
	catch_up_histories(false);
}

void YhdeSession::catch_up_histories(bool look_for_new) {
	if (state_ == STATE_OFFLINE) return;
	// New scenes get their history lazily; connect before anything is missed,
	// and catch up with any history that changed without telling us.
	watch_scene_histories(look_for_new);
	EditorUndoRedoManager *ur = EditorInterface::get_singleton()->get_editor_undo_redo();
	for (auto &kv : histories_) {
		UndoRedo *h = ur->get_history_undo_redo(int32_t(kv.first));
		if (h && h->get_instance_id() == kv.second.object && h->get_version() != kv.second.version) {
			handle_history_event(kv.first, true);
		}
	}
}

YhdeSession::ActionRecord *YhdeSession::record_for(uint64_t group, bool *undo) {
	auto g = groups_.find(group);
	if (g == groups_.end()) return nullptr;
	auto h = histories_.find(g->second.history);
	if (h == histories_.end()) return nullptr;
	// Indices shift when the history drops its oldest steps: find by group.
	for (ActionRecord &rec : h->second.actions) {
		if (rec.group == group || std::find(rec.aliases.begin(), rec.aliases.end(), group) != rec.aliases.end()) {
			if (undo) *undo = g->second.undo;
			return &rec;
		}
	}
	return nullptr;
}

void YhdeSession::handle_history_event(int64_t history_id, bool missed) {
	if (state_ == STATE_OFFLINE || !engine_.active()) return;
	EditorUndoRedoManager *ur = EditorInterface::get_singleton()->get_editor_undo_redo();
	UndoRedo *h = ur ? ur->get_history_undo_redo(int32_t(history_id)) : nullptr;
	auto it = histories_.find(history_id);
	if (!h || it == histories_.end() || it->second.object != h->get_instance_id()) return;
	HistoryState &hs = it->second;

	bool committing = h->is_committing_action();
	int count = h->get_history_count();
	int current = h->get_current_action();
	int prev_count = hs.count;
	int prev_current = hs.current;
	hs.version = h->get_version();
	hs.count = count;
	hs.current = current;
	if (OS::get_singleton()->has_environment("YHDE_DEBUG")) {
		UtilityFunctions::print("[yhde] history ", history_id, committing ? " commit" : " change", " ", prev_current, "->", current,
				" of ", count, missed ? " (missed)" : "");
	}

	std::vector<String> docs = docs_for_history(history_id);
	if (count <= 0) {
		hs.actions.clear();
	}

	if (committing && !missed) {
		bool merge = count == prev_count && current == prev_current && current >= 0 && size_t(current) < hs.actions.size();
		if (!merge) {
			// A new action replaces anything that could have been redone.
			hs.actions.resize(size_t(std::max(0, current)));
			ActionRecord rec;
			rec.group = next_group_++;
			hs.actions.push_back(std::move(rec));
		}
		ActionRecord &rec = hs.actions[size_t(current)];
		last_group_ = rec.group;
		GroupRef ref;
		ref.history = history_id;
		ref.index = size_t(current);
		groups_[rec.group] = ref;
		std::vector<yhde::LocalOp> ops = engine_.scan(docs, yhde::ScanKind::Action);
		engine_.emit_group(ops, rec.group);
		engine_.schedule_followup(docs, rec.group);
	} else if (!missed && count == prev_count && (current == prev_current - 1 || current == prev_current + 1)) {
		// Undo or redo of one action: the server undoes it (operation_system.md).
		// The editor's own undo already shows the result; the inverse is
		// computed from what the action sent, so it is exact even when others
		// changed the scene since (or the editor's undo could not do it all).
		bool is_undo = current == prev_current - 1;
		size_t index = size_t(is_undo ? prev_current : current);
		ActionRecord *rec = index < hs.actions.size() ? &hs.actions[index] : nullptr;
		const std::vector<JournalOp> *source = nullptr;
		if (rec) source = is_undo ? (rec->is_undone ? nullptr : &rec->done) : (rec->is_undone ? &rec->undone : nullptr);
		if (OS::get_singleton()->has_environment("YHDE_DEBUG")) {
			String what;
			if (source) {
				for (const JournalOp &j : *source) what += String(" ") + String::utf8(j.op.type.c_str()) + ":" + j.op.key;
			}
			UtilityFunctions::print("[yhde] ", is_undo ? "undo" : "redo", " action #", int64_t(index), " (", int64_t(hs.actions.size()), " journaled)",
					rec ? String(" group ") + String::num_int64(int64_t(rec->group)) : String(" no record"), what);
		}
		if (source && !source->empty()) {
			std::vector<yhde::LocalOp> group;
			std::vector<Uuid> undo_of;
			for (const JournalOp &j : *source) {
				group.push_back(j.op);
				undo_of.push_back(j.op_id);
			}
			std::vector<yhde::LocalOp> ops = engine_.revert(group);
			uint64_t g = next_group_++;
			rec->aliases.push_back(g);
			GroupRef ref;
			ref.history = history_id;
			ref.index = index;
			ref.undo = is_undo;
			groups_[g] = ref;
			(is_undo ? rec->undone : rec->done).clear();
			rec->is_undone = is_undo;
			submit_undo(ops, g, undo_of, is_undo ? "undo" : "redo");
			engine_.schedule_followup(docs, g);
		} else {
			// An action from before we connected: send what changed.
			std::vector<yhde::LocalOp> ops = engine_.scan(docs, yhde::ScanKind::Action);
			engine_.emit_group(ops, 0);
			engine_.schedule_followup(docs, 0);
		}
	} else {
		// A change we could not attribute (history cleared, missed events).
		std::vector<yhde::LocalOp> ops = engine_.scan(docs, yhde::ScanKind::Action);
		engine_.emit_group(ops, 0);
		engine_.schedule_followup(docs, 0);
		if (missed || count != prev_count) {
			hs.actions.clear();
			hs.actions.resize(size_t(std::max(0, count)));
		}
	}
	// Keep the journal the size of the history (it drops its oldest steps).
	if (count >= 0 && hs.actions.size() > size_t(count)) {
		hs.actions.erase(hs.actions.begin(), hs.actions.begin() + long(hs.actions.size() - size_t(count)));
		for (auto &g : groups_) {
			if (g.second.history == history_id) g.second.index = 0; // indices shifted; stale refs are validated by group
		}
	}
}

void YhdeSession::on_inspected_changed() {
	if (EditorInterface *ei = EditorInterface::get_singleton()) {
		if (EditorInspector *inspector = ei->get_inspector()) engine_.on_inspected(inspector->get_edited_object());
	}
}

void YhdeSession::set_state(State s) {
	String text = get_status_text();
	if (s == state_ && text == last_status_text_) return;
	state_ = s;
	last_status_text_ = get_status_text();
	emit_signal("state_changed", int(state_), last_status_text_);
}

void YhdeSession::emit_notice(const String &text, int level) {
	// Never flood the editor: identical notices are shown once per 10 s and at
	// most a handful per second.
	double t = now();
	auto it = recent_notices_.find(text);
	if (it != recent_notices_.end() && t - it->second < 10.0) return;
	if (t - notice_window_ >= 1.0) {
		notice_window_ = t;
		notices_this_second_ = 0;
	}
	if (++notices_this_second_ > 4) return;
	if (recent_notices_.size() > 256) recent_notices_.clear();
	recent_notices_[text] = t;
	if (level >= NOTICE_WARNING) UtilityFunctions::push_warning("YHDE: ", text);
	emit_signal("notice", text, level);
}

// Frame loop

void YhdeSession::_process(double delta) {
	if (state_ == STATE_OFFLINE) return;
	double t = now();
	net_.poll(t);
	if (net_.state() == yhde::YhdeNetworking::State::Refused) {
		// Retrying can't help until the person signs in again or gets a new code.
		offline_reason_ = "Sign-in or invite code refused";
		stop();
		emit_notice("The server did not accept your sign-in or invite code. Sign in again in the YHDE panel, or ask your team for a new invite link.", NOTICE_ERROR);
		return;
	}
	drain();
	asset_.process(t);
	text_.process(t);
	if (!held_.empty() && asset_.take_progress()) {
		std::vector<proto::CommittedOp> none;
		process_committed(none);
	}
	check_caught_up();
	engine_.process(t);
	if (t - last_history_watch_ >= 0.5) {
		last_history_watch_ = t;
		watch_scene_histories();
	}
	handle_closed_docs();
	if (resync_requested_ && state_ == STATE_LIVE) {
		resync_requested_ = false;
		subscribe(false);
	}
	if (resend_at_ >= 0 && t >= resend_at_ && state_ == STATE_LIVE) {
		resend_at_ = -1.0;
		queue_.mark_all_unsent();
	}
	send_pending();
	tick_presence();
	send_ack(false);

	if (t - last_flush_ >= 0.5) {
		last_flush_ = t;
		cache_.flush_if_dirty();
		queue_.flush_if_dirty();
		asset_.flush_if_dirty();
		text_.flush_if_dirty();
	}

	// The log is ahead of what was applied, yet nothing is buffered or held:
	// an operation went missing on the way. Re-sync instead of waiting forever.
	if (buffer_.empty() && held_.empty() && !syncing_ && !join_pending_ && state_ == STATE_LIVE && head_seq_ > cache_.applied_seq()) {
		if (stalled_since_ < 0) stalled_since_ = t;
		if (t - stalled_since_ > kGapTimeout * 2 && t >= resync_not_before_) {
			stalled_since_ = -1.0;
			if (debug_) UtilityFunctions::print("[yhde] applied ", cache_.applied_seq(), " < head ", head_seq_, " with nothing pending: re-syncing");
			resync_after_gap(t);
		}
	} else {
		stalled_since_ = -1.0;
	}
	// A hole in the sequence that does not fill in: re-sync from our watermark.
	if (!buffer_.empty() && !syncing_ && buffer_.begin()->first > next_seq_) {
		if (gap_since_ < 0) gap_since_ = t;
		if (t - gap_since_ > kGapTimeout && t >= resync_not_before_) {
			gap_since_ = -1.0;
			resync_after_gap(t);
		}
	} else {
		gap_since_ = -1.0;
	}
	// A minute live without a gap: the next re-sync is quick again.
	if (state_ == STATE_LIVE && gap_since_ < 0 && stalled_since_ < 0) {
		if (quiet_since_ < 0) quiet_since_ = t;
		if (t - quiet_since_ > 60.0) resyncs_in_a_row_ = 0;
	} else {
		quiet_since_ = -1.0;
	}

	if (state_ == STATE_RECONNECTING || state_ == STATE_CONNECTING || state_ == STATE_SYNCING) set_state(state_);
}

void YhdeSession::on_open() {
	proto::Hello hello;
	hello.capabilities = { "ops", "presence", "assets", "social" };
	hello.member_id = member_id_;
	hello.display_name = to_std(display_name_);
	hello.client_version = kAddonVersion;
	proto::Frame f;
	f.type = proto::MsgType::Hello;
	f.channel = proto::Channel::System;
	f.payload = proto::encode(hello);
	net_.send(f);
}

void YhdeSession::on_closed(const String &reason) {
	syncing_ = false;
	buffer_.clear();
	presence_.clear();
	queue_.mark_all_unsent();
	set_state(STATE_RECONNECTING);
	emit_signal("peers_changed");
	emit_signal("presence_changed");
}

void YhdeSession::subscribe(bool fresh_connection) {
	int64_t from = cache_.resume_seq();
	next_seq_ = from + 1;
	buffer_.clear();
	held_.clear();
	asset_.clear_incoming();
	syncing_ = true;
	// On a new connection nothing sent before arrived for sure: send it all
	// again. A re-sync on the same connection keeps what is on its way (the
	// server answers each one; sending them again only wasted traffic).
	if (fresh_connection) queue_.mark_all_unsent();
	proto::Subscribe sub;
	sub.project_id = project_id_;
	sub.branch_id = branch_id_;
	sub.last_acked_seq = from;
	proto::Frame f;
	f.type = proto::MsgType::Subscribe;
	f.channel = proto::Channel::Ops;
	f.payload = proto::encode(sub);
	net_.send(f);
	set_state(STATE_SYNCING);
}

void YhdeSession::on_frame(const proto::Frame &frame) {
	switch (frame.type) {
		case proto::MsgType::Welcome: {
			proto::Welcome w;
			if (!proto::decode(frame.payload, w)) return;
			session_id_ = w.session_id;
			server_capabilities_ = w.server_capabilities;
			server_version_ = w.server_version;
			project_name_ = from_std(w.project_name);
			if (!w.project_id.is_nil() && !w.branch_id.is_nil() && (w.project_id != project_id_ || w.branch_id != branch_id_)) {
				// The invite code decides the project.
				switch_project(w.project_id, w.branch_id);
			}
			if (!w.project_id.is_nil()) {
				emit_signal("project_joined", w.project_id.to_string(), w.branch_id.to_string(), project_name_);
			}
			presence_.set_self(session_id_);
			subscribe();
			break;
		}
		case proto::MsgType::Error: {
			proto::Error e;
			if (!proto::decode(frame.payload, e)) return;
			if (e.retryable) {
				// A passing problem on the server: what was sent goes again shortly.
				if (resend_at_ < 0) resend_at_ = now() + 3.0;
				if (debug_) UtilityFunctions::print("[yhde] server: ", from_std(e.message), " (will resend)");
				break;
			}
			emit_notice(String("Server: ") + from_std(e.message), NOTICE_ERROR);
			break;
		}
		case proto::MsgType::SyncState: {
			proto::SyncState s;
			if (!proto::decode(frame.payload, s)) {
				net_.restart("Unreadable catch-up page");
				return;
			}
			head_seq_ = s.head_seq;
			catchup_head_ = s.head_seq;
			if (s.head_seq < cache_.applied_seq()) {
				// The server's log is behind what we have seen: it was reset.
				emit_notice("The server history was reset; re-synchronizing from scratch", NOTICE_WARNING);
				cache_.reset();
				queue_.clear();
				asset_.reset();
				subscribe();
				return;
			}
			for (proto::CommittedOp &op : s.tail) {
				if (op.seq >= next_seq_) buffer_[op.seq] = std::move(op);
			}
			if (!s.has_more) {
				syncing_ = false;
				if (fresh_join_ && !join_pending_ && !check_join()) {
					set_state(STATE_LIVE);
					return; // held until someone confirms this is the right folder
				}
				fresh_join_ = false;
				drain();
				set_state(STATE_LIVE);
				send_pending();
				last_presence_json_ = String(); // announce ourselves right away
				tick_presence();
			}
			break;
		}
		case proto::MsgType::OpCommitted: {
			proto::CommittedOp op;
			if (!proto::decode(frame.payload, op)) return;
			head_seq_ = std::max(head_seq_, op.seq);
			if (op.seq >= next_seq_) buffer_[op.seq] = std::move(op);
			break;
		}
		case proto::MsgType::OpRejected: {
			proto::OpRejected r;
			if (proto::decode(frame.payload, r)) on_rejected(r);
			break;
		}
		case proto::MsgType::UndoResult: {
			proto::UndoResult r;
			if (proto::decode(frame.payload, r)) on_undo_result(r);
			break;
		}
		case proto::MsgType::SocialEvent: {
			proto::SocialEvent e;
			if (!proto::decode(frame.payload, e)) return;
			Variant body;
			if (!yhde::from_json(from_std(e.body_json), body) || body.get_type() != Variant::DICTIONARY) return;
			emit_signal("social_event", from_std(e.kind), body, e.request_id.is_nil() ? String() : e.request_id.to_string());
			break;
		}
		case proto::MsgType::PresenceState: {
			proto::PresenceState ps;
			if (!proto::decode(frame.payload, ps)) return;
			bool roster = presence_.apply(ps, now());
			if (!follow_target_.is_empty() && !presence_.find(Uuid::parse_or_nil(follow_target_))) {
				follow_target_ = String();
				roster = true;
			}
			if (roster) emit_signal("peers_changed");
			emit_signal("presence_changed");
			break;
		}
		default:
			break;
	}
}

void YhdeSession::drain() {
	if (buffer_.empty() || join_pending_) return;
	// Drop anything we already processed.
	while (!buffer_.empty() && buffer_.begin()->first < next_seq_) buffer_.erase(buffer_.begin());
	std::vector<proto::CommittedOp> ready;
	while (!buffer_.empty() && buffer_.begin()->first == next_seq_ && ready.size() < ops_per_frame_) {
		ready.push_back(std::move(buffer_.begin()->second));
		buffer_.erase(buffer_.begin());
		next_seq_++;
	}
	if (ready.empty()) return;
	size_t count = ready.size();
	double started = double(Time::get_singleton()->get_ticks_usec()) / 1e6;
	process_committed(ready);
	double took = double(Time::get_singleton()->get_ticks_usec()) / 1e6 - started;
	// Fit the next frame's share to the time budget: halve after a frame that
	// took too long, grow again while frames stay well inside it.
	double budget = syncing_ ? kApplyBudgetSync : kApplyBudgetLive;
	if (took > budget && ops_per_frame_ > 8) {
		ops_per_frame_ = std::max<size_t>(8, ops_per_frame_ / 2);
	} else if (took < budget / 4 && count >= ops_per_frame_) {
		ops_per_frame_ = std::min(kMaxOpsPerFrame, ops_per_frame_ * 2);
	}
	if (debug_ && took > budget) UtilityFunctions::print("[yhde] applied ", int64_t(count), " ops in ", int64_t(took * 1000), " ms; next frame takes ", int64_t(ops_per_frame_));
}

void YhdeSession::resync_after_gap(double now) {
	// 3 s, 6 s, 12 s ... up to 30 s between re-syncs that follow each other,
	// each with up to a third more at random.
	static std::mt19937 rng{ std::random_device{}() };
	double wait = std::min(kResyncBackoffMax, kGapTimeout * double(1 << std::min(resyncs_in_a_row_, 4)));
	wait *= 1.0 + std::uniform_real_distribution<double>(0.0, 0.33)(rng);
	resync_not_before_ = now + wait;
	resyncs_in_a_row_++;
	subscribe(false);
}

bool YhdeSession::parse_committed(const proto::CommittedOp &op, yhde::RemoteOp &r) {
	Variant payload;
	if (!yhde::from_json(from_std(op.payload_json), payload) || payload.get_type() != Variant::DICTIONARY) {
		emit_notice(String("Skipped an unreadable operation #") + String::num_int64(op.seq), NOTICE_WARNING);
		return false;
	}
	r.seq = op.seq;
	r.type = op.type;
	r.target = op.target_id;
	r.payload = payload;
	r.doc = r.payload.get("s", String());
	r.client_op_ref = op.client_op_ref;
	if (yhde::PendingOp *mine = queue_.find(op.client_op_ref)) {
		r.own = true;
		r.own_applied = mine->applied_locally;
		r.actor = display_name_;
	} else {
		String who = presence_.name_of(op.session_id);
		r.actor = who.is_empty() ? String("Someone") : who;
	}
	return !r.doc.is_empty();
}

void YhdeSession::process_committed(std::vector<proto::CommittedOp> &ops) {
	// The log's picture of the project's files moves on as soon as an asset
	// operation is seen, even when its bytes are still on their way.
	for (const proto::CommittedOp &op : ops) {
		if (!yhde::AssetSync::is_asset_op(op.type)) continue;
		yhde::RemoteOp r;
		if (parse_committed(op, r)) asset_.note_committed(r);
	}
	std::deque<proto::CommittedOp> todo;
	todo.swap(held_);
	for (proto::CommittedOp &op : ops) todo.push_back(std::move(op));

	// Operations wait behind an asset operation whose bytes are not here yet
	// when they touch the same file or refer to it (a texture assigned right
	// after it was added); everything else goes ahead.
	// A set, and each operation's text is read once: with thousands of files
	// on their way (a big import), comparing every operation with every
	// waiting file took seconds per frame.
	std::unordered_set<std::string> blocked;
	auto block = [&](const String &path) {
		if (!path.is_empty()) blocked.insert(to_std(yhde::AssetSync::key_of(path)));
	};
	auto is_blocked = [&](const String &path) {
		return blocked.count(to_std(yhde::AssetSync::key_of(path))) > 0;
	};
	// Whether the JSON names a waiting file: "res://x" or "res://x::sub".
	auto refers = [&](const std::string &json) {
		for (size_t at = json.find("\"res://"); at != std::string::npos; at = json.find("\"res://", at + 1)) {
			size_t end = json.find('"', at + 1);
			if (end == std::string::npos) break;
			std::string s = json.substr(at + 1, end - at - 1);
			if (blocked.count(s)) return true;
			size_t sub = s.find("::");
			if (sub != std::string::npos && blocked.count(s.substr(0, sub))) return true;
		}
		return false;
	};

	std::vector<yhde::RemoteOp> batch;
	bool catching_up = syncing_ || (!todo.empty() && todo.back().seq <= catchup_head_);
	auto flush = [&]() {
		if (batch.empty()) return;
		engine_.apply_batch(batch);
		// Own ops the engine did not route (unsupported/unopenable documents).
		for (const yhde::RemoteOp &r : batch) {
			if (r.own) queue_.take(r.client_op_ref, nullptr);
		}
		batch.clear();
	};
	// Files written this round are registered with the editor and imported
	// before any later scene edit is applied: an edit that puts a just-added
	// model in a scene must find it imported, or the editor reports errors
	// (and the scene only looks right after a reload).
	bool assets_unsettled = false;
	auto settle_assets = [&]() {
		if (!assets_unsettled) return;
		asset_.finish_batch();
		assets_unsettled = false;
	};
	int64_t last = cache_.applied_seq();
	for (proto::CommittedOp &op : todo) {
		yhde::RemoteOp r;
		if (!parse_committed(op, r)) {
			last = std::max(last, op.seq);
			continue;
		}
		if (yhde::AssetSync::is_asset_op(r.type)) {
			String from = r.payload.get("f", String());
			bool hold = is_blocked(r.doc) || (!from.is_empty() && is_blocked(from));
			// A file waits for the files it needs (a model for its textures).
			PackedStringArray deps = yhde::AssetSync::dependencies_of(r.payload);
			for (int64_t i = 0; i < deps.size() && !hold; i++) hold = is_blocked(deps[i]);
			if (hold || !asset_.ready(r)) {
				block(r.doc);
				block(from);
				held_.push_back(std::move(op));
				continue;
			}
			flush();
			bool already = asset_.stale(r); // re-delivered after a re-sync
			asset_.apply(r);
			if (r.own) queue_.take(r.client_op_ref, nullptr);
			assets_unsettled = true;
			// A text file with new bytes (or gone): its live text starts over.
			if (already) {
				// nothing changed here
			} else if (r.type == "DeleteAsset") {
				text_.forget(r.doc);
			} else if (r.type == "MoveAsset") {
				text_.forget(r.payload.get("f", String()));
			} else if (!r.own && yhde::TextSync::is_text_path(r.doc)) {
				text_.file_replaced(r.doc, r.seq);
			}
		} else {
			if (!blocked.empty() && (is_blocked(r.doc) || refers(op.payload_json))) {
				block(r.doc);
				held_.push_back(std::move(op));
				continue;
			}
			settle_assets();
			if (r.type == "EditText") {
				// Typed text, in log order (text_editing.md).
				flush();
				text_.apply_remote(r.doc, r.seq, r.payload, r.own);
				if (r.own) queue_.take(r.client_op_ref, nullptr);
				last = std::max(last, op.seq);
				continue;
			}
			batch.push_back(std::move(r));
		}
		last = std::max(last, op.seq);
	}
	flush();
	settle_assets();
	if (catching_up) activity_batch_.clear(); // history, not live activity
	if (!held_.empty()) last = std::min(last, held_.front().seq - 1);
	if (debug_ && (!todo.empty())) {
		String held;
		for (const proto::CommittedOp &h : held_) held += String(" ") + String::num_int64(h.seq);
		UtilityFunctions::print("[yhde] round ", int64_t(todo.size()), " ops ", todo.front().seq, "..", todo.back().seq, " -> applied ", last,
				held.is_empty() ? String() : String(", held") + held);
	}
	cache_.set_applied_seq(std::max(last, int64_t(0)));
	flush_activity();
}

void YhdeSession::check_caught_up() {
	if (asset_.caught_up() || state_ != STATE_LIVE || next_seq_ <= catchup_head_) return;
	asset_.set_caught_up();
	send_pending();
}

void YhdeSession::flush_activity() {
	if (activity_batch_.empty()) return;
	double stamp = Time::get_singleton()->get_unix_time_from_system();
	size_t shown = 0;
	for (const ActivityItem &item : activity_batch_) {
		if (shown++ >= 8) break;
		Dictionary entry;
		entry["name"] = item.actor;
		entry["color"] = yhde::YhdePresence::color_for(item.actor);
		entry["initials"] = yhde::YhdePresence::initials_for(item.actor);
		entry["target"] = item.node;
		entry["text"] = item.subject;
		// Property edits are merged by the UI into "changed a, b on X".
		entry["props"] = item.properties_only ? item.props : PackedStringArray();
		entry["where"] = item.where;
		entry["scene"] = item.doc;
		entry["time"] = stamp;
		emit_signal("activity", entry);
	}
	activity_batch_.clear();
}

void YhdeSession::on_rejected(const proto::OpRejected &msg) {
	yhde::PendingOp op;
	if (!queue_.take(msg.client_op_ref, &op)) return;
	if (msg.code == "DuplicateOpId") return; // already committed; the log delivers it
	if (op.type == "EditText") {
		text_.on_rejected(op.doc, op.client_op_ref, msg.code);
		if (msg.code != "TextOutdated") emit_notice(String("A text edit was refused: ") + from_std(msg.reason), NOTICE_WARNING);
		return;
	}
	if (yhde::AssetSync::is_asset_op(op.type)) {
		Variant payload;
		if (yhde::from_json(from_std(op.payload_json), payload) && payload.get_type() == Variant::DICTIONARY) {
			asset_.on_rejected(payload, op.client_op_ref, msg.code);
		}
		if (msg.code == "AssetMissing") return; // uploaded again, then resent
		if (msg.code == "AlreadyCurrent") return; // someone shared the very same file first
	}
	emit_notice(String("The server refused a change: ") + from_std(msg.reason), NOTICE_WARNING);
	if ((op.type == "ChangeProperty" || op.type == "ChangeResourceProperty") && !queue_.masks(op.doc, op.target, op.key)) {
		Variant payload;
		if (yhde::from_json(from_std(op.payload_json), payload) && payload.get_type() == Variant::DICTIONARY) {
			engine_.revert(op.doc, op.target, op.key, Dictionary(payload).get("o", Variant()));
		}
	}
}

void YhdeSession::submit_undo(std::vector<yhde::LocalOp> &ops, uint64_t group, const std::vector<Uuid> &undo_of, const char *kind) {
	if (ops.empty()) return;
	yhde::UndoBatch batch;
	batch.request_id = Uuid::random();
	batch.kind = kind;
	batch.undo_of = undo_of;
	Uuid request = batch.request_id;
	queue_.add_batch(std::move(batch));
	for (yhde::LocalOp &op : ops) emit_local(std::move(op), group, request, false);
	ops.clear();
	if (state_ == STATE_LIVE) send_pending();
}

void YhdeSession::on_undo_result(const proto::UndoResult &result) {
	if (result.status == "committed") {
		queue_.drop_batch(result.request_id);
		return;
	}
	if (result.code == "TryAgain") {
		// The server could not save it right now: the same undo goes again.
		if (resend_at_ < 0) resend_at_ = now() + 3.0;
		return;
	}
	// Refused as an undo (e.g. not the author): the local state already shows
	// the change, so it goes out as an ordinary edit.
	emit_notice(String("Undo recorded as a normal edit: ") + from_std(result.reason), NOTICE_WARNING);
	queue_.unbatch(result.request_id);
	send_pending();
}

Uuid YhdeSession::emit_local(yhde::LocalOp &&op, uint64_t group, const Uuid &batch, bool send, const std::string &blob,
		bool before_doc) {
	yhde::PendingOp p;
	p.batch = batch;
	p.op_id = Uuid::random();
	p.client_op_ref = Uuid::random();
	p.type = op.type;
	p.target = op.target;
	p.doc = op.payload.get("s", String());
	p.key = op.key;
	p.payload_json = to_std(yhde::to_json(op.payload));
	p.parent_seq = next_seq_ - 1;
	p.blob = blob;
	if (p.payload_json.size() > kMaxPayloadBytes) {
		emit_notice(String("A change is too large to synchronize (") + String::num_int64(int64_t(p.payload_json.size() / 1024)) +
						String::utf8(" KiB): ") + String::utf8(op.type.c_str()),
				NOTICE_ERROR);
		return Uuid();
	}
	if (audit_) {
		Dictionary a;
		a["type"] = String::utf8(op.type.c_str());
		a["doc"] = p.doc;
		a["key"] = op.key;
		// Undo/redo ops count against the action they undo.
		uint64_t action_group = group;
		if (ActionRecord *rec = group ? record_for(group, nullptr) : nullptr) action_group = rec->group;
		a["group"] = int64_t(action_group);
		a["undo"] = !batch.is_nil();
		String where;
		if (yhde::SyncDocument *d = engine_.find_doc(p.doc)) {
			Node *n = d->node(op.target);
			Node *root = d->root_node();
			if (n && root) where = String(root->get_path_to(n));
		}
		a["path"] = where;
		audit_log_.push_back(a);
	}
	if (group != 0) {
		bool undo = false;
		if (ActionRecord *rec = record_for(group, &undo)) {
			JournalOp j;
			j.op_id = p.op_id;
			j.op = op;
			(undo ? rec->undone : rec->done).push_back(std::move(j));
		}
	}
	Uuid ref = p.client_op_ref;
	if (before_doc) {
		String doc = p.doc;
		queue_.push_before_doc(std::move(p), doc);
	} else {
		queue_.push(std::move(p));
	}
	if (yhde::AssetSync::is_asset_op(op.type)) { // (p was moved into the queue)
		authored_files_++;
	} else {
		authored_++;
	}
	if (send && state_ == STATE_LIVE) send_pending();
	return ref;
}

void YhdeSession::send_pending() {
	if (state_ != STATE_LIVE || !net_.is_open() || join_pending_) return;
	// Checking what an operation refers to can queue a file's registration,
	// which asks to send again: that happens after this pass, not inside it.
	if (sending_) {
		send_again_ = true;
		return;
	}
	sending_ = true;
	for (int pass = 0; pass < 4; pass++) {
		send_again_ = false;
		send_pending_pass();
		if (!send_again_) break;
	}
	sending_ = false;
}

void YhdeSession::send_pending_pass() {
	bool any = false;
	// An asset operation goes out once its bytes are uploaded; operations on
	// the same file, or that refer to it, wait behind it. Others go ahead.
	std::vector<std::string> blocked;
	// An edit that refers to a project file the log does not have yet (a
	// texture dropped in and assigned at once) waits until that file's
	// registration has gone out ahead of it.
	auto references_wait = [&](const yhde::PendingOp &o) {
		if (yhde::AssetSync::is_asset_op(o.type)) return false;
		size_t pos = 0;
		while ((pos = o.payload_json.find("\"res://", pos)) != std::string::npos) {
			size_t end = o.payload_json.find('"', pos + 1);
			if (end == std::string::npos) break;
			String ref = from_std(o.payload_json.substr(pos + 1, end - pos - 1));
			pos = end + 1;
			Uuid reg;
			if (!asset_.reference_waits(ref, &reg)) continue;
			if (reg.is_nil()) return true;
			const yhde::PendingOp *r = queue_.find(reg);
			if (r && !r->sent) return true;
		}
		return false;
	};
	auto waits = [&](const yhde::PendingOp &o) {
		if (!o.blob.empty() && !asset_.blob_ready(o.blob)) return true;
		if (references_wait(o)) return true;
		if (blocked.empty()) return false;
		std::string doc = to_std(yhde::AssetSync::key_of(o.doc));
		for (const std::string &b : blocked) {
			if (doc == b || o.payload_json.find("\"" + b + "\"") != std::string::npos ||
					o.payload_json.find("\"" + b + "::") != std::string::npos) {
				return true;
			}
		}
		return false;
	};
	for (yhde::PendingOp &op : queue_.ops()) {
		if (op.sent) continue;
		bool wait = false;
		if (!op.batch.is_nil()) {
			for (const yhde::PendingOp &o : queue_.ops()) {
				if (o.batch == op.batch && !o.sent && waits(o)) wait = true;
			}
		} else {
			wait = waits(op);
		}
		if (wait) {
			blocked.push_back(to_std(yhde::AssetSync::key_of(op.doc)));
			continue;
		}
		if (!op.batch.is_nil()) {
			// An undo/redo travels as one request.
			const yhde::UndoBatch *b = queue_.batch(op.batch);
			proto::UndoRequest req;
			req.request_id = op.batch;
			req.kind = b ? b->kind : std::string("undo");
			if (b) req.undo_of = b->undo_of;
			std::vector<yhde::PendingOp *> members;
			for (yhde::PendingOp &o : queue_.ops()) {
				if (o.batch != op.batch || o.sent) continue;
				proto::SubmitOp m;
				m.op_id = o.op_id;
				m.type = o.type;
				m.target_id = o.target;
				m.payload_json = o.payload_json;
				m.client_op_ref = o.client_op_ref;
				m.parent_seq = o.parent_seq;
				req.ops.push_back(std::move(m));
				members.push_back(&o);
			}
			proto::Frame f;
			f.type = proto::MsgType::UndoRequest;
			f.channel = proto::Channel::Ops;
			std::array<uint8_t, 16> ref = op.batch.to_wire();
			f.client_op_ref.assign(ref.begin(), ref.end());
			f.payload = proto::encode(req);
			if (!net_.send(f)) break;
			for (yhde::PendingOp *o : members) o->sent = true;
			any = true;
			continue;
		}
		proto::SubmitOp m;
		m.op_id = op.op_id;
		m.type = op.type;
		m.target_id = op.target;
		m.payload_json = op.payload_json;
		m.client_op_ref = op.client_op_ref;
		m.parent_seq = op.parent_seq;
		proto::Frame f;
		f.type = proto::MsgType::SubmitOp;
		f.channel = proto::Channel::Ops;
		std::array<uint8_t, 16> ref = op.client_op_ref.to_wire();
		f.client_op_ref.assign(ref.begin(), ref.end());
		f.payload = proto::encode(m);
		if (!net_.send(f)) break;
		op.sent = true;
		any = true;
	}
	if (any) queue_.mark_dirty();
}

void YhdeSession::send_ack(bool force) {
	int64_t seq = cache_.applied_seq();
	if (seq <= last_ack_sent_ || !net_.is_open()) return;
	double t = now();
	if (!force && t - last_ack_time_ < kAckInterval) return;
	proto::Frame f;
	f.type = proto::MsgType::Ack;
	f.channel = proto::Channel::Ops;
	f.seq_or_ack = seq;
	f.payload = proto::encode_ack(seq);
	if (net_.send(f)) {
		last_ack_sent_ = seq;
		last_ack_time_ = t;
	}
}

void YhdeSession::handle_closed_docs() {
	bool need = false;
	for (const String &doc : engine_.take_closed_docs()) need = cache_.note_discarded(doc) || need;
	if (need) resync_requested_ = true;
}

// Presence (outbound)

void YhdeSession::tick_presence() {
	if (state_ != STATE_LIVE) return;
	double t = now();
	if (t - last_presence_sent_ < kPresenceInterval) return;

	EditorInterface *ei = EditorInterface::get_singleton();
	yhde::SyncDocument *doc = engine_.current_doc();
	Dictionary st;

	Array sel;
	Array drags;
	if (doc) {
		TypedArray<Node> nodes = ei->get_selection()->get_selected_nodes();
		for (int64_t i = 0; i < nodes.size() && sel.size() < kMaxSelection; i++) {
			Node *n = Object::cast_to<Node>(nodes[i]);
			Uuid id = doc->node_id(n);
			if (id.is_nil()) continue;
			sel.push_back(id.to_string());
			if (drags.size() >= kMaxDrags || !doc->transform_in_flight(n)) continue;
			Array d;
			d.push_back(id.to_string());
			if (Node3D *n3 = Object::cast_to<Node3D>(n)) {
				d.push_back("t3");
				Array f = transform3d_floats(n3->get_global_transform());
				for (int64_t k = 0; k < f.size(); k++) d.push_back(f[k]);
			} else if (CanvasItem *ci = Object::cast_to<CanvasItem>(n)) {
				Transform2D xf = ci->get_global_transform();
				d.push_back("t2");
				Array f = floats({ xf.columns[0].x, xf.columns[0].y, xf.columns[1].x, xf.columns[1].y, xf.columns[2].x, xf.columns[2].y });
				for (int64_t k = 0; k < f.size(); k++) d.push_back(f[k]);
				if (Control *c = Object::cast_to<Control>(n)) {
					d.push_back(double(c->get_size().x));
					d.push_back(double(c->get_size().y));
				}
			} else {
				continue;
			}
			drags.push_back(d);
		}
	}
	st["sel"] = sel;
	if (!drags.is_empty()) st["drag"] = drags;
	// Where you are typing (script editor).
	Dictionary text_state = text_.caret_state();
	if (!text_state.is_empty() && (main_screen_ == "Script" || yhde::TextSync::is_shader_path(text_state["script"]))) {
		st["script"] = text_state["script"];
		st["caret"] = text_state["caret"];
		if (text_state.has("tsel")) st["tsel"] = text_state["tsel"];
		if (t - text_.last_typed() < 3.0) st["typing"] = true;
	}
	if (!pointer_.is_empty()) st["cur"] = pointer_;
	// Each summon has its own id; editors act once per id they see appear.
	if (!summon_.is_empty() && t - summon_at_ < kSummonSeconds) st["summon"] = summon_;

	if (follow_target_.is_empty()) {
		if (main_screen_ == "2D") {
			if (SubViewport *vp = ei->get_editor_viewport_2d()) {
				Transform2D xf = vp->get_global_canvas_transform();
				Vector2 size = vp->get_visible_rect().size;
				Vector2 center = xf.affine_inverse().xform(size * 0.5f);
				real_t zoom = xf.get_scale().x;
				st["view"] = floats({ center.x, center.y, zoom });
			}
		} else if (main_screen_ == "3D") {
			if (SubViewport *vp = ei->get_editor_viewport_3d(0)) {
				if (Camera3D *cam = vp->get_camera_3d()) {
					Array v = transform3d_floats(cam->get_global_transform());
					v.push_back(double(cam->get_fov()));
					st["view"] = v;
				}
			}
		}
	}

	String scene = engine_.current_scene_path();
	String json = yhde::to_json(st);
	bool changed = json != last_presence_json_ || scene != last_presence_scene_;
	if (!changed && t - last_presence_sent_ < kPresenceKeepAlive) return;

	proto::PresenceUpdate u;
	u.display_name = to_std(display_name_);
	u.scene = to_std(scene);
	u.tool = to_std(main_screen_);
	u.state_json = to_std(json);
	proto::Frame f;
	f.type = proto::MsgType::PresenceUpdate;
	f.channel = proto::Channel::Presence;
	f.payload = proto::encode(u);
	if (net_.send(f)) {
		last_presence_sent_ = t;
		last_presence_json_ = json;
		last_presence_scene_ = scene;
	}
}

void YhdeSession::set_pointer_2d(const Vector2 &viewport_position) {
	EditorInterface *ei = EditorInterface::get_singleton();
	SubViewport *vp = ei ? ei->get_editor_viewport_2d() : nullptr;
	if (!vp) return;
	Vector2 world = vp->get_global_canvas_transform().affine_inverse().xform(viewport_position);
	pointer_ = floats({ world.x, world.y });
}

void YhdeSession::set_pointer_3d(Camera3D *camera, const Vector2 &viewport_position) {
	if (!camera) return;
	Vector3 origin = camera->project_ray_origin(viewport_position);
	Vector3 dir = camera->project_ray_normal(viewport_position);
	Vector3 point = origin + dir * 10.0f;
	if (std::abs(dir.y) > 1e-5f) {
		real_t t = -origin.y / dir.y;
		if (t > 0.0f && t < 2000.0f) point = origin + dir * t;
	}
	pointer_ = floats({ point.x, point.y, point.z });
}

void YhdeSession::clear_pointer() {
	pointer_ = Array();
}

void YhdeSession::set_main_screen(const String &name) {
	if (name == main_screen_) return;
	main_screen_ = name;
	pointer_ = Array();
}

// Editor notifications

void YhdeSession::notify_history_changed() {
	on_manager_history_changed();
}

void YhdeSession::notify_scene_saved(const String &path) {
	if (state_ == STATE_OFFLINE) return;
	cache_.note_saved(path);
	asset_.editor_saved(path);
	engine_.on_scene_saved(path);
}

void YhdeSession::notify_scene_closed(const String &path) {
	if (state_ == STATE_OFFLINE) return;
	engine_.request_refresh();
}

void YhdeSession::notify_scene_changed() {
	if (state_ == STATE_OFFLINE) return;
	engine_.request_refresh();
	emit_signal("presence_changed");
}

void YhdeSession::notify_resource_saved(const Ref<Resource> &res) {
	if (state_ == STATE_OFFLINE || res.is_null()) return;
	cache_.note_saved(res->get_path());
	// A live text file you saved: everyone saves it (its text is already shared).
	if (yhde::TextSync::is_text_path(res->get_path()) && text_.saved(res->get_path())) {
		asset_.absorb(res->get_path());
		return;
	}
	asset_.editor_saved(res->get_path());
	engine_.on_resource_saved(res);
}

// Secrets (sign-in token, invite code or server key)
// They live in the editor's own data folder, never inside the project (which
// gets committed and zipped), readable only by the user. Each is tied to the
// exact server address it belongs to, so a changed address can never send it
// to a different server.

String YhdeSession::access_keys_path() const {
	EditorInterface *ei = EditorInterface::get_singleton();
	if (!ei) return String();
	return ei->get_editor_paths()->get_data_dir().path_join("yhde").path_join("access_keys.json");
}

Dictionary YhdeSession::load_access_keys() const {
	String path = access_keys_path();
	if (path.is_empty() || !FileAccess::file_exists(path)) return Dictionary();
	Variant parsed = JSON::parse_string(FileAccess::get_file_as_string(path));
	return parsed.get_type() == Variant::DICTIONARY ? Dictionary(parsed) : Dictionary();
}

void YhdeSession::set_access_key(const String &url, const String &key) {
	String u = url.strip_edges();
	String k = key.strip_edges();
	if (u.is_empty()) return;
	if (!k.is_empty() && !valid_access_key(k)) {
		emit_notice("That sign-in or code contains characters it cannot have (spaces or non-ASCII)", NOTICE_ERROR);
		return;
	}
	String path = access_keys_path();
	if (path.is_empty()) return;
	String dir = path.get_base_dir();
	DirAccess::make_dir_recursive_absolute(dir);
	restrict_to_owner(dir, true);

	Dictionary keys = load_access_keys();
	if (k.is_empty()) {
		keys.erase(u);
	} else {
		keys[u] = k;
	}
	// Written next to it, then moved over it: a reader (another editor on
	// this computer) never sees a half-written file.
	String final_path = path;
	path = path + ".tmp" + String::num_int64(int64_t(OS::get_singleton()->get_process_id()));
	Ref<FileAccess> f = FileAccess::open(path, FileAccess::WRITE);
	if (f.is_null()) {
		emit_notice("Could not save the sign-in", NOTICE_ERROR);
		return;
	}
	f->store_string(JSON::stringify(keys, "\t"));
	f->close();
	restrict_to_owner(path, false);
	if (DirAccess::rename_absolute(path, final_path) != OK) {
		DirAccess::remove_absolute(path);
		emit_notice("Could not save the sign-in", NOTICE_ERROR);
	}
}

bool YhdeSession::has_access_key(const String &url) const {
	return load_access_keys().has(url.strip_edges());
}

// Queries for the UI

String YhdeSession::get_status_text() const {
	switch (state_) {
		case STATE_OFFLINE:
			return "Offline";
		case STATE_CONNECTING:
			return String::utf8("Connecting…");
		case STATE_SYNCING: {
			int64_t done = std::max<int64_t>(0, next_seq_ - 1);
			if (head_seq_ > 0 && done < head_seq_) {
				return String("Syncing ") + String::num_int64(done) + " / " + String::num_int64(head_seq_);
			}
			return String::utf8("Syncing…");
		}
		case STATE_LIVE:
			return "Live";
		case STATE_RECONNECTING: {
			double wait = net_.seconds_until_retry(double(Time::get_singleton()->get_ticks_msec()) / 1000.0);
			String text = wait > 0.5 ? String("Reconnecting in ") + String::num_int64(int64_t(std::ceil(wait))) + "s" : String::utf8("Reconnecting…");
			return text;
		}
	}
	return String();
}

Dictionary YhdeSession::get_status() const {
	Dictionary d;
	d["state"] = int(state_);
	d["text"] = get_status_text();
	d["error"] = state_ == STATE_OFFLINE ? offline_reason_ : net_.last_error();
	d["rtt_ms"] = net_.round_trip_ms();
	d["pending"] = int64_t(queue_.size());
	d["seq"] = cache_.applied_seq();
	d["head"] = head_seq_;
	d["peers"] = int64_t(presence_.peers().size());
	d["scan_ms"] = engine_.last_scan_ms();
	d["scene"] = engine_.current_scene_path();
	d["scene_unsaved"] = engine_.current_scene_unsaved();
	d["session_id"] = session_id_.is_nil() ? String() : session_id_.to_string();
	d["authored"] = authored_;
	d["authored_files"] = authored_files_;
	d["assets"] = asset_.status();
	d["held"] = int64_t(held_.size());
	d["server_version"] = from_std(server_version_);
	d["project_name"] = project_name_;
	d["project_id"] = project_id_.to_string();
	d["join_pending"] = join_pending_;
	d["social"] = has_social();
	return d;
}

String YhdeSession::get_version() const {
	return kAddonVersion;
}

void YhdeSession::load_member_id() {
	// One random id per project folder, kept in the folder's editor data
	// (.godot, never shared): chat and comments know it is the same person
	// tomorrow, and two editors on one computer are two people. The first
	// folder takes over the id older versions kept per editor install, so
	// comments made before stay yours.
	EditorInterface *ei = EditorInterface::get_singleton();
	if (!ei) return;
	auto read_id = [this](const String &path) {
		return FileAccess::file_exists(path) && Uuid::parse(FileAccess::get_file_as_string(path).strip_edges(), member_id_) && !member_id_.is_nil();
	};
	auto write = [](const String &path, const String &text) {
		DirAccess::make_dir_recursive_absolute(path.get_base_dir());
		Ref<FileAccess> f = FileAccess::open(path, FileAccess::WRITE);
		if (f.is_valid()) f->store_string(text);
	};
	String local = ei->get_editor_paths()->get_project_settings_dir().path_join("yhde").path_join("member_id");
	if (read_id(local)) return;
	String install = ei->get_editor_paths()->get_data_dir().path_join("yhde").path_join("member_id");
	String claimed = install + ".claimed";
	if (!FileAccess::file_exists(claimed) && read_id(install)) {
		write(claimed, ProjectSettings::get_singleton()->globalize_path("res://"));
	} else {
		member_id_ = Uuid::random();
	}
	write(local, member_id_.to_string());
}

bool YhdeSession::has_social() const {
	return std::find(server_capabilities_.begin(), server_capabilities_.end(), "social") != server_capabilities_.end();
}

String YhdeSession::social_send(const String &kind, const Dictionary &body) {
	if (state_ != STATE_LIVE || !net_.is_open() || !has_social()) return String();
	proto::SocialRequest req;
	req.request_id = Uuid::random();
	req.kind = to_std(kind);
	req.body_json = to_std(yhde::to_json(body));
	if (req.body_json.size() > kMaxSocialBody) return String();
	proto::Frame f;
	f.type = proto::MsgType::SocialRequest;
	f.channel = proto::Channel::Social;
	f.payload = proto::encode(req);
	if (!net_.send(f)) return String();
	return req.request_id.to_string();
}

String YhdeSession::get_node_id(Node *node) const {
	if (!node) return String();
	yhde::SyncDocument *doc = const_cast<yhde::SyncEngine &>(engine_).current_doc();
	if (!doc) return String();
	Uuid id = doc->node_id(node);
	return id.is_nil() ? String() : id.to_string();
}

Node *YhdeSession::find_node_by_id(const String &scene_path, const String &id) const {
	yhde::SyncDocument *doc = const_cast<yhde::SyncEngine &>(engine_).find_doc(scene_path);
	Uuid u;
	if (!doc || !Uuid::parse(id, u)) return nullptr;
	return doc->node(u);
}

Dictionary YhdeSession::get_defaults() const {
	Dictionary d;
	d["url"] = kDefaultUrl;
	d["project_id"] = kDefaultProject;
	d["branch_id"] = kDefaultBranch;
	return d;
}

Dictionary YhdeSession::get_local_peer() const {
	Dictionary d;
	d["id"] = session_id_.is_nil() ? String() : session_id_.to_string();
	d["member"] = member_id_.is_nil() ? String() : member_id_.to_string();
	d["name"] = display_name_;
	d["color"] = yhde::YhdePresence::color_for(display_name_);
	d["initials"] = yhde::YhdePresence::initials_for(display_name_);
	return d;
}

Dictionary YhdeSession::peer_dict(const yhde::Peer &p) const {
	Dictionary d;
	d["id"] = p.session.to_string();
	d["member"] = p.member.is_nil() ? String() : p.member.to_string();
	d["name"] = p.name;
	d["color"] = p.color;
	d["initials"] = yhde::YhdePresence::initials_for(p.name);
	d["scene"] = p.scene;
	d["scene_name"] = p.scene.get_file().get_basename();
	d["tool"] = p.tool;
	d["same_scene"] = !p.scene.is_empty() && p.scene == engine_.current_scene_path();
	d["script"] = p.state.get("script", String());
	d["typing"] = bool(p.state.get("typing", false));
	d["selection_ids"] = p.state.get("sel", Array());
	d["following"] = follow_target_ == p.session.to_string();
	d["summon"] = p.state.get("summon", String());
	Variant sel = p.state.get("sel", Array());
	d["selection_count"] = sel.get_type() == Variant::ARRAY ? Array(sel).size() : 0;
	return d;
}

Array YhdeSession::get_peers() const {
	Array out;
	for (const auto &kv : presence_.peers()) out.push_back(peer_dict(kv.second));
	return out;
}

Array YhdeSession::resolve_selection(const yhde::Peer &p) const {
	Array nodes;
	yhde::SyncDocument *doc = const_cast<yhde::SyncEngine &>(engine_).current_doc();
	if (!doc) return nodes;
	Variant sel = p.state.get("sel", Array());
	if (sel.get_type() != Variant::ARRAY) return nodes;
	Array ids = sel;
	for (int64_t i = 0; i < ids.size() && i < kMaxSelection; i++) {
		Uuid id;
		if (!Uuid::parse(String(ids[i]), id)) continue;
		if (Node *n = doc->node(id)) nodes.push_back(n);
	}
	return nodes;
}

Array YhdeSession::get_overlay_2d() const {
	Array out;
	yhde::SyncDocument *doc = const_cast<yhde::SyncEngine &>(engine_).current_doc();
	for (const auto &kv : presence_.peers()) {
		const yhde::Peer &p = kv.second;
		if (!doc || p.scene != doc->path()) continue;
		Dictionary d = peer_dict(p);
		double c[2];
		if (p.tool == "2D" && read_floats(p.state.get("cur", Variant()), 0, 2, c) && Array(p.state["cur"]).size() == 2) {
			d["cursor"] = Vector2(real_t(c[0]), real_t(c[1]));
		}
		d["selection"] = resolve_selection(p);
		Array ghosts;
		Variant drags = p.state.get("drag", Array());
		if (drags.get_type() == Variant::ARRAY) {
			Array list = drags;
			for (int64_t i = 0; i < list.size() && i < kMaxDrags; i++) {
				if (list[i].get_type() != Variant::ARRAY) continue;
				Array g = list[i];
				if (g.size() < 8 || String(g[1]) != "t2") continue;
				Node *n = doc->node(Uuid::parse_or_nil(g[0]));
				double f[6];
				if (!n || !read_floats(g, 2, 6, f)) continue;
				Dictionary gd;
				gd["node"] = n;
				gd["xform"] = Transform2D(Vector2(real_t(f[0]), real_t(f[1])), Vector2(real_t(f[2]), real_t(f[3])),
						Vector2(real_t(f[4]), real_t(f[5])));
				double sz[2];
				if (read_floats(g, 8, 2, sz)) gd["size"] = Vector2(real_t(sz[0]), real_t(sz[1]));
				ghosts.push_back(gd);
			}
		}
		d["ghosts"] = ghosts;
		out.push_back(d);
	}
	return out;
}

Array YhdeSession::get_overlay_3d() const {
	Array out;
	yhde::SyncDocument *doc = const_cast<yhde::SyncEngine &>(engine_).current_doc();
	for (const auto &kv : presence_.peers()) {
		const yhde::Peer &p = kv.second;
		if (!doc || p.scene != doc->path()) continue;
		Dictionary d = peer_dict(p);
		double c[3];
		if (p.tool == "3D" && read_floats(p.state.get("cur", Variant()), 0, 3, c)) {
			d["cursor"] = Vector3(real_t(c[0]), real_t(c[1]), real_t(c[2]));
		}
		double v[13];
		if (p.tool == "3D" && read_floats(p.state.get("view", Variant()), 0, 13, v)) {
			d["camera"] = transform3d_from(v);
			d["fov"] = v[12];
		}
		d["selection"] = resolve_selection(p);
		Array ghosts;
		Variant drags = p.state.get("drag", Array());
		if (drags.get_type() == Variant::ARRAY) {
			Array list = drags;
			for (int64_t i = 0; i < list.size() && i < kMaxDrags; i++) {
				if (list[i].get_type() != Variant::ARRAY) continue;
				Array g = list[i];
				if (g.size() < 14 || String(g[1]) != "t3") continue;
				Node *n = doc->node(Uuid::parse_or_nil(g[0]));
				double f[12];
				if (!n || !read_floats(g, 2, 12, f)) continue;
				Dictionary gd;
				gd["node"] = n;
				gd["xform"] = transform3d_from(f);
				ghosts.push_back(gd);
			}
		}
		d["ghosts"] = ghosts;
		out.push_back(d);
	}
	return out;
}

// Follow mode

void YhdeSession::follow(const String &peer_id) {
	if (!presence_.find(Uuid::parse_or_nil(peer_id))) return;
	follow_target_ = peer_id;
	emit_signal("peers_changed");
}

void YhdeSession::summon() {
	summon_ = Uuid::random().to_string();
	summon_at_ = now();
	last_presence_sent_ = -1e9; // announce it on the next tick
}

void YhdeSession::unfollow() {
	if (follow_target_.is_empty()) return;
	follow_target_ = String();
	emit_signal("peers_changed");
}

String YhdeSession::get_follow_target() const {
	return follow_target_;
}

void YhdeSession::text_bind(Node *code_edit, const String &path) {
	if (state_ == STATE_OFFLINE || !asset_.caught_up() || join_pending_) return;
	text_.bind(Object::cast_to<CodeEdit>(code_edit), path);
}

void YhdeSession::text_unbind(Node *code_edit) {
	text_.unbind(Object::cast_to<CodeEdit>(code_edit));
}

Array YhdeSession::get_script_carets(const String &path) const {
	Array out;
	for (const auto &kv : presence_.peers()) {
		const yhde::Peer &p = kv.second;
		if (String(p.state.get("script", String())) != path) continue;
		if (p.tool != "Script" && !yhde::TextSync::is_shader_path(path)) continue; // shaders: bottom panel
		Dictionary d;
		d["id"] = p.session.to_string();
		d["name"] = p.name;
		d["color"] = p.color;
		d["caret"] = p.state.get("caret", Array());
		d["sel"] = p.state.get("tsel", Array());
		d["typing"] = bool(p.state.get("typing", false));
		out.push_back(d);
	}
	return out;
}

Dictionary YhdeSession::get_follow_view() const {
	Dictionary d;
	const yhde::Peer *p = presence_.find(Uuid::parse_or_nil(follow_target_));
	if (!p) return d;
	d["id"] = follow_target_;
	d["name"] = p->name;
	d["color"] = p->color;
	d["scene"] = p->scene;
	d["tool"] = p->tool;
	double v[13];
	if (p->tool == "2D" && read_floats(p->state.get("view", Variant()), 0, 3, v)) {
		d["center"] = Vector2(real_t(v[0]), real_t(v[1]));
		d["zoom"] = v[2];
	} else if (p->tool == "3D" && read_floats(p->state.get("view", Variant()), 0, 13, v)) {
		d["camera"] = transform3d_from(v);
		d["fov"] = v[12];
	}
	return d;
}

void YhdeSession::set_allow_builtin_scripts(bool allow) {
	engine_.set_allow_builtin_scripts(allow);
}

Array YhdeSession::take_audit() {
	Array out = audit_log_;
	audit_log_ = Array();
	return out;
}

bool YhdeSession::get_allow_builtin_scripts() const {
	return engine_.allow_builtin_scripts();
}

// Bindings

void YhdeSession::_bind_methods() {
	ClassDB::bind_method(D_METHOD("start", "url", "project_id", "branch_id", "display_name"), &YhdeSession::start);
	ClassDB::bind_method(D_METHOD("set_access_key", "url", "key"), &YhdeSession::set_access_key);
	ClassDB::bind_method(D_METHOD("has_access_key", "url"), &YhdeSession::has_access_key);
	ClassDB::bind_method(D_METHOD("take_audit"), &YhdeSession::take_audit);
	ClassDB::bind_method(D_METHOD("get_last_action_group"), &YhdeSession::get_last_action_group);
	ClassDB::bind_method(D_METHOD("stop"), &YhdeSession::stop);
	ClassDB::bind_method(D_METHOD("get_state"), &YhdeSession::get_state);
	ClassDB::bind_method(D_METHOD("get_status_text"), &YhdeSession::get_status_text);
	ClassDB::bind_method(D_METHOD("get_status"), &YhdeSession::get_status);
	ClassDB::bind_method(D_METHOD("get_defaults"), &YhdeSession::get_defaults);
	ClassDB::bind_method(D_METHOD("get_local_peer"), &YhdeSession::get_local_peer);
	ClassDB::bind_method(D_METHOD("get_peers"), &YhdeSession::get_peers);
	ClassDB::bind_method(D_METHOD("get_overlay_2d"), &YhdeSession::get_overlay_2d);
	ClassDB::bind_method(D_METHOD("get_overlay_3d"), &YhdeSession::get_overlay_3d);
	ClassDB::bind_method(D_METHOD("set_pointer_2d", "viewport_position"), &YhdeSession::set_pointer_2d);
	ClassDB::bind_method(D_METHOD("summon"), &YhdeSession::summon);
	ClassDB::bind_method(D_METHOD("set_pointer_3d", "camera", "viewport_position"), &YhdeSession::set_pointer_3d);
	ClassDB::bind_method(D_METHOD("clear_pointer"), &YhdeSession::clear_pointer);
	ClassDB::bind_method(D_METHOD("set_main_screen", "name"), &YhdeSession::set_main_screen);
	ClassDB::bind_method(D_METHOD("notify_history_changed"), &YhdeSession::notify_history_changed);
	ClassDB::bind_method(D_METHOD("notify_scene_saved", "path"), &YhdeSession::notify_scene_saved);
	ClassDB::bind_method(D_METHOD("notify_scene_closed", "path"), &YhdeSession::notify_scene_closed);
	ClassDB::bind_method(D_METHOD("notify_scene_changed"), &YhdeSession::notify_scene_changed);
	ClassDB::bind_method(D_METHOD("notify_resource_saved", "resource"), &YhdeSession::notify_resource_saved);
	ClassDB::bind_method(D_METHOD("follow", "peer_id"), &YhdeSession::follow);
	ClassDB::bind_method(D_METHOD("unfollow"), &YhdeSession::unfollow);
	ClassDB::bind_method(D_METHOD("get_follow_target"), &YhdeSession::get_follow_target);
	ClassDB::bind_method(D_METHOD("get_follow_view"), &YhdeSession::get_follow_view);
	ClassDB::bind_method(D_METHOD("set_allow_builtin_scripts", "allow"), &YhdeSession::set_allow_builtin_scripts);
	ClassDB::bind_method(D_METHOD("get_allow_builtin_scripts"), &YhdeSession::get_allow_builtin_scripts);
	ClassDB::bind_method(D_METHOD("get_version"), &YhdeSession::get_version);
	ClassDB::bind_method(D_METHOD("social_send", "kind", "body"), &YhdeSession::social_send);
	ClassDB::bind_method(D_METHOD("has_social"), &YhdeSession::has_social);
	ClassDB::bind_method(D_METHOD("get_member_id"), &YhdeSession::get_member_id);
	ClassDB::bind_method(D_METHOD("get_color_for", "name"), &YhdeSession::get_color_for);
	ClassDB::bind_method(D_METHOD("get_initials_for", "name"), &YhdeSession::get_initials_for);
	ClassDB::bind_method(D_METHOD("get_node_id", "node"), &YhdeSession::get_node_id);
	ClassDB::bind_method(D_METHOD("find_node_by_id", "scene_path", "id"), &YhdeSession::find_node_by_id);
	ClassDB::bind_method(D_METHOD("get_held_deletions"), &YhdeSession::get_held_deletions);
	ClassDB::bind_method(D_METHOD("text_bind", "code_edit", "path"), &YhdeSession::text_bind);
	ClassDB::bind_method(D_METHOD("text_unbind", "code_edit"), &YhdeSession::text_unbind);
	ClassDB::bind_method(D_METHOD("is_text_live", "path"), &YhdeSession::is_text_live);
	ClassDB::bind_method(D_METHOD("get_script_carets", "path"), &YhdeSession::get_script_carets);
	ClassDB::bind_method(D_METHOD("confirm_join"), &YhdeSession::confirm_join);
	ClassDB::bind_method(D_METHOD("get_project_name"), &YhdeSession::get_project_name);
	ClassDB::bind_method(D_METHOD("confirm_deletions"), &YhdeSession::confirm_deletions);
	ClassDB::bind_method(D_METHOD("get_held_code"), &YhdeSession::get_held_code);
	ClassDB::bind_method(D_METHOD("accept_held_code"), &YhdeSession::accept_held_code);
	ClassDB::bind_method(D_METHOD("set_trust_code", "on"), &YhdeSession::set_trust_code);
	ClassDB::bind_method(D_METHOD("restore_deletions"), &YhdeSession::restore_deletions);

	ADD_SIGNAL(MethodInfo("state_changed", PropertyInfo(Variant::INT, "state"), PropertyInfo(Variant::STRING, "text")));
	ADD_SIGNAL(MethodInfo("peers_changed"));
	ADD_SIGNAL(MethodInfo("presence_changed"));
	ADD_SIGNAL(MethodInfo("activity", PropertyInfo(Variant::DICTIONARY, "entry")));
	ADD_SIGNAL(MethodInfo("notice", PropertyInfo(Variant::STRING, "text"), PropertyInfo(Variant::INT, "level")));
	ADD_SIGNAL(MethodInfo("project_joined", PropertyInfo(Variant::STRING, "project_id"), PropertyInfo(Variant::STRING, "branch_id"),
			PropertyInfo(Variant::STRING, "name")));
	ADD_SIGNAL(MethodInfo("join_check", PropertyInfo(Variant::DICTIONARY, "info")));
	ADD_SIGNAL(MethodInfo("social_event", PropertyInfo(Variant::STRING, "kind"), PropertyInfo(Variant::DICTIONARY, "body"),
			PropertyInfo(Variant::STRING, "request_id")));

	BIND_ENUM_CONSTANT(STATE_OFFLINE);
	BIND_ENUM_CONSTANT(STATE_CONNECTING);
	BIND_ENUM_CONSTANT(STATE_SYNCING);
	BIND_ENUM_CONSTANT(STATE_LIVE);
	BIND_ENUM_CONSTANT(STATE_RECONNECTING);
	BIND_ENUM_CONSTANT(NOTICE_INFO);
	BIND_ENUM_CONSTANT(NOTICE_WARNING);
	BIND_ENUM_CONSTANT(NOTICE_ERROR);
}

} // namespace godot
