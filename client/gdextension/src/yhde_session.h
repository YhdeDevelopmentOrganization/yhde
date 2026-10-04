#pragma once

#include "assets/asset_sync.h"
#include "local_cache/yhde_local_cache.h"
#include "networking/yhde_networking.h"
#include "operation_queue/yhde_operation_queue.h"
#include "presence/yhde_presence.h"
#include "sync/sync_engine.h"
#include "text/text_sync.h"

#include <godot_cpp/classes/camera3d.hpp>
#include <godot_cpp/classes/node.hpp>
#include <godot_cpp/classes/resource.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/dictionary.hpp>

#include <deque>
#include <map>
#include <unordered_map>

namespace godot {

// The YHDE editor session: the one object the GDScript UI talks to. The wire
// protocol, the operation model, scene diffing and applying, and identity all
// live here in native code. The UI only gets display data (names, colors,
// positions) and forwards editor events.
class YhdeSession : public Node {
	GDCLASS(YhdeSession, Node)

public:
	enum State {
		STATE_OFFLINE = 0,
		STATE_CONNECTING = 1,
		STATE_SYNCING = 2,
		STATE_LIVE = 3,
		STATE_RECONNECTING = 4,
	};

	enum NoticeLevel {
		NOTICE_INFO = 0,
		NOTICE_WARNING = 1,
		NOTICE_ERROR = 2,
	};

	YhdeSession();
	~YhdeSession() override;

	void _process(double delta) override;
	void _notification(int what);

	bool start(const String &url, const String &project_id, const String &branch_id, const String &display_name);
	void stop();

	// The secret for a server (sign-in token, invite code or server key),
	// stored per server address outside the project. Scripts can set or
	// replace it but never read it back.
	void set_access_key(const String &url, const String &key);
	bool has_access_key(const String &url) const;

	int get_state() const { return int(state_); }
	String get_status_text() const;
	Dictionary get_status() const;
	Dictionary get_defaults() const;

	Dictionary get_local_peer() const;
	Array get_peers() const;
	Array get_overlay_2d() const;
	Array get_overlay_3d() const;

	void set_pointer_2d(const Vector2 &viewport_position);
	void set_pointer_3d(Camera3D *camera, const Vector2 &viewport_position);
	void clear_pointer();
	void set_main_screen(const String &name);

	void notify_history_changed();
	void notify_scene_saved(const String &path);
	void notify_scene_closed(const String &path);
	void notify_scene_changed();
	void notify_resource_saved(const Ref<Resource> &res);

	void follow(const String &peer_id);
	void unfollow();
	// "Bring everyone here": ask everyone in the project to follow you.
	void summon();
	String get_follow_target() const;
	Dictionary get_follow_view() const;

	void set_allow_builtin_scripts(bool allow);
	bool get_allow_builtin_scripts() const;

	// The add-on version this core belongs to (plugin.cfg), and the server's.
	String get_version() const;

	// Chat and comments (social.md). The UI owns their state; the session
	// carries requests and events. Returns the request id ("" when offline or
	// the server has no chat).
	String social_send(const String &kind, const Dictionary &body);
	bool has_social() const;
	String get_member_id() const { return member_id_.to_string(); }
	// The color a person has everywhere (cursor, avatar, comment pins).
	Color get_color_for(const String &name) const { return yhde::YhdePresence::color_for(name); }
	String get_initials_for(const String &name) const { return yhde::YhdePresence::initials_for(name); }

	// Live co-editing (text_editing.md): the UI maps script editor tabs to
	// files and binds their CodeEdits here.
	void text_bind(Node *code_edit, const String &path);
	void text_unbind(Node *code_edit);
	bool is_text_live(const String &path) const { return text_.is_bound(path); }
	// Others' carets and selections in a text file: [{name, color, caret, sel, typing}].
	Array get_script_carets(const String &path) const;

	// First connection of this folder to a project whose files look nothing
	// like it: nothing is exchanged until someone confirms (projects.md).
	void confirm_join();
	String get_project_name() const { return project_name_; }

	// Node identity for comments pinned to nodes: stable across editors.
	String get_node_id(Node *node) const;
	Node *find_node_by_id(const String &scene_path, const String &id) const;

	// Files that vanished all at once, held until someone decides.
	PackedStringArray get_held_deletions() const { return asset_.held_deletions(); }
	void confirm_deletions() { asset_.confirm_deletions(); }
	Array get_held_code() const { return asset_.held_code(); }
	void accept_held_code() { asset_.accept_code(); }
	void set_trust_code(bool on) { asset_.set_trust_code(on); }
	void restore_deletions() { asset_.restore_deletions(); }

	// Test support (YHDE_AUDIT): what this editor sent, per editor action.
	Array take_audit();
	int64_t get_last_action_group() const { return int64_t(last_group_); }

protected:
	static void _bind_methods();

private:
	double now() const;
	void set_state(State s);
	String access_keys_path() const;
	void load_member_id();
	Dictionary load_access_keys() const;
	void emit_notice(const String &text, int level);

