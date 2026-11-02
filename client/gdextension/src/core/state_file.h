#pragma once

#include <godot_cpp/variant/string.hpp>
#include <godot_cpp/variant/variant.hpp>

#include <functional>

namespace yhde {

// A small JSON file holding this editor's sync state, rewritten up to twice a
// second. Writing a file does not put it on the disk: the system does that
// some seconds later, so a sudden power loss can leave what was written lately
// empty or full of zeros, moved-over copies included. A backup rotated at the
// same pace dies with the file (reliability.md, "Sudden Power Loss"), so the
// backup here is only replaced every kBackupInterval, by a copy first forced
// onto the disk: after a crash at most that much work is fetched again.
class StateFile {
public:
	void configure(const godot::String &path);

	// The file, else its backup. `valid` decides whether what was read is
	// usable. False when neither is.
	bool load(godot::Variant &out, const std::function<bool(const godot::Variant &)> &valid);
	// Whether the file or its backup exists, readable or not.
	bool exists() const;
	// The last load() came from the backup: up to kBackupInterval old.
	bool from_backup() const { return from_backup_; }

	bool save(const godot::String &text);
	// Nothing to keep: the file and its backup go.
	void remove();

private:
	godot::String path_;
	double last_backup_ = 0.0;
	// The file on disk is ours and whole: only such a file may become the backup.
	bool file_good_ = false;
	bool from_backup_ = false;
};

} // namespace yhde
