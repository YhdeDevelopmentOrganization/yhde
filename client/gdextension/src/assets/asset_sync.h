#pragma once

#include "assets/asset_worker.h"
#include "core/uuid.h"
#include "sync/scene_document.h"
#include "sync/sync_engine.h"

#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/packed_string_array.hpp>
#include <godot_cpp/variant/string.hpp>

#include <functional>
#include <map>
#include <set>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace yhde {

// Keeps the project's files identical across editors (assets.md): textures,
// models, audio, scripts, shaders, project.godot, and the bytes of scenes and
// resources when they appear or change outside the editor. Edits made to
// open scenes and resources keep travelling as operations (SyncEngine); this
// class gives every editor the same files underneath them.
//
// The log is the authority: an asset operation names the content hash a path
// holds; the bytes come from the server's blob store. A local change is
// uploaded first and only then enters the log, so every editor that sees the
// operation can fetch its bytes.
class AssetSync {
public:
	struct Hooks {
		// Queue an operation; `blob` must be on the server before it is sent.
		// `before_doc`: place it ahead of already queued ops of that document.
		std::function<Uuid(LocalOp &&op, const std::string &blob, bool before_doc)> emit;
		// An emitted operation can never be sent (its file changed before the upload).
		std::function<void(const Uuid &client_op_ref)> drop;
		std::function<void(const godot::String &text, int level)> notice;
		// Does an open, synchronized document cover this file?
		std::function<bool(const godot::String &path)> is_document;
		// A document's file got new bytes: reload it wherever it is open.
		std::function<void(const godot::String &path, int64_t seq)> replaced;
		std::function<void(const RemoteOp &op, const godot::String &subject)> activity;
	};

	void set_hooks(Hooks hooks) { hooks_ = std::move(hooks); }
	void set_debug(bool on) { debug_ = on; }
	void configure(const godot::String &state_file);
	void load();
	void flush_if_dirty();
	void reset();

	void start(const std::string &server_url, const std::string &authorization);
	void stop();
	bool active() const { return active_; }

	// Before subscribing: files changed or deleted while offline that the log
	// already knows are queued now, so they win over what catch-up brings.
	void queue_offline_changes();
	// Every catch-up operation has been seen: new files can be registered.
	void set_caught_up();
	bool caught_up() const { return caught_up_; }
	void process(double now);

	static bool is_asset_op(const std::string &type);

	// Outgoing.
	bool blob_ready(const std::string &hash) const { return remote_blobs_.count(hash) > 0; }
	void restore_pending(const std::string &type, const godot::Dictionary &payload, const Uuid &client_op_ref);
	void on_rejected(const godot::Dictionary &payload, const Uuid &client_op_ref, const std::string &code);

	// Incoming, in log order. note_committed() once per operation when it is
	// first seen; ready() until it says yes (it starts fetching the bytes);
	// then apply(). finish_batch() after a round of applies.
	void note_committed(const RemoteOp &op);
	// Applied here already (re-delivered after a re-sync).
	bool stale(const RemoteOp &op) const;
	bool ready(const RemoteOp &op);
	void apply(const RemoteOp &op);
	void finish_batch();
	// Something became ready (a download finished) since the last call.
	bool take_progress();
	// A new subscription re-delivers everything not applied yet.
	void clear_incoming() { incoming_.clear(); }

	// Documents.
	void ensure_registered(const godot::String &path);
	void absorb(const godot::String &path);
	void editor_saved(const godot::String &path);

	// An operation about to be sent refers to this project file. True when it
	// must wait: the file is not in the log yet (it is registered now, or as
	// soon as the editor has imported it) or its registration is not sent.
	// `pending_ref` receives the registering operation to wait for, if any.
	bool reference_waits(const godot::String &path, Uuid *pending_ref);

	// Is this file in the log with this editor's copy in sync (its live text
	// can start from the disk)? `seq` receives the log seq of its bytes.
	bool text_base(const godot::String &path, int64_t *seq) const;

	// Many files vanished at once (a folder deleted by mistake, a wrong
	// checkout): their deletion is held until someone confirms it.
	godot::PackedStringArray held_deletions() const;
	void confirm_deletions();
	void restore_deletions();

	// Files from someone else that can run code inside this editor (native
	// extensions and libraries, build files, @tool scripts) are not written
	// until this person accepts them (security.md): connecting to a project or
	// a server must never be enough to run someone else's code here. Bytes
	// already in this project (a move, a copy) are never held.
	godot::Array held_code() const; // [{path, by}]
	void accept_code();
	// "Trust this project": write them like any other file.
	void set_trust_code(bool on);
	// Could this file, with these bytes, run code in the editor?
	static bool is_native_or_build_file(const godot::String &path);
	static bool may_run_in_editor(const godot::String &path, const godot::String &bytes_file);

	godot::Dictionary status() const;