	void on_open();
	void on_closed(const String &reason);
	void on_frame(const yhde::proto::Frame &frame);
	void subscribe();
	void drain();
	void process_committed(std::vector<yhde::proto::CommittedOp> &ops);
	void on_rejected(const yhde::proto::OpRejected &msg);
	yhde::Uuid emit_local(yhde::LocalOp &&op, uint64_t group, const yhde::Uuid &batch = yhde::Uuid(), bool send = true,
			const std::string &blob = std::string(), bool before_doc = false);
	// Committed ops waiting for asset bytes (or for ops ahead of them that
	// wait), in log order: they are applied once the bytes are here.
	std::deque<yhde::proto::CommittedOp> held_;
	bool parse_committed(const yhde::proto::CommittedOp &op, yhde::RemoteOp &out);
	void check_caught_up();
	std::function<void(const yhde::RemoteOp &op, const String &subject)> activity_hook_;
	// Sends one undo/redo as a single request (operation_system.md).
	void submit_undo(std::vector<yhde::LocalOp> &ops, uint64_t group, const std::vector<yhde::Uuid> &undo_of, const char *kind);
	void on_undo_result(const yhde::proto::UndoResult &result);
	void send_pending();
	void send_pending_pass();
	bool sending_ = false;
	bool send_again_ = false;
	void send_ack(bool force);
	void tick_presence();
	void handle_closed_docs();
	void connect_editor_signals();
	void disconnect_editor_signals();
	// Every undo history (one per open scene plus the global one) is watched
	// directly: each commit, undo and redo is captured the moment it happens,
	// so one editor action becomes one group of operations.
	void watch_scene_histories(bool look_for_new = true);
	void on_history_event(int64_t history_id);
	void handle_history_event(int64_t history_id, bool missed);
	void on_manager_history_changed();
	void on_manager_version_changed();
	void catch_up_histories(bool look_for_new);
	std::vector<String> docs_for_history(int64_t history_id) const;
	double last_history_watch_ = 0.0;
	void on_inspected_changed();

	struct JournalOp {
		yhde::Uuid op_id;
		yhde::LocalOp op;
	};
	// One editor action as the server knows it.
	struct ActionRecord {
		uint64_t group = 0;
		std::vector<uint64_t> aliases; // groups of its undos and redos
		std::vector<JournalOp> done;   // ops that put its effect in place (commit or redo)
		std::vector<JournalOp> undone; // ops of its latest undo
		bool is_undone = false;
	};
	struct HistoryState {
		uint64_t object = 0; // the UndoRedo instance
		uint64_t version = 0;
		int count = 0;
		int current = -1;
		std::vector<ActionRecord> actions; // parallel to the history's actions
	};
	std::map<int64_t, HistoryState> histories_;
	std::map<int64_t, String> history_paths_; // scene history id -> document
	struct GroupRef {
		int64_t history = 0;
		size_t index = 0;
		bool undo = false;
	};
	std::unordered_map<uint64_t, GroupRef> groups_;
	uint64_t next_group_ = 1;
	uint64_t last_group_ = 0;
	bool audit_ = false;
	bool debug_ = false;
	Array audit_log_;
	ActionRecord *record_for(uint64_t group, bool *undo);

	Dictionary peer_dict(const yhde::Peer &p) const;
	Array resolve_selection(const yhde::Peer &p) const;

	yhde::YhdeNetworking net_;
	yhde::YhdeOperationQueue queue_;
	yhde::YhdeLocalCache cache_;
	yhde::SyncEngine engine_;
	yhde::YhdePresence presence_;
	yhde::AssetSync asset_;
	yhde::TextSync text_;
	void switch_project(const yhde::Uuid &project, const yhde::Uuid &branch);
	bool check_join();
	String storage_dir_;
	String project_name_;
	bool fresh_join_ = false;
	bool join_pending_ = false;

	State state_ = STATE_OFFLINE;
	String last_status_text_;
	String url_;
	String offline_reason_; // why we went offline on our own (shown by the UI)
	yhde::Uuid project_id_;
	yhde::Uuid branch_id_;
	String display_name_;
	yhde::Uuid session_id_;
	yhde::Uuid member_id_;
	std::vector<std::string> server_capabilities_;
	std::string server_version_;

	bool syncing_ = false;
	int64_t next_seq_ = 1;
	int64_t head_seq_ = 0;
	int64_t catchup_head_ = 0; // ops up to here arrived as catch-up history
	std::map<int64_t, yhde::proto::CommittedOp> buffer_;
	double gap_since_ = -1.0;
	double stalled_since_ = -1.0;
	int64_t last_ack_sent_ = 0;
	double last_ack_time_ = 0.0;
	double last_flush_ = 0.0;
	bool resync_requested_ = false;
	int64_t authored_ = 0;       // scene/resource operations this editor produced (diagnostics)
	int64_t authored_files_ = 0; // file operations this editor produced

	// Local presence.
	String main_screen_ = "2D";
	Array pointer_;
	double last_presence_sent_ = -100.0;
	String last_presence_json_;
	String last_presence_scene_;
	String follow_target_;
	String summon_;          // id of your latest "bring everyone here"
	double summon_at_ = -1e9;
	bool signals_connected_ = false;

	// Activity is summarized per batch: one entry per (person, object).
	struct ActivityItem {
		String actor;
		String doc;
		String subject;
		String node;
		String where; // " on Player" / " in mat.tres"
		PackedStringArray props;
		int changes = 0;
		bool properties_only = true;
	};
	std::vector<ActivityItem> activity_batch_;
	void flush_activity();
	std::map<String, double> recent_notices_;
	int notices_this_second_ = 0;
	double notice_window_ = 0.0;
};

} // namespace godot

VARIANT_ENUM_CAST(YhdeSession::State);
VARIANT_ENUM_CAST(YhdeSession::NoticeLevel);
