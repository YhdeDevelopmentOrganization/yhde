#pragma once

#include <godot_cpp/variant/string.hpp>

#include <cstdint>
#include <map>

namespace yhde {

// Per-branch bookkeeping of how far the local project reflects the operation
// log (snapshots.md, reliability.md).
//
// For every document (scene or resource file) we track two watermarks:
//   applied: highest seq reflected in the editor's in-memory copy
//   persisted: highest seq reflected in the file on disk
// A document whose in-memory changes were never saved (editor closed or crash)
// is replayed from `persisted` on the next subscription, so nothing is lost
// even though scene files themselves are never synchronized.
class YhdeLocalCache {
public:
	void configure(const godot::String &file_path);
	void load();
	void flush_if_dirty();
	void reset();

	int64_t applied_seq() const { return applied_seq_; }
	void set_applied_seq(int64_t seq);

	int64_t doc_applied(const godot::String &doc) const;
	void note_applied(const godot::String &doc, int64_t seq, bool persisted);
	void note_saved(const godot::String &doc);
	// The in-memory copy was thrown away: re-apply from the persisted mark.
	bool note_discarded(const godot::String &doc);
	bool has_unsaved(const godot::String &doc) const;

	// Where a subscription must resume so every document converges.
	int64_t resume_seq() const;

private:
	struct DocSeq {
		int64_t applied = 0;
		int64_t persisted = 0;
	};

	godot::String path_;
	int64_t applied_seq_ = 0;
	std::map<godot::String, DocSeq> docs_;
	bool dirty_ = false;
};

} // namespace yhde
