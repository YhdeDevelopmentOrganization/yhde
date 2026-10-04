#pragma once

#include "core/uuid.h"

#include <godot_cpp/variant/string.hpp>

#include <cstdint>
#include <deque>
#include <string>
#include <unordered_map>
#include <vector>

namespace yhde {

// A locally authored operation the server has not acknowledged yet.
struct PendingOp {
	Uuid op_id;
	Uuid client_op_ref;
	std::string type;
	Uuid target;
	godot::String doc;
	godot::String key; // what it overwrites (see op_key_for)
	std::string payload_json;
	int64_t parent_seq = 0;
	bool sent = false;
	// False for ops restored after a crash: the local scene was reloaded from
	// disk and does not contain them yet, so their commit must be applied.
	bool applied_locally = true;
	// Part of an undo/redo request (sent together, committed atomically).
	Uuid batch;
	// Asset operations: the content hash that must be uploaded first.
	std::string blob;
};

// An undo or redo of one editor action: its operations travel together in one
// UndoRequest that names the operations they undo (operation_system.md).
struct UndoBatch {
	Uuid request_id;
	std::string kind; // "undo" | "redo"
	std::vector<Uuid> undo_of;
};

// The client-side, persisted operation queue (operation_system.md,
// reliability.md): holds unacknowledged ops in send order, survives editor
// crashes, and is replayed with the original op ids after a reconnect so the
// server can de-duplicate.
class YhdeOperationQueue {
public:
	void configure(const godot::String &file_path);
	// Restores ops persisted by a previous run (marked not-applied-locally).
	void load();
	void flush_if_dirty();
	void clear();

	void push(PendingOp op);
	// Ahead of the first unsent op of `doc` (a file registered only after its
	// document was already being edited).
	void push_before_doc(PendingOp op, const godot::String &doc);
	bool take(const Uuid &client_op_ref, PendingOp *out);
	PendingOp *find(const Uuid &client_op_ref);

	// Is a local, unacknowledged change pending for this (document, target, key)?
	bool masks(const godot::String &doc, const Uuid &target, const godot::String &key) const;

	std::deque<PendingOp> &ops() { return ops_; }

	void add_batch(UndoBatch batch);
	const UndoBatch *batch(const Uuid &request_id) const;
	// The server refused the request as an undo: its operations go out as
	// ordinary edits instead (the local state already shows them).
	void unbatch(const Uuid &request_id);
	void drop_batch(const Uuid &request_id);
	void mark_all_unsent();
	// The document was reloaded from disk: its pending ops are no longer in memory.
	void mark_not_applied(const godot::String &doc);
	void mark_dirty() { dirty_ = true; }
	size_t size() const { return ops_.size(); }
	bool empty() const { return ops_.empty(); }

private:
	static std::string mask_key(const godot::String &doc, const Uuid &target, const godot::String &key);
	void index(const PendingOp &op, int delta);

	std::deque<PendingOp> ops_;
	std::unordered_map<Uuid, UndoBatch, UuidHash> batches_;
	std::unordered_map<std::string, int> masks_;
	godot::String path_;
	bool dirty_ = false;
};

} // namespace yhde
