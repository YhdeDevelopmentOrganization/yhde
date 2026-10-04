#include "operation_queue/yhde_operation_queue.h"

#include "sync/variant_codec.h"

#include <godot_cpp/classes/dir_access.hpp>
#include <godot_cpp/classes/file_access.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/dictionary.hpp>

using namespace godot;

namespace yhde {

std::string YhdeOperationQueue::mask_key(const String &doc, const Uuid &target, const String &key) {
	return (doc + String("\x1f") + target.to_string() + String("\x1f") + key).utf8().get_data();
}

void YhdeOperationQueue::index(const PendingOp &op, int delta) {
	std::string k = mask_key(op.doc, op.target, op.key);
	int &count = masks_[k];
	count += delta;
	if (count <= 0) masks_.erase(k);
}

void YhdeOperationQueue::configure(const String &file_path) {
	path_ = file_path;
	ops_.clear();
	masks_.clear();
	batches_.clear();
	dirty_ = false;
}

void YhdeOperationQueue::clear() {
	ops_.clear();
	masks_.clear();
	batches_.clear();
	dirty_ = true;
}

void YhdeOperationQueue::add_batch(UndoBatch batch) {
	Uuid id = batch.request_id;
	batches_[id] = std::move(batch);
	dirty_ = true;
}

const UndoBatch *YhdeOperationQueue::batch(const Uuid &request_id) const {
	auto it = batches_.find(request_id);
	return it == batches_.end() ? nullptr : &it->second;
}

void YhdeOperationQueue::unbatch(const Uuid &request_id) {
	for (PendingOp &op : ops_) {
		if (op.batch == request_id) {
			op.batch = Uuid();
			op.sent = false;
		}
	}
	batches_.erase(request_id);
	dirty_ = true;
}

void YhdeOperationQueue::drop_batch(const Uuid &request_id) {
	if (batches_.erase(request_id)) dirty_ = true;
}

void YhdeOperationQueue::load() {
	ops_.clear();
	masks_.clear();
	batches_.clear();
	if (path_.is_empty() || !FileAccess::file_exists(path_)) return;
	Ref<FileAccess> f = FileAccess::open(path_, FileAccess::READ);
	if (f.is_null()) return;
	Variant data;
	if (!from_json(f->get_as_text(), data)) return;
	Array items;
	if (data.get_type() == Variant::ARRAY) {
		items = data; // first format: a bare list of operations
	} else if (data.get_type() == Variant::DICTIONARY) {
		Dictionary root = data;
		items = root.get("ops", Array());
		Array batches = root.get("batches", Array());
		for (int64_t i = 0; i < batches.size(); i++) {
			if (batches[i].get_type() != Variant::DICTIONARY) continue;
			Dictionary b = batches[i];
			UndoBatch batch;
			if (!Uuid::parse(b.get("request", String()), batch.request_id)) continue;
			batch.kind = String(b.get("kind", String())).utf8().get_data();
			Array of = b.get("of", Array());
			for (int64_t k = 0; k < of.size(); k++) {
				Uuid id;
				if (Uuid::parse(of[k], id)) batch.undo_of.push_back(id);
			}
			batches_[batch.request_id] = std::move(batch);
		}
	} else {
		return;
	}
	for (int64_t i = 0; i < items.size(); i++) {
		if (items[i].get_type() != Variant::DICTIONARY) continue;
		Dictionary d = items[i];
		PendingOp op;
		if (!Uuid::parse(d.get("op_id", String()), op.op_id)) continue;
		if (!Uuid::parse(d.get("ref", String()), op.client_op_ref)) continue;
		if (!Uuid::parse(d.get("target", String()), op.target)) continue;
		op.type = String(d.get("type", String())).utf8().get_data();
		op.doc = d.get("doc", String());
		op.key = d.get("key", String());
		op.payload_json = String(d.get("payload", String())).utf8().get_data();
		op.parent_seq = int64_t(double(d.get("parent_seq", 0.0)));
		op.sent = false;
		op.applied_locally = false;
		Uuid::parse(d.get("batch", String()), op.batch);
		op.blob = String(d.get("blob", String())).utf8().get_data();
		if (!op.batch.is_nil() && !batches_.count(op.batch)) op.batch = Uuid();
		if (op.type.empty() || op.payload_json.empty()) continue;
		index(op, +1);
		ops_.push_back(std::move(op));
	}
}

void YhdeOperationQueue::flush_if_dirty() {
	if (!dirty_ || path_.is_empty()) return;
	dirty_ = false;
	DirAccess::make_dir_recursive_absolute(path_.get_base_dir());
	if (ops_.empty()) {
		if (FileAccess::file_exists(path_)) DirAccess::remove_absolute(path_);
		return;
	}
	Array items;
	for (const PendingOp &op : ops_) {
		Dictionary d;
		d["op_id"] = op.op_id.to_string();
		d["ref"] = op.client_op_ref.to_string();
		d["type"] = String::utf8(op.type.c_str());
		d["target"] = op.target.to_string();
		d["doc"] = op.doc;
		d["key"] = op.key;
		d["payload"] = String::utf8(op.payload_json.c_str(), int64_t(op.payload_json.size()));
		d["parent_seq"] = op.parent_seq;
		if (!op.batch.is_nil()) d["batch"] = op.batch.to_string();
		if (!op.blob.empty()) d["blob"] = String::utf8(op.blob.c_str());
		items.push_back(d);
	}
	Array batches;
	for (const auto &kv : batches_) {
		Dictionary b;
		b["request"] = kv.first.to_string();
		b["kind"] = String::utf8(kv.second.kind.c_str());
		Array of;
		for (const Uuid &id : kv.second.undo_of) of.push_back(id.to_string());
		b["of"] = of;
		batches.push_back(b);
	}
	Dictionary root;
	root["v"] = 2;
	root["ops"] = items;
	root["batches"] = batches;
	// Write-then-rename so a crash mid-write never corrupts the queue.
	String tmp = path_ + ".tmp";
	{
		Ref<FileAccess> f = FileAccess::open(tmp, FileAccess::WRITE);
		if (f.is_null()) {
			dirty_ = true;
			return;
		}
		f->store_string(to_json(root));
		f->flush();
	}
	DirAccess::rename_absolute(tmp, path_);
}

void YhdeOperationQueue::push(PendingOp op) {
	index(op, +1);
	ops_.push_back(std::move(op));
	dirty_ = true;
}

void YhdeOperationQueue::push_before_doc(PendingOp op, const String &doc) {
	index(op, +1);
	auto at = ops_.begin();
	while (at != ops_.end() && (at->sent || at->doc != doc)) ++at;
	ops_.insert(at, std::move(op));
	dirty_ = true;
}

bool YhdeOperationQueue::take(const Uuid &client_op_ref, PendingOp *out) {
	for (auto it = ops_.begin(); it != ops_.end(); ++it) {
		if (it->client_op_ref != client_op_ref) continue;
		index(*it, -1);
		if (out) *out = std::move(*it);
		ops_.erase(it);
		dirty_ = true;
		return true;
	}
	return false;
}

PendingOp *YhdeOperationQueue::find(const Uuid &client_op_ref) {
	for (PendingOp &op : ops_) {
		if (op.client_op_ref == client_op_ref) return &op;
	}
	return nullptr;
}

bool YhdeOperationQueue::masks(const String &doc, const Uuid &target, const String &key) const {
	return masks_.count(mask_key(doc, target, key)) > 0;
}

void YhdeOperationQueue::mark_not_applied(const String &doc) {
	for (PendingOp &op : ops_) {
		if (op.doc == doc) op.applied_locally = false;
	}
}

void YhdeOperationQueue::mark_all_unsent() {
	for (PendingOp &op : ops_) op.sent = false;
}

} // namespace yhde
