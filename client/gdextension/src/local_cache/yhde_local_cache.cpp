#include "local_cache/yhde_local_cache.h"

#include "sync/variant_codec.h"

#include <godot_cpp/classes/dir_access.hpp>
#include <godot_cpp/classes/file_access.hpp>
#include <godot_cpp/variant/dictionary.hpp>

#include <algorithm>

using namespace godot;

namespace yhde {

void YhdeLocalCache::configure(const String &file_path) {
	path_ = file_path;
	applied_seq_ = 0;
	docs_.clear();
	dirty_ = false;
}

void YhdeLocalCache::reset() {
	applied_seq_ = 0;
	docs_.clear();
	dirty_ = true;
}

void YhdeLocalCache::load() {
	applied_seq_ = 0;
	docs_.clear();
	if (path_.is_empty() || !FileAccess::file_exists(path_)) return;
	Ref<FileAccess> f = FileAccess::open(path_, FileAccess::READ);
	if (f.is_null()) return;
	Variant data;
	if (!from_json(f->get_as_text(), data) || data.get_type() != Variant::DICTIONARY) return;
	Dictionary d = data;
	applied_seq_ = int64_t(double(d.get("applied_seq", 0.0)));
	Dictionary docs = d.get("docs", Dictionary());
	Array keys = docs.keys();
	for (int64_t i = 0; i < keys.size(); i++) {
		Dictionary e = docs[keys[i]];
		DocSeq s;
		s.applied = int64_t(double(e.get("applied", 0.0)));
		s.persisted = int64_t(double(e.get("persisted", 0.0)));
		docs_[String(keys[i])] = s;
	}
}

void YhdeLocalCache::flush_if_dirty() {
	if (!dirty_ || path_.is_empty()) return;
	dirty_ = false;
	Dictionary d;
	d["applied_seq"] = applied_seq_;
	Dictionary docs;
	for (const auto &kv : docs_) {
		Dictionary e;
		e["applied"] = kv.second.applied;
		e["persisted"] = kv.second.persisted;
		docs[kv.first] = e;
	}
	d["docs"] = docs;
	DirAccess::make_dir_recursive_absolute(path_.get_base_dir());
	String tmp = path_ + ".tmp";
	{
		Ref<FileAccess> f = FileAccess::open(tmp, FileAccess::WRITE);
		if (f.is_null()) {
			dirty_ = true;
			return;
		}
		f->store_string(to_json(d));
		f->flush();
	}
	DirAccess::rename_absolute(tmp, path_);
}

void YhdeLocalCache::set_applied_seq(int64_t seq) {
	if (seq == applied_seq_) return;
	applied_seq_ = seq;
	dirty_ = true;
}

int64_t YhdeLocalCache::doc_applied(const String &doc) const {
	auto it = docs_.find(doc);
	return it == docs_.end() ? 0 : it->second.applied;
}

void YhdeLocalCache::note_applied(const String &doc, int64_t seq, bool persisted) {
	DocSeq &s = docs_[doc];
	s.applied = std::max(s.applied, seq);
	if (persisted) s.persisted = std::max(s.persisted, seq);
	dirty_ = true;
}

void YhdeLocalCache::note_saved(const String &doc) {
	auto it = docs_.find(doc);
	if (it == docs_.end() || it->second.persisted == it->second.applied) return;
	it->second.persisted = it->second.applied;
	dirty_ = true;
}

bool YhdeLocalCache::note_discarded(const String &doc) {
	auto it = docs_.find(doc);
	if (it == docs_.end() || it->second.applied == it->second.persisted) return false;
	it->second.applied = it->second.persisted;
	dirty_ = true;
	return true;
}

bool YhdeLocalCache::has_unsaved(const String &doc) const {
	auto it = docs_.find(doc);
	return it != docs_.end() && it->second.applied > it->second.persisted;
}

int64_t YhdeLocalCache::resume_seq() const {
	int64_t seq = applied_seq_;
	for (const auto &kv : docs_) {
		if (kv.second.applied > kv.second.persisted) seq = std::min(seq, kv.second.persisted);
	}
	return seq;
}

} // namespace yhde
