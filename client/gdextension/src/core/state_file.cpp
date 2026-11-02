#include "core/state_file.h"

#include "sync/variant_codec.h"

#include <godot_cpp/classes/dir_access.hpp>
#include <godot_cpp/classes/file_access.hpp>
#include <godot_cpp/classes/project_settings.hpp>
#include <godot_cpp/classes/time.hpp>
#include <godot_cpp/variant/char_string.hpp>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#else
#include <fcntl.h>
#include <unistd.h>
#endif

using namespace godot;

namespace yhde {

namespace {

constexpr double kBackupInterval = 30.0;

double now_seconds() {
	return double(Time::get_singleton()->get_ticks_msec()) / 1000.0;
}

// Godot's flush only hands the bytes to the system; this waits for the disk.
void sync_to_disk(const String &path) {
	String global = ProjectSettings::get_singleton()->globalize_path(path);
#ifdef _WIN32
	Char16String wide = global.replace("/", "\\").utf16();
	HANDLE file = CreateFileW(reinterpret_cast<const wchar_t *>(wide.get_data()), GENERIC_WRITE,
			FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
	if (file == INVALID_HANDLE_VALUE) return;
	FlushFileBuffers(file);
	CloseHandle(file);
#else
	int file = ::open(global.utf8().get_data(), O_WRONLY);
	if (file < 0) return;
	::fsync(file);
	::close(file);
#endif
}

} // namespace

void StateFile::configure(const String &path) {
	path_ = path;
	last_backup_ = now_seconds();
	file_good_ = false;
}

bool StateFile::load(Variant &out, const std::function<bool(const Variant &)> &valid) {
	file_good_ = false;
	from_backup_ = false;
	auto read = [&](const String &path) {
		if (!FileAccess::file_exists(path)) return false;
		Ref<FileAccess> f = FileAccess::open(path, FileAccess::READ);
		if (f.is_null()) return false;
		Variant data;
		if (!from_json(f->get_as_text(), data) || !valid(data)) return false;
		out = data;
		return true;
	};
	if (read(path_)) {
		file_good_ = true;
		return true;
	}
	from_backup_ = read(path_ + String(".bak"));
	return from_backup_;
}

bool StateFile::exists() const {
	return FileAccess::file_exists(path_) || FileAccess::file_exists(path_ + String(".bak"));
}

bool StateFile::save(const String &text) {
	if (path_.is_empty()) return false;
	DirAccess::make_dir_recursive_absolute(path_.get_base_dir());
	// Written beside it, checked and flushed, then moved over it.
	String tmp = path_ + String(".tmp");
	int64_t bytes = text.utf8().length();
	{
		Ref<FileAccess> f = FileAccess::open(tmp, FileAccess::WRITE);
		if (f.is_null() || !f->store_string(text)) return false;
		f->flush();
		if (f->get_error() != OK) return false;
	}
	if (int64_t(FileAccess::get_size(tmp)) != bytes) {
		DirAccess::remove_absolute(tmp);
		return false;
	}
	double now = now_seconds();
	String backup = path_ + String(".bak");
	if (file_good_ && FileAccess::file_exists(path_) && (!FileAccess::file_exists(backup) || now - last_backup_ >= kBackupInterval)) {
		sync_to_disk(path_);
		DirAccess::remove_absolute(backup);
		DirAccess::rename_absolute(path_, backup);
		last_backup_ = now;
	}
	if (DirAccess::rename_absolute(tmp, path_) != OK) return false;
	file_good_ = true;
	return true;
}

void StateFile::remove() {
	if (path_.is_empty()) return;
	DirAccess::remove_absolute(path_);
	DirAccess::remove_absolute(path_ + String(".bak"));
	file_good_ = false;
}

} // namespace yhde