	// Paths grouped with their source file: art/a.png.import -> art/a.png.
	static godot::String key_of(const godot::String &path);
	static bool excluded(const godot::String &path);
	// The YHDE add-on itself and project.godot can never be removed through
	// the log: losing either would disconnect or break every editor.
	static bool protected_from_delete(const godot::String &path);
	// Imported results kept in .godot/imported/ that are shared because
	// producing them needs a tool not every teammate has (Blender, FBX2glTF).
	static bool is_imported_output(const godot::String &path);
	// res:// project files an asset operation's file depends on ("d").
	static godot::PackedStringArray dependencies_of(const godot::Dictionary &payload);

private:
	struct LogEntry {
		std::string hash;
		int64_t size = 0;
		int64_t seq = 0;
	};
	struct DiskEntry {
		std::string hash;
		int64_t size = 0;
		int64_t mtime = 0;
		std::string base; // the log hash this content stands for ("" = a local change)
	};
	struct Pending {
		std::string hash; // "" = delete
		Uuid ref;
	};
	struct HeldCode {
		std::string hash;
		int64_t size = 0;
		int64_t seq = 0;
		godot::String by;
		bool accepted = false;
	};

	void scan_step(double now);
	void finish_pass(double now);
	void observe(const godot::String &path, int64_t size, int64_t mtime, double now);
	void on_result(AssetResult &r, double now);
	void evaluate(const godot::String &path, double now, bool urgent, int depth = 0);
	// Share a file now if the log does not have its current bytes (a
	// companion or dependency that must be in the log before `path`).
	void evaluate_companion(const godot::String &path, double now, int depth);
	bool needs_import_wait(const godot::String &path, double now);
	bool is_importable(const godot::String &path) const;
	static bool shares_import(const godot::String &source);
	static std::vector<godot::String> import_outputs(const godot::String &source);
	std::vector<godot::String> project_dependencies(const godot::String &path) const;
	void process_restores();
	void cancel_hash(const godot::String &path);
	bool have_bytes(const std::string &hash);
	void fetch(const std::string &hash, int64_t size);
	bool hash_now(const godot::String &path, DiskEntry &out) const;
	void emit(const std::string &type, const godot::String &path, const godot::Dictionary &payload, const std::string &blob,
			bool before_doc = false);
	void need_blob(const std::string &hash, const godot::String &path, int64_t size);
	bool local_copy(const std::string &hash, godot::String &from);
	bool write_blob(const godot::String &path, const std::string &hash, int64_t size);
	void remove_file(const godot::String &path, bool backup);
	void backup(const godot::String &path);
	void apply_project_settings(const godot::String &incoming);
	bool needs_approval(const godot::String &path, const std::string &hash);
	void release_accepted_code();
	godot::String incoming_path(const std::string &hash) const;
	static godot::String global(const godot::String &path);
	static std::string std_of(const godot::String &s);
	static godot::String str_of(const std::string &s);

	Hooks hooks_;
	AssetWorker worker_;
	godot::String state_file_;
	bool dirty_ = false;
	bool active_ = false;
	bool caught_up_ = false;
	bool progress_ = false;
	double now_ = 0.0;

	std::map<godot::String, LogEntry> log_;
	std::map<godot::String, DiskEntry> disk_;
	std::map<godot::String, int64_t> applied_; // path -> seq of the last file operation applied here
	std::map<godot::String, Pending> pending_;
	std::map<godot::String, int> incoming_;     // key -> ops seen but not applied yet
	std::set<std::pair<godot::String, std::string>> refused_;
	std::set<godot::String> deferred_;           // documents to register once caught up
	std::unordered_set<std::string> remote_blobs_;

	// Scanning.
	std::vector<godot::String> scan_dirs_;
	std::set<godot::String> scan_seen_;
	bool scan_running_ = false;
	double next_scan_ = 0.0;
	std::map<uint64_t, godot::String> hash_jobs_;
	std::set<godot::String> hashing_;
	std::map<godot::String, int64_t> hashing_mtime_;
	std::map<godot::String, double> waiting_; // new file -> when first seen (waiting for the import)
	bool efs_scan_requested_ = false;
	bool was_busy_ = false;
	bool retry_when_idle_ = false; // something was held for the editor being busy
	bool debug_ = false;
	std::map<godot::String, double> sidecar_wait_; // received sidecar -> first seen without its file
	std::set<godot::String> import_exts_;     // extensions seen with a .import next to them
	std::set<godot::String> held_deletes_;    // mass deletion waiting for confirmation
	std::map<godot::String, HeldCode> held_code_; // code from others waiting for this person's OK
	bool trust_code_ = false;
	std::map<godot::String, std::string> restoring_; // path -> log hash being put back

	// Transfers.
	std::vector<std::pair<std::string, godot::String>> to_check_; // hash, a path holding it
	std::map<std::string, godot::String> upload_source_;
	std::map<uint64_t, std::string> transfer_jobs_;
	std::set<std::string> uploading_;
	std::set<std::string> downloading_;
	std::set<std::string> downloaded_;
	std::map<std::string, double> retry_at_;
	double next_check_ = 0.0;

	// Applied this round, finished in finish_batch().
	std::vector<godot::String> touched_;
	std::vector<std::pair<godot::String, int64_t>> replaced_;
	std::set<std::string> used_incoming_;
	int applied_count_ = 0;
};

} // namespace yhde
