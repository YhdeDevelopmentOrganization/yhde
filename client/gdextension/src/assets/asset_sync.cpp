#include "assets/asset_sync.h"

#include "sync/variant_codec.h"

#include <godot_cpp/classes/config_file.hpp>
#include <godot_cpp/classes/dir_access.hpp>
#include <godot_cpp/classes/editor_file_system.hpp>
#include <godot_cpp/classes/editor_interface.hpp>
#include <godot_cpp/classes/file_access.hpp>
#include <godot_cpp/classes/os.hpp>
#include <godot_cpp/classes/project_settings.hpp>
#include <godot_cpp/classes/resource_loader.hpp>
#include <godot_cpp/classes/resource_uid.hpp>
#include <godot_cpp/classes/script.hpp>
#include <godot_cpp/classes/script_editor.hpp>
#include <godot_cpp/classes/time.hpp>
#include <godot_cpp/variant/packed_string_array.hpp>
#include <godot_cpp/variant/utility_functions.hpp>

#include <algorithm>

using namespace godot;

namespace yhde {

namespace {

constexpr double kScanInterval = 1.0;
constexpr int kDirsPerStep = 24;
constexpr double kHotSeconds = 2.0;      // mtime has 1 s resolution: re-hash files this fresh
// A file the editor imports is shared once its .import exists, so its uid is
// the same everywhere. Imports of big models (Blender, FBX) take a while; a
// file whose import never happens (failed, disabled) is shared as is after this.
constexpr double kImportWaitMax = 180.0;
constexpr double kRetryDelay = 5.0;
constexpr size_t kCheckBatch = 2000;
// This many files vanishing in one pass is held for confirmation.
constexpr size_t kMassDelete = 20;
constexpr int kMaxDependencies = 128;
constexpr double kSidecarWait = 30.0; // a received .import/.uid waits this long for its file's operation
const char *kIncomingDir = "res://.godot/yhde/incoming";
const char *kReplacedDir = "res://.godot/yhde/replaced";
const char *kImportedDir = "res://.godot/imported/";

bool is_document_path(const String &p) {
	String ext = p.get_extension().to_lower();
	return ext == "tscn" || ext == "scn" || ext == "tres" || ext == "res";
}

bool is_sidecar(const String &p) {
	return p.ends_with(".import") || p.ends_with(".uid");
}

// What Godot 4.7 imports out of the box. Extensions added by import plugins
// are learned from the .import files found in the project.
bool builtin_importable(const String &ext) {
	static const char *kExts[] = { "png", "jpg", "jpeg", "webp", "svg", "svgz", "bmp", "tga", "hdr", "exr", "dds", "ktx",
		"ktx2", "astc", "pkm", "wav", "ogg", "mp3", "glb", "gltf", "fbx", "blend", "obj", "dae", "ttf", "otf", "woff",
		"woff2", "ttc", "otc", "pfb", "pfm", "fnt", "font", "csv" };
	for (const char *e : kExts) {
		if (ext == e) return true;
	}
	return false;
}

EditorFileSystem *efs() {
	EditorInterface *ei = EditorInterface::get_singleton();
	return ei ? ei->get_resource_filesystem() : nullptr;
}

} // namespace

std::string AssetSync::std_of(const String &s) {
	CharString c = s.utf8();
	return std::string(c.get_data(), size_t(c.length()));
}

String AssetSync::str_of(const std::string &s) {
	return String::utf8(s.c_str(), int64_t(s.size()));
}

String AssetSync::global(const String &path) {
	return ProjectSettings::get_singleton()->globalize_path(path);
}

String AssetSync::key_of(const String &path) {
	if (path.ends_with(".import")) return path.substr(0, path.length() - 7);
	if (path.ends_with(".uid")) return path.substr(0, path.length() - 4);
	return path;
}

bool AssetSync::excluded(const String &path) {
	if (!path.begins_with("res://")) return true;
	String rel = path.substr(6);
	if (rel.is_empty() || rel.contains("..") || rel.contains("\\") || rel.contains(":")) return true;
	// Case-insensitive: on Windows and macOS "Addons/YHDE/..." is the same folder.
	if (rel.to_lower().begins_with("addons/yhde/")) return true;
	if (is_imported_output(path)) return false;
	PackedStringArray parts = rel.split("/");
	for (int64_t i = 0; i + 1 < parts.size(); i++) {
		if (parts[i].begins_with(".")) return true; // .godot, .git, .import, editor folders
	}
	String file = rel.get_file();
	String lower = file.to_lower();
	if (lower == ".ds_store" || lower == "thumbs.db" || lower == "desktop.ini" || lower == "override.cfg") return true;
	if (lower.ends_with(".tmp") || lower.ends_with("~") || lower.ends_with(".swp") || lower.ends_with(".yhde-tmp")) return true;
	if (file.ends_with(".") || file != file.strip_edges()) return true;
	return false;
}

bool AssetSync::is_asset_op(const std::string &type) {
	return type == "RegisterAsset" || type == "UpdateAsset" || type == "MoveAsset" || type == "DeleteAsset";
}

bool AssetSync::protected_from_delete(const String &path) {
	if (path == "res://project.godot") return true;
	return path.to_lower().begins_with("res://addons/yhde/") || path.to_lower() == "res://addons/yhde";
}

bool AssetSync::is_imported_output(const String &path) {
	if (!path.begins_with(kImportedDir)) return false;
	String name = path.substr(String(kImportedDir).length());
	return !name.is_empty() && !name.contains("/") && !name.contains("\\") && !name.begins_with(".") &&
			!name.ends_with(".tmp");
}

PackedStringArray AssetSync::dependencies_of(const Dictionary &payload) {
	PackedStringArray out;
	Variant d = payload.get("d", Variant());
	if (d.get_type() != Variant::ARRAY) return out;
	Array a = d;
	for (int64_t i = 0; i < a.size() && i < kMaxDependencies; i++) {
		if (a[i].get_type() == Variant::STRING) out.push_back(a[i]);
	}
	return out;
}

bool AssetSync::shares_import(const String &source) {
	String ext = source.get_extension().to_lower();
	if (ext == "blend") return true; // needs Blender (and gives different results per Blender version)
	if (ext != "fbx") return false;
	// FBX goes through the external FBX2glTF tool only when chosen per file.
	Ref<ConfigFile> cf;
	cf.instantiate();
	if (cf->load(source + String(".import")) != OK) return false;
	return int64_t(cf->get_value("params", "fbx/importer", 0)) == 1;
}

std::vector<String> AssetSync::import_outputs(const String &source) {
	std::vector<String> out;
	Ref<ConfigFile> cf;
	cf.instantiate();
	if (cf->load(source + String(".import")) != OK) return out;
	Variant dest = cf->get_value("deps", "dest_files", Variant());
	if (dest.get_type() != Variant::PACKED_STRING_ARRAY && dest.get_type() != Variant::ARRAY) return out;
	Array files = dest;
	for (int64_t i = 0; i < files.size(); i++) {
		String f = files[i];
		if (!is_imported_output(f)) continue;
		out.push_back(f);
	}
	// The editor compares this checksum file with the source before deciding
	// to import again: with it, a teammate's editor keeps the shared result.
	String md5 = String(kImportedDir) + source.get_file() + "-" + source.md5_text() + ".md5";
	out.push_back(md5);
	return out;
}

bool AssetSync::is_importable(const String &path) const {
	if (is_sidecar(path) || is_imported_output(path) || is_document_path(path)) return false;
	if (FileAccess::file_exists(path + String(".import"))) return true;
	String ext = path.get_extension().to_lower();
	if (!builtin_importable(ext) && !import_exts_.count(ext)) return false;
	// The editor never imports anything in a folder with a .gdignore.
	for (String dir = path.get_base_dir(); dir.begins_with("res://"); dir = dir.get_base_dir()) {
		if (FileAccess::file_exists(dir.path_join(".gdignore"))) return false;
		if (dir == "res://") break;
	}
	return true;
}

std::vector<String> AssetSync::project_dependencies(const String &path) const {
	std::vector<String> out;
	bool imported = FileAccess::file_exists(path + String(".import"));
	if (!is_document_path(path) && !imported) return out;
	ResourceLoader *loader = ResourceLoader::get_singleton();
	if (!loader->exists(path)) return out;
	PackedStringArray raw = loader->get_dependencies(path);
	for (int64_t i = 0; i < raw.size() && int64_t(out.size()) < kMaxDependencies; i++) {
		// 4.x entries look like "uid://abc::Type::res://path" (older: "res://path::Type").
		PackedStringArray parts = raw[i].split("::");
		String dep;
		for (int64_t k = parts.size() - 1; k >= 0; k--) {
			if (parts[k].begins_with("res://")) {
				dep = parts[k];
				break;
			}
		}
		if (dep.is_empty() && parts.size() > 0 && parts[0].begins_with("uid://")) {
			dep = ResourceUID::uid_to_path(parts[0]);
		}
		if (dep.is_empty() || dep == path || excluded(dep) || is_imported_output(dep) || !FileAccess::file_exists(dep)) continue;
		if (std::find(out.begin(), out.end(), dep) == out.end()) out.push_back(dep);
	}
	return out;
}

// State

void AssetSync::configure(const String &state_file) {
	state_file_ = state_file;
	reset();
	dirty_ = false;
}

void AssetSync::reset() {
	log_.clear();
	disk_.clear();
	applied_.clear();
	pending_.clear();
	incoming_.clear();
	refused_.clear();
	deferred_.clear();
	remote_blobs_.clear();
	held_code_.clear();
	dirty_ = true;
}

void AssetSync::load() {
	log_.clear();
	disk_.clear();
	applied_.clear();
	held_code_.clear();
	if (state_file_.is_empty() || !FileAccess::file_exists(state_file_)) return;
	Ref<FileAccess> f = FileAccess::open(state_file_, FileAccess::READ);
	if (f.is_null()) return;
	Variant data;
	if (!from_json(f->get_as_text(), data) || data.get_type() != Variant::DICTIONARY) return;
	Dictionary d = data;
	Dictionary log = d.get("log", Dictionary());
	Array keys = log.keys();
	for (int64_t i = 0; i < keys.size(); i++) {
		Array e = log[keys[i]];
		if (e.size() < 3) continue;
		LogEntry l;
		l.hash = std_of(e[0]);
		l.size = int64_t(double(e[1]));
		l.seq = int64_t(double(e[2]));
		log_[String(keys[i])] = l;
		remote_blobs_.insert(l.hash);
	}
	Dictionary applied = d.get("applied", Dictionary());
	keys = applied.keys();
	for (int64_t i = 0; i < keys.size(); i++) applied_[String(keys[i])] = int64_t(double(applied[keys[i]]));
	Dictionary disk = d.get("disk", Dictionary());
	keys = disk.keys();
	for (int64_t i = 0; i < keys.size(); i++) {
		Array e = disk[keys[i]];
		if (e.size() < 4) continue;
		DiskEntry de;
		de.hash = std_of(e[0]);
		de.size = int64_t(double(e[1]));
		de.mtime = int64_t(double(e[2]));
		de.base = std_of(e[3]);
		disk_[String(keys[i])] = de;
	}
	Dictionary held = d.get("held", Dictionary());
	keys = held.keys();
	for (int64_t i = 0; i < keys.size(); i++) {
		Array e = held[keys[i]];
		if (e.size() < 5) continue;
		HeldCode h;
		h.hash = std_of(e[0]);
		h.size = int64_t(double(e[1]));
		h.seq = int64_t(double(e[2]));
		h.by = e[3];
		h.accepted = bool(e[4]);
		held_code_[String(keys[i])] = h;
	}
}

void AssetSync::flush_if_dirty() {
	if (!dirty_ || state_file_.is_empty()) return;
	dirty_ = false;
	Dictionary log;
	for (const auto &kv : log_) {
		Array e;
		e.push_back(str_of(kv.second.hash));
		e.push_back(kv.second.size);
		e.push_back(kv.second.seq);
		log[kv.first] = e;
	}
	Dictionary disk;
	for (const auto &kv : disk_) {
		Array e;
		e.push_back(str_of(kv.second.hash));
		e.push_back(kv.second.size);
		e.push_back(kv.second.mtime);
		e.push_back(str_of(kv.second.base));
		disk[kv.first] = e;
	}
	Dictionary applied;
	for (const auto &kv : applied_) applied[kv.first] = kv.second;
	Dictionary held;
	for (const auto &kv : held_code_) {
		Array e;
		e.push_back(str_of(kv.second.hash));
		e.push_back(kv.second.size);
		e.push_back(kv.second.seq);
		e.push_back(kv.second.by);
		e.push_back(kv.second.accepted);
		held[kv.first] = e;
	}
	Dictionary d;
	d["v"] = 1;
	d["log"] = log;
	d["disk"] = disk;
	d["applied"] = applied;
	d["held"] = held;
	DirAccess::make_dir_recursive_absolute(state_file_.get_base_dir());
	String tmp = state_file_ + ".tmp";
	{
		Ref<FileAccess> f = FileAccess::open(tmp, FileAccess::WRITE);
		if (f.is_null()) {
			dirty_ = true;
			return;
		}
		f->store_string(to_json(d));
	}
	DirAccess::rename_absolute(tmp, state_file_);
}

// Lifecycle

void AssetSync::start(const std::string &server_url, const std::string &authorization) {
	stop();
	worker_.start(server_url, authorization);
	active_ = true;
	caught_up_ = false;
	progress_ = false;
	pending_.clear();
	incoming_.clear();
	deferred_.clear();
	scan_running_ = false;
	next_scan_ = 0.0;
	DirAccess::make_dir_recursive_absolute(kIncomingDir);
}

void AssetSync::stop() {
	if (!active_) return;
	worker_.stop();
	active_ = false;
	caught_up_ = false;
	hash_jobs_.clear();
	hashing_.clear();
	hashing_mtime_.clear();
	transfer_jobs_.clear();
	uploading_.clear();
	downloading_.clear();
	to_check_.clear();
	waiting_.clear();
	flush_if_dirty();
}

void AssetSync::set_caught_up() {
	if (caught_up_) return;
	caught_up_ = true;
	next_scan_ = 0.0;
	// Documents opened during catch-up that the log does not know yet.
	std::set<String> deferred;
	deferred.swap(deferred_);
	for (const String &path : deferred) {
		if (log_.count(path) || pending_.count(path) || !FileAccess::file_exists(path)) continue;
		DiskEntry d;
		if (!hash_now(path, d)) continue;
		disk_[path] = d;
		Dictionary p;
		p["s"] = path;
		p["h"] = str_of(d.hash);
		p["n"] = d.size;
		emit("RegisterAsset", path, p, d.hash, true);
	}
}

void AssetSync::queue_offline_changes() {
	for (auto &kv : disk_) {
		const String &path = kv.first;
		DiskEntry &d = kv.second;
		auto l = log_.find(path);
		if (l == log_.end() || d.base.empty() || pending_.count(path) || excluded(path) || held_code_.count(path)) continue;
		if (!FileAccess::file_exists(path)) {
			Dictionary p;
			p["s"] = path;
			p["o"] = str_of(l->second.hash);
			emit("DeleteAsset", path, p, std::string());
			continue;
		}
		if (int64_t(FileAccess::get_modified_time(path)) == d.mtime && int64_t(FileAccess::get_size(path)) == d.size) continue;
		DiskEntry now;
		if (!hash_now(path, now)) continue;
		now.base = now.hash == d.hash ? d.base : std::string();
		d = now;
		dirty_ = true;
		if (d.hash == l->second.hash || d.base == l->second.hash) continue;
		if (hooks_.is_document && hooks_.is_document(path)) continue;
		Dictionary p;
		p["s"] = path;
		p["h"] = str_of(d.hash);
		p["n"] = d.size;
		p["o"] = str_of(l->second.hash);
		emit("UpdateAsset", path, p, d.hash);
	}
}

bool AssetSync::take_progress() {
	bool p = progress_;
	progress_ = false;
	return p;
}

// Per frame

void AssetSync::process(double now) {
	if (!active_) return;
	now_ = now;
	EditorFileSystem *fs = efs();
	bool busy = fs && (fs->is_scanning() || fs->is_importing());
	if (was_busy_ && !busy) {
		progress_ = true; // operations held for the editor's scan can go on
		if (waiting_.empty()) efs_scan_requested_ = false;
	}
	was_busy_ = busy;
	if (retry_when_idle_ && !busy) {
		retry_when_idle_ = false;
		progress_ = true;
	}
	if (!sidecar_wait_.empty()) progress_ = true; // re-check sidecars waiting for their file
	AssetResult r;
	while (worker_.poll(r)) on_result(r, now);

	// Ask which new blobs the server lacks, then upload those.
	if (!to_check_.empty() && now >= next_check_) {
		AssetJob job;
		job.kind = AssetJob::Kind::Missing;
		size_t n = std::min(to_check_.size(), kCheckBatch);
		for (size_t i = 0; i < n; i++) {
			job.hashes.push_back(to_check_[i].first);
			upload_source_[to_check_[i].first] = to_check_[i].second;
		}
		to_check_.erase(to_check_.begin(), to_check_.begin() + long(n));
		transfer_jobs_[worker_.submit(job)] = std::string();
	}

	if (caught_up_) {
		if (!scan_running_ && now >= next_scan_) {
			scan_running_ = true;
			scan_dirs_.clear();
			scan_dirs_.push_back("res://");
			scan_seen_.clear();
		}
		if (scan_running_) scan_step(now);
		// Files that were waiting for the editor to import them.
		std::vector<String> waiting;
		for (const auto &kv : waiting_) waiting.push_back(kv.first);
		for (const String &path : waiting) evaluate(path, now, false);
		if (waiting_.empty()) efs_scan_requested_ = false;
	}
	if (!restoring_.empty()) process_restores();
	if (!busy) release_accepted_code();
}

void AssetSync::scan_step(double now) {
	EditorFileSystem *fs = efs();
	int budget = kDirsPerStep;
	while (!scan_dirs_.empty() && budget-- > 0) {
		String dir = scan_dirs_.back();
		scan_dirs_.pop_back();
		PackedStringArray dirs = DirAccess::get_directories_at(dir);
		for (int64_t i = 0; i < dirs.size(); i++) {
			if (dirs[i].begins_with(".")) continue;
			String sub = dir.path_join(dirs[i]);
			if (sub == "res://addons/yhde") continue;
			// Folders with a .gdignore are skipped by the editor; they still
			// belong to the project and are shared.
			scan_dirs_.push_back(sub);
		}
		PackedStringArray files = DirAccess::get_files_at(dir);
		for (int64_t i = 0; i < files.size(); i++) {
			String path = dir.path_join(files[i]);
			if (path.ends_with(".import")) import_exts_.insert(key_of(path).get_extension().to_lower());
			if (excluded(path)) continue;
			scan_seen_.insert(path);
			observe(path, int64_t(FileAccess::get_size(path)), int64_t(FileAccess::get_modified_time(path)), now);
		}
	}
	(void)fs;
	if (scan_dirs_.empty()) finish_pass(now);
}

void AssetSync::finish_pass(double now) {
	scan_running_ = false;
	next_scan_ = now + kScanInterval;
	// Known files that are gone.
	std::vector<String> gone;
	for (const auto &kv : disk_) {
		if (!scan_seen_.count(kv.first) && !FileAccess::file_exists(kv.first)) gone.push_back(kv.first);
	}
	for (const auto &kv : log_) {
		if (!scan_seen_.count(kv.first) && !disk_.count(kv.first) && !excluded(kv.first) && !FileAccess::file_exists(kv.first)) {
			gone.push_back(kv.first);
		}
	}
	// Deletions the log would see. A whole folder vanishing at once is more
	// often a mistake (or a wrong checkout) than a decision: it waits for a
	// person to confirm, instead of emptying everyone's project.
	std::vector<String> deletions;
	for (const String &path : gone) {
		if (held_deletes_.count(path) || protected_from_delete(path) || is_imported_output(path)) continue;
		if (held_code_.count(path)) continue; // not written yet: its absence is no deletion
		auto p = pending_.find(path);
		bool known = log_.count(path) || (p != pending_.end() && !p->second.hash.empty());
		if (known && !incoming_.count(key_of(path))) deletions.push_back(path);
	}
	if (deletions.size() >= kMassDelete) {
		for (const String &path : deletions) held_deletes_.insert(path);
		if (hooks_.notice) {
			hooks_.notice(String::num_int64(int64_t(held_deletes_.size())) +
							" files disappeared from your project at once. Nothing was deleted for your team yet: open the YHDE panel to delete them for everyone or restore them.",
					1);
		}
	}
	for (const String &path : gone) {
		disk_.erase(path);
		dirty_ = true;
		evaluate(path, now, false);
	}
	scan_seen_.clear();
}

PackedStringArray AssetSync::held_deletions() const {
	PackedStringArray out;
	for (const String &p : held_deletes_) out.push_back(p);
	return out;
}

void AssetSync::confirm_deletions() {
	std::set<String> held;
	held.swap(held_deletes_);
	for (const String &path : held) evaluate(path, now_, true);
}

void AssetSync::restore_deletions() {
	for (const String &path : held_deletes_) {
		auto l = log_.find(path);
		if (l != log_.end() && !FileAccess::file_exists(path)) restoring_[path] = l->second.hash;
	}
	held_deletes_.clear();
}

void AssetSync::process_restores() {
	bool wrote = false;
	for (auto it = restoring_.begin(); it != restoring_.end();) {
		const String path = it->first;
		const std::string hash = it->second;
		auto l = log_.find(path);
		if (FileAccess::file_exists(path) || l == log_.end() || l->second.hash != hash) {
			it = restoring_.erase(it);
			continue;
		}
		// Sidecars go back first, so the editor keeps the file's uid.
		if (!is_sidecar(path) && (restoring_.count(path + String(".import")) || restoring_.count(path + String(".uid")))) {
			++it;
			continue;
		}
		String from;
		bool have = (downloaded_.count(hash) && FileAccess::file_exists(incoming_path(hash))) || local_copy(hash, from);
		if (!have) {
			if (!downloading_.count(hash)) {
				AssetJob job;
				job.kind = AssetJob::Kind::Download;
				job.hash = hash;
				job.size = l->second.size;
				job.file = std_of(global(incoming_path(hash)));
				downloading_.insert(hash);
				transfer_jobs_[worker_.submit(job)] = hash;
			}
			++it;
			continue;
		}
		if (write_blob(path, hash, l->second.size)) {
			DiskEntry w;
			hash_now(path, w);
			w.base = hash;
			disk_[path] = w;
			touched_.push_back(path);
			wrote = true;
		}
		it = restoring_.erase(it);
	}
	if (wrote) {
		finish_batch();
		if (restoring_.empty() && hooks_.notice) hooks_.notice("Restored the files from the shared project", 0);
	}
}

void AssetSync::observe(const String &path, int64_t size, int64_t mtime, double now) {
	auto d = disk_.find(path);
	double unix_now = Time::get_singleton()->get_unix_time_from_system();
	bool hot = double(mtime) >= unix_now - kHotSeconds;
	if (d != disk_.end() && d->second.size == size && d->second.mtime == mtime && !hot) {
		if (waiting_.count(path) || !log_.count(path)) evaluate(path, now, false);
		return;
	}
	if (hashing_.count(path)) return;
	hashing_.insert(path);
	hashing_mtime_[path] = mtime;
	AssetJob job;
	job.kind = AssetJob::Kind::Hash;
	job.file = std_of(global(path));
	hash_jobs_[worker_.submit(job)] = path;
}

bool AssetSync::hash_now(const String &path, DiskEntry &out) const {
	if (!FileAccess::file_exists(path)) return false;
	out.hash = std_of(FileAccess::get_sha256(path));
	if (out.hash.size() != 64) return false;
	out.size = int64_t(FileAccess::get_size(path));
	out.mtime = int64_t(FileAccess::get_modified_time(path));
	out.base.clear();
	return true;
}

void AssetSync::on_result(AssetResult &r, double now) {
	if (r.kind == AssetJob::Kind::Hash) {
		auto it = hash_jobs_.find(r.id);
		if (it == hash_jobs_.end()) return;
		String path = it->second;
		hash_jobs_.erase(it);
		hashing_.erase(path);
		int64_t mtime = hashing_mtime_[path];
		hashing_mtime_.erase(path);
		if (!r.ok) return; // gone meanwhile: the next pass notices
		// Written again while it was being hashed (often by this editor applying
		// a teammate's change): the result describes old bytes. The next pass
		// sees the new modification time and hashes again.
		if (int64_t(FileAccess::get_modified_time(path)) != mtime) return;
		DiskEntry &d = disk_[path];
		if (d.hash != r.hash) d.base.clear();
		d.hash = r.hash;
		d.size = r.size;
		d.mtime = mtime;
		dirty_ = true;
		evaluate(path, now, false);
		return;
	}

	auto job = transfer_jobs_.find(r.id);
	if (job != transfer_jobs_.end()) transfer_jobs_.erase(job);

	switch (r.kind) {
		case AssetJob::Kind::Missing: {
			if (!r.ok) {
				// Try the whole batch again a bit later.
				for (const auto &kv : upload_source_) {
					if (!uploading_.count(kv.first) && !remote_blobs_.count(kv.first)) to_check_.emplace_back(kv.first, kv.second);
				}
				upload_source_.clear();
				next_check_ = now + kRetryDelay;
				return;
			}
			std::set<std::string> missing(r.missing.begin(), r.missing.end());
			std::map<std::string, int64_t> partial(r.partial.begin(), r.partial.end());
			for (auto it = upload_source_.begin(); it != upload_source_.end();) {
				const std::string hash = it->first;
				String path = it->second;
				it = upload_source_.erase(it);
				if (!missing.count(hash)) {
					remote_blobs_.insert(hash);
					progress_ = true;
					continue;
				}
				if (uploading_.count(hash)) continue;
				DiskEntry d;
				if (!hash_now(path, d) || d.hash != hash) continue; // changed again: its newer op uploads
				AssetJob up;
				up.kind = AssetJob::Kind::Upload;
				up.file = std_of(global(path));
				up.hash = hash;
				up.size = d.size;
				auto p = partial.find(hash);
				up.offset = p != partial.end() ? p->second : 0;
				uploading_.insert(hash);
				transfer_jobs_[worker_.submit(up)] = hash;
			}
			break;
		}
		case AssetJob::Kind::Upload: {
			uploading_.erase(r.hash);
			if (r.ok) {
				remote_blobs_.insert(r.hash);
				progress_ = true;
				return;
			}
			if (r.transient) {
				String path;
				for (const auto &kv : pending_) {
					if (kv.second.hash == r.hash) path = kv.first;
				}
				if (!path.is_empty()) to_check_.emplace_back(r.hash, path);
				next_check_ = now + kRetryDelay;
				return;
			}
			// The file no longer has these bytes (or the server refuses them):
			// the operation waiting for them can never go out.
			for (auto it = pending_.begin(); it != pending_.end();) {
				if (it->second.hash == r.hash) {
					if (hooks_.drop) hooks_.drop(it->second.ref);
					auto d = disk_.find(it->first);
					if (d != disk_.end()) d->second.mtime = -1; // re-hash
					if (!r.mismatch && hooks_.notice) {
						hooks_.notice(String("Could not upload ") + it->first.get_file() + ": " + str_of(r.error), 2);
						refused_.insert({ it->first, r.hash });
					}
					it = pending_.erase(it);
				} else {
					++it;
				}
			}
			break;
		}
		case AssetJob::Kind::Download: {
			downloading_.erase(r.hash);
			if (r.ok) {
				downloaded_.insert(r.hash);
				progress_ = true;
			} else {
				retry_at_[r.hash] = now + kRetryDelay;
				if (!r.transient && hooks_.notice) hooks_.notice(String("Download failed: ") + str_of(r.error), 1);
			}
			break;
		}
		default:
			break;
	}
}

// Local changes -> operations

void AssetSync::evaluate(const String &path, double now, bool urgent, int depth) {
	if (!caught_up_ && !urgent) return;
	if (excluded(path)) return;
	// Held code is not written: what is on disk is not this person's edit.
	if (held_code_.count(path)) return;
	// Imported results travel only with their source's .import (evaluate_companion).
	if (is_imported_output(path) && !urgent) return;
	if (incoming_.count(key_of(path))) return; // the log is changing it right now
	if (hashing_.count(path)) return;
	auto d = disk_.find(path);
	auto l = log_.find(path);
	auto p = pending_.find(path);
	bool exists = d != disk_.end() && FileAccess::file_exists(path);

	if (!exists) {
		waiting_.erase(path);
		if (p != pending_.end() && p->second.hash.empty()) return;
		if (l == log_.end() && (p == pending_.end() || p->second.hash.empty())) return;
		// Never delete the add-on or project.godot for everyone, whatever happened here.
		if (protected_from_delete(path) || is_imported_output(path)) return;
		if (held_deletes_.count(path) || restoring_.count(path)) return;
		// The editor removes a .import/.uid whose file is missing: that is not
		// a deletion anyone made. A sidecar's deletion follows its file's.
		if (is_sidecar(path) && !FileAccess::file_exists(key_of(path))) return;
		if (l == log_.end() && p != pending_.end() && !remote_blobs_.count(p->second.hash)) {
			// Made and removed before it was ever shared: nothing to tell anyone.
			if (hooks_.drop) hooks_.drop(p->second.ref);
			pending_.erase(p);
			return;
		}
		Dictionary payload;
		payload["s"] = path;
		payload["o"] = l != log_.end() ? str_of(l->second.hash) : String();
		emit("DeleteAsset", path, payload, std::string());
		return;
	}
	held_deletes_.erase(path); // it came back

	const DiskEntry &de = d->second;
	if (p != pending_.end()) {
		if (p->second.hash == de.hash) return;
	} else if (l != log_.end() && (l->second.hash == de.hash || (!de.base.empty() && de.base == l->second.hash))) {
		waiting_.erase(path);
		return;
	}
	if (refused_.count({ path, de.hash })) return;
	// A live document's edits travel as operations; its bytes only at birth.
	if (l != log_.end() && hooks_.is_document && is_document_path(path) && hooks_.is_document(path)) return;

	// A file the editor imports is shared once the import is done: the .import
	// (with the uid every editor must use) goes into the log before the file.
	if (needs_import_wait(path, now)) return;
	waiting_.erase(path);

	if (!is_sidecar(path)) {
		// Sidecars go first: .uid before a script, .import before its source.
		for (const char *suffix : { ".uid", ".import" }) {
			String sc = path + String(suffix);
			evaluate_companion(sc, now, depth + 1);
			auto sd = disk_.find(sc);
			if (FileAccess::file_exists(sc) && !log_.count(sc) && !pending_.count(sc) &&
					!(sd != disk_.end() && refused_.count({ sc, sd->second.hash }))) {
				waiting_.emplace(path, now); // shared right after its sidecar
				return;
			}
		}
	}

	// Files this one needs (textures of a model, sub-scenes of a scene) enter
	// the log before it, so a teammate never loads it without them. They are
	// listed in "d": both the send gate and receivers keep that order.
	Array deps;
	if (path.ends_with(".import") && shares_import(key_of(path))) {
		// Tool-made import results (Blender, FBX2glTF) go before the .import
		// that points at them, so teammates never have to import it themselves.
		for (const String &out : import_outputs(key_of(path))) {
			evaluate_companion(out, now, depth + 1);
			if (log_.count(out) || pending_.count(out)) deps.push_back(out);
		}
	} else if (!is_sidecar(path) && !is_imported_output(path)) {
		for (const String &dep : project_dependencies(path)) {
			if (!log_.count(dep) && !pending_.count(dep)) {
				evaluate_companion(dep, now, depth + 1);
				if (!log_.count(dep) && !pending_.count(dep)) {
					if (waiting_.count(dep)) {
						waiting_.emplace(path, now); // still importing: wait with it
						return;
					}
					continue; // cannot be shared (refused, ignored): nothing to wait for
				}
			}
			deps.push_back(dep);
		}
	}
	// A dependency cycle may have shared this file already.
	p = pending_.find(path);
	if (p != pending_.end() && p->second.hash == de.hash) return;
	// An earlier change that never got uploaded is replaced, not chained: its
	// bytes are gone from disk, so it could never be sent (emit drops it).
	if (p != pending_.end() && !p->second.hash.empty() && !remote_blobs_.count(p->second.hash)) p = pending_.end();

	Dictionary payload;
	payload["s"] = path;
	payload["h"] = str_of(de.hash);
	payload["n"] = de.size;
	if (!deps.is_empty()) payload["d"] = deps;
	if (p != pending_.end() && !p->second.hash.empty()) {
		payload["o"] = str_of(p->second.hash);
	} else if (l != log_.end()) {
		payload["o"] = str_of(l->second.hash);
	}
	bool update = l != log_.end() || (p != pending_.end() && !p->second.hash.empty());
	emit(update ? "UpdateAsset" : "RegisterAsset", path, payload, de.hash);
}

void AssetSync::evaluate_companion(const String &path, double now, int depth) {
	if (depth > 8 || excluded(path) || !FileAccess::file_exists(path)) return;
	// It must be decided now (it goes into the log before the file that needs
	// it), so a background hash of it is replaced by one done here.
	if (hashing_.count(path)) {
		cancel_hash(path);
		disk_.erase(path);
	}
	auto known = disk_.find(path);
	int64_t mtime = int64_t(FileAccess::get_modified_time(path));
	int64_t size = int64_t(FileAccess::get_size(path));
	if (known == disk_.end() || known->second.mtime != mtime || known->second.size != size) {
		DiskEntry sd;
		if (!hash_now(path, sd)) return;
		if (known != disk_.end() && known->second.hash == sd.hash) sd.base = known->second.base;
		disk_[path] = sd;
		dirty_ = true;
	}
	evaluate(path, now, true, depth);
}

bool AssetSync::needs_import_wait(const String &path, double now) {
	if (!is_importable(path)) return false;
	EditorFileSystem *fs = efs();
	bool busy = fs && (fs->is_scanning() || fs->is_importing());
	bool has_import = FileAccess::file_exists(path + String(".import"));
	if (has_import && !busy) return false;
	auto w = waiting_.find(path);
	if (w == waiting_.end()) {
		waiting_[path] = now;
		// A file written by another program (a Blender export, a git pull)
		// while the editor was in the background: ask the editor to look.
		if (!has_import && fs && !busy && !efs_scan_requested_) {
			efs_scan_requested_ = true;
			fs->scan();
		}
		return true;
	}
	if (!has_import && !busy && now - w->second >= kImportWaitMax) return false; // never imported: share as is
	return true;
}

void AssetSync::emit(const std::string &type, const String &path, const Dictionary &payload, const std::string &blob, bool before_doc) {
	if (!hooks_.emit) return;
	LocalOp op;
	op.type = type;
	op.target = Uuid::from_name(yhde_namespace(), String("file:") + path);
	op.key = "@file";
	op.payload = payload;
	auto old = pending_.find(path);
	if (old != pending_.end() && !old->second.hash.empty() && !remote_blobs_.count(old->second.hash) && hooks_.drop) {
		// Unsent (its bytes were never uploaded): superseded by this one.
		hooks_.drop(old->second.ref);
	}
	// Known as pending before it is queued: queuing may send at once, and the
	// send gate asks about this very file (it must not register it again).
	Pending pe;
	pe.hash = blob;
	pending_[path] = pe;
	Uuid ref = hooks_.emit(std::move(op), blob, before_doc);
	pending_[path].ref = ref;
	if (!blob.empty()) need_blob(blob, path, 0);
}

void AssetSync::need_blob(const std::string &hash, const String &path, int64_t size) {
	if (remote_blobs_.count(hash) || uploading_.count(hash) || upload_source_.count(hash)) return;
	for (const auto &c : to_check_) {
		if (c.first == hash) return;
	}
	to_check_.emplace_back(hash, path);
}

void AssetSync::restore_pending(const std::string &type, const Dictionary &payload, const Uuid &client_op_ref) {
	String path = payload.get("s", String());
	if (path.is_empty()) return;
	Pending pe;
	pe.ref = client_op_ref;
	if (type != "DeleteAsset") pe.hash = std_of(payload.get("h", String()));
	pending_[path] = pe;
	if (!pe.hash.empty() && !remote_blobs_.count(pe.hash)) need_blob(pe.hash, path, 0);
}

void AssetSync::on_rejected(const Dictionary &payload, const Uuid &client_op_ref, const std::string &code) {
	String path = payload.get("s", String());
	auto p = pending_.find(path);
	if (p == pending_.end() || p->second.ref != client_op_ref) return;
	std::string hash = p->second.hash;
	pending_.erase(p);
	if (code == "AlreadyCurrent") {
		// The log already says exactly this: our bytes stand for it.
		auto d = disk_.find(path);
		if (d != disk_.end() && d->second.hash == hash) d->second.base = hash;
		if (!hash.empty() && !log_.count(path)) {
			// The operation that made it so is on its way; until then, know it.
			LogEntry l;
			l.hash = hash;
			l.size = d != disk_.end() ? d->second.size : 0;
			log_[path] = l;
			remote_blobs_.insert(hash);
		}
		dirty_ = true;
		return;
	}
	if (code == "AssetMissing") {
		remote_blobs_.erase(hash); // re-check and re-upload, then send again
		auto d = disk_.find(path);
		if (d != disk_.end()) d->second.mtime = -1;
		return;
	}
	refused_.insert({ path, hash });
	if (hooks_.notice) hooks_.notice(String("The server refused ") + path.get_file(), 1);
}

// Documents

void AssetSync::ensure_registered(const String &path) {
	if (!active_ || excluded(path)) return;
	if (log_.count(path) || pending_.count(path)) return;
	if (!caught_up_) {
		deferred_.insert(path);
		return;
	}
	if (!FileAccess::file_exists(path)) return;
	DiskEntry d;
	if (!hash_now(path, d)) return;
	disk_[path] = d;
	dirty_ = true;
	evaluate(path, now_, true);
}

bool AssetSync::text_base(const String &path, int64_t *seq) const {
	auto l = log_.find(path);
	if (l == log_.end() || incoming_.count(key_of(path)) || pending_.count(path)) return false;
	auto d = disk_.find(path);
	if (d == disk_.end() || !(d->second.hash == l->second.hash || d->second.base == l->second.hash)) return false;
	if (seq) *seq = l->second.seq;
	return true;
}

bool AssetSync::reference_waits(const String &reference, Uuid *pending_ref) {
	if (!active_) return false;
	String path = reference.get_slice("::", 0);
	if (excluded(path) || is_imported_output(path)) return false;
	auto p = pending_.find(path);
	if (p != pending_.end()) {
		if (p->second.hash.empty()) return false; // being deleted: the reference is broken anyway
		if (pending_ref) *pending_ref = p->second.ref;
		return true;
	}
	if (log_.count(path)) return false;
	if (!FileAccess::file_exists(path)) return false; // nothing to share
	if (!caught_up_) return true;                     // the first scan registers it
	if (waiting_.count(path) || hashing_.count(path)) return true;
	// Just made (a texture dropped in and assigned right away): share it now,
	// ahead of the edit that uses it.
	DiskEntry d;
	if (!hash_now(path, d)) return false;
	auto old = disk_.find(path);
	if (old != disk_.end() && old->second.hash == d.hash) d.base = old->second.base;
	disk_[path] = d;
	dirty_ = true;
	evaluate(path, now_, true);
	p = pending_.find(path);
	if (p != pending_.end()) {
		if (pending_ref) *pending_ref = p->second.ref;
		return true;
	}
	return waiting_.count(path) > 0;
}

void AssetSync::cancel_hash(const String &path) {
	if (!hashing_.erase(path)) return;
	hashing_mtime_.erase(path);
	for (auto it = hash_jobs_.begin(); it != hash_jobs_.end();) {
		it = it->second == path ? hash_jobs_.erase(it) : std::next(it);
	}
}

void AssetSync::absorb(const String &path) {
	if (!active_ || excluded(path)) return;
	// A hash still running for this path read the bytes from before this
	// write: its result must not undo what absorb records.
	cancel_hash(path);
	DiskEntry d;
	if (!hash_now(path, d)) return;
	auto l = log_.find(path);
	auto old = disk_.find(path);
	if (l != log_.end()) {
		d.base = l->second.hash;
	} else if (old != disk_.end()) {
		d.base = old->second.base;
	}
	disk_[path] = d;
	dirty_ = true;
}

void AssetSync::editor_saved(const String &path) {
	if (!active_ || excluded(path)) return;
	if (hooks_.is_document && hooks_.is_document(path) && log_.count(path)) {
		absorb(path);
		return;
	}
	DiskEntry d;
	if (!hash_now(path, d)) return;
	auto old = disk_.find(path);
	if (old != disk_.end() && old->second.hash == d.hash) d.base = old->second.base;
	disk_[path] = d;
	dirty_ = true;
	evaluate(path, now_, true);
}

// Incoming

bool AssetSync::stale(const RemoteOp &op) const {
	// Already applied (a re-sync delivers from an older point): applying an
	// old version again would put old bytes back over newer work.
	auto a = applied_.find(String(op.payload.get("s", String())));
	return a != applied_.end() && a->second >= op.seq;
}

void AssetSync::note_committed(const RemoteOp &op) {
	if (stale(op)) return;
	String path = op.payload.get("s", String());
	std::string hash = std_of(op.payload.get("h", String()));
	if (op.type == "DeleteAsset") {
		log_.erase(path);
	} else {
		LogEntry l;
		l.hash = hash;
		l.size = int64_t(double(op.payload.get("n", 0.0)));
		l.seq = op.seq;
		log_[path] = l;
		remote_blobs_.insert(hash);
		if (op.type == "MoveAsset") {
			String from = op.payload.get("f", String());
			log_.erase(from);
			incoming_[key_of(from)]++;
		}
	}
	incoming_[key_of(path)]++;
	dirty_ = true;
}

bool AssetSync::local_copy(const std::string &hash, String &from) {
	for (const auto &kv : disk_) {
		if (kv.second.hash != hash) continue;
		if (!FileAccess::file_exists(kv.first)) continue;
		if (int64_t(FileAccess::get_modified_time(kv.first)) != kv.second.mtime) continue;
		from = kv.first;
		return true;
	}
	return false;
}

String AssetSync::incoming_path(const std::string &hash) const {
	return String(kIncomingDir).path_join(str_of(hash));
}

bool AssetSync::ready(const RemoteOp &op) {
	if (op.own || op.type == "DeleteAsset" || stale(op)) return true;
	EditorFileSystem *fs = efs();
	auto why = [&](const char *reason) {
		if (debug_) UtilityFunctions::print("[yhde] hold #", op.seq, " ", String(op.payload.get("s", String())), ": ", reason);
		return false;
	};
	if (fs && (fs->is_scanning() || fs->is_importing())) {
		retry_when_idle_ = true;
		return why(fs->is_scanning() ? "editor scanning" : "editor importing");
	}
	String path = op.payload.get("s", String());
	std::string hash = std_of(op.payload.get("h", String()));
	if (pending_.count(path)) return true; // ours wins: nothing to write
	if (excluded(path)) return true;
	// The editor only takes in files of folders it has scanned (update_file
	// and reimport ignore the rest): make a new folder and let it look first.
	if (fs && !is_imported_output(path)) {
		String dir = path.get_base_dir();
		if (dir != "res://" && fs->get_filesystem_path(dir) == nullptr) {
			DirAccess::make_dir_recursive_absolute(dir);
			if (!efs_scan_requested_) {
				efs_scan_requested_ = true;
				fs->scan();
			}
			// A small project's scan can start and finish between two frames:
			// retry on the next idle frame instead of waiting to see it end.
			retry_when_idle_ = true;
			return why("new folder, waiting for the editor's scan");
		}
	}
	// A .import or .uid on disk without its file is an orphan to the editor,
	// which deletes it on its next scan. Write it only when its file can be
	// written right after it (same batch), or once no file is coming.
	if (is_sidecar(path)) {
		String source = key_of(path);
		if (!FileAccess::file_exists(source)) {
			auto l = log_.find(source);
			if (l != log_.end()) {
				if (!have_bytes(l->second.hash)) {
					fetch(l->second.hash, l->second.size);
					return why("sidecar waits for its file's bytes");
				}
			} else {
				auto first = sidecar_wait_.emplace(path, now_).first;
				if (now_ - first->second < kSidecarWait) return why("sidecar waits for its file's operation");
			}
		}
		sidecar_wait_.erase(path);
	}
	if (have_bytes(hash)) return true;
	if (op.type == "MoveAsset") {
		String old = op.payload.get("f", String());
		if (FileAccess::file_exists(old) && std_of(FileAccess::get_sha256(old)) == hash) return true;
	}
	fetch(hash, int64_t(double(op.payload.get("n", 0.0))));
	return why(downloading_.count(hash) ? "downloading" : "download not started");
}

bool AssetSync::have_bytes(const std::string &hash) {
	if (downloaded_.count(hash) && FileAccess::file_exists(incoming_path(hash))) return true;
	String from;
	return local_copy(hash, from);
}

void AssetSync::fetch(const std::string &hash, int64_t size) {
	if (downloading_.count(hash)) return;
	auto retry = retry_at_.find(hash);
	if (retry != retry_at_.end() && now_ < retry->second) return;
	retry_at_.erase(hash);
	AssetJob job;
	job.kind = AssetJob::Kind::Download;
	job.hash = hash;
	job.size = size;
	job.file = std_of(global(incoming_path(hash)));
	downloading_.insert(hash);
	transfer_jobs_[worker_.submit(job)] = hash;
}

void AssetSync::backup(const String &path) {
	if (!FileAccess::file_exists(path)) return;
	String stamp = Time::get_singleton()->get_datetime_string_from_system(true).replace(":", "-");
	String dest = String(kReplacedDir).path_join(stamp).path_join(path.substr(6));
	DirAccess::make_dir_recursive_absolute(dest.get_base_dir());
	DirAccess::copy_absolute(path, dest);
	if (hooks_.notice) {
		hooks_.notice(String("Replaced your local ") + path.get_file() + " with the shared version (your copy is in " + dest.get_base_dir() + ")", 1);
	}
}

void AssetSync::remove_file(const String &path, bool keep_backup) {
	if (!FileAccess::file_exists(path)) return;
	if (keep_backup) backup(path);
	if (OS::get_singleton()->move_to_trash(global(path)) != OK) DirAccess::remove_absolute(path);
	// Drop folders the removal emptied.
	String dir = path.get_base_dir();
	while (dir != "res://" && dir.begins_with("res://") && DirAccess::get_files_at(dir).is_empty() &&
			DirAccess::get_directories_at(dir).is_empty()) {
		if (DirAccess::remove_absolute(dir) != OK) break;
		dir = dir.get_base_dir();
	}
}

bool AssetSync::write_blob(const String &path, const std::string &hash, int64_t size) {
	String source;
	String incoming = incoming_path(hash);
	if (downloaded_.count(hash) && FileAccess::file_exists(incoming)) {
		source = incoming;
		used_incoming_.insert(hash);
	} else if (!local_copy(hash, source)) {
		return false;
	}
	if (source == path) return true;
	DirAccess::make_dir_recursive_absolute(path.get_base_dir());
	String tmp = path.get_base_dir().path_join(String(".") + path.get_file() + ".yhde-tmp");
	if (DirAccess::copy_absolute(source, tmp) != OK) return false;
	if (FileAccess::file_exists(path)) DirAccess::remove_absolute(path);
	if (DirAccess::rename_absolute(tmp, path) != OK) {
		DirAccess::remove_absolute(tmp);
		return false;
	}
	return true;
}

void AssetSync::apply(const RemoteOp &op) {
	String path = op.payload.get("s", String());
	String from = op.payload.get("f", String());
	std::string hash = std_of(op.payload.get("h", String()));
	int64_t size = int64_t(double(op.payload.get("n", 0.0)));

	if (stale(op)) return;
	auto done = [&](const String &p) {
		auto it = incoming_.find(key_of(p));
		if (it != incoming_.end() && --it->second <= 0) incoming_.erase(it);
	};
	done(path);
	if (op.type == "MoveAsset") done(from);
	applied_[path] = op.seq;
	if (op.type == "MoveAsset") applied_[from] = op.seq;
	dirty_ = true;

	if (op.own) {
		auto p = pending_.find(path);
		if (p != pending_.end() && p->second.ref == op.client_op_ref) pending_.erase(p);
		if (op.type == "MoveAsset") pending_.erase(from);
		auto d = disk_.find(path);
		if (d != disk_.end() && !hash.empty() && d->second.hash == hash) d->second.base = hash;
		if (op.type == "DeleteAsset" && !FileAccess::file_exists(path)) disk_.erase(path);
		return;
	}
	if (excluded(path)) return;
	if (pending_.count(path)) return; // our own change to it commits after this one
	// Belt and braces (the server refuses these too): never let the log remove
	// the add-on or project.godot from this machine.
	if (op.type == "DeleteAsset" && protected_from_delete(path)) return;
	if (op.type == "MoveAsset" && protected_from_delete(from)) return;

	if (op.type == "DeleteAsset") {
		held_code_.erase(path);
	} else if (needs_approval(path, hash)) {
		bool fresh = !held_code_.count(path);
		HeldCode &h = held_code_[path];
		h.hash = hash;
		h.size = size;
		h.seq = op.seq;
		h.by = op.actor;
		h.accepted = false;
		if (op.type == "MoveAsset") {
			// The old name goes as it would have: only the new one is held.
			held_code_.erase(from);
			auto fd = disk_.find(from);
			if (FileAccess::file_exists(from) && fd != disk_.end() && fd->second.hash == hash) remove_file(from, false);
			disk_.erase(from);
			touched_.push_back(from);
		}
		dirty_ = true;
		if (fresh && hooks_.notice) {
			String who = op.actor.is_empty() ? String("A teammate") : op.actor;
			hooks_.notice(who + String(" sent ") + path.get_file() +
							String(", which can run code in your editor. It is held until you accept it in the YHDE panel."),
					1);
		}
		return;
	} else {
		held_code_.erase(path);
	}

	String subject;
	if (op.type == "DeleteAsset") {
		auto d = disk_.find(path);
		bool local_change = FileAccess::file_exists(path) &&
				(d == disk_.end() || d->second.base.empty() || int64_t(FileAccess::get_modified_time(path)) != d->second.mtime);
		remove_file(path, local_change);
		disk_.erase(path);
		touched_.push_back(path);
		subject = String("Deleted ") + path.get_file();
	} else {
		// What is on disk now, and is it something only this editor has?
		DiskEntry current;
		bool exists = FileAccess::file_exists(path);
		auto d = disk_.find(path);
		if (exists) {
			if (d != disk_.end() && d->second.mtime == int64_t(FileAccess::get_modified_time(path)) &&
					d->second.size == int64_t(FileAccess::get_size(path))) {
				current = d->second;
			} else {
				hash_now(path, current);
			}
		}
		if (exists && current.hash == hash) {
			current.base = hash;
			disk_[path] = current;
		} else {
			bool local_change = exists && (current.base.empty() || d == disk_.end());
			if (path == "res://project.godot" && exists) {
				String incoming = incoming_path(hash);
				String source = incoming;
				if (!(downloaded_.count(hash) && FileAccess::file_exists(incoming)) && !local_copy(hash, source)) return;
				used_incoming_.insert(hash);
				apply_project_settings(source);
				absorb(path);
				auto pe = disk_.find(path);
				if (pe != disk_.end()) pe->second.base = hash;
			} else {
				if (op.type == "MoveAsset" && FileAccess::file_exists(from) && std_of(FileAccess::get_sha256(from)) == hash && !exists) {
					DirAccess::make_dir_recursive_absolute(path.get_base_dir());
					DirAccess::rename_absolute(from, path);
				} else {
					if (local_change) backup(path);
					if (!write_blob(path, hash, size)) {
						if (hooks_.notice) hooks_.notice(String("Could not write ") + path, 2);
						return;
					}
				}
				DiskEntry w;
				hash_now(path, w);
				w.base = hash;
				disk_[path] = w;
			}
			touched_.push_back(path);
			if (is_document_path(path)) replaced_.emplace_back(path, op.seq);
		}
		if (op.type == "MoveAsset") {
			auto fd = disk_.find(from);
			if (FileAccess::file_exists(from) && fd != disk_.end() && fd->second.hash == hash) remove_file(from, false);
			disk_.erase(from);
			touched_.push_back(from);
			subject = String("Moved ") + from.get_file() + String::utf8(" to ") + path.trim_prefix("res://");
		} else {
			subject = String(op.type == "RegisterAsset" ? "Added " : "Updated ") + path.get_file();
		}
	}
	applied_count_++;
	if (hooks_.activity && !is_sidecar(path)) hooks_.activity(op, subject);
}

void AssetSync::apply_project_settings(const String &incoming) {
	ProjectSettings *ps = ProjectSettings::get_singleton();
	auto read = [](const String &file, Dictionary &out) {
		Ref<ConfigFile> cf;
		cf.instantiate();
		if (cf->load(file) != OK) return false;
		PackedStringArray sections = cf->get_sections();
		for (int64_t i = 0; i < sections.size(); i++) {
			if (sections[i].is_empty()) continue; // config_version
			PackedStringArray keys = cf->get_section_keys(sections[i]);
			for (int64_t k = 0; k < keys.size(); k++) out[sections[i] + "/" + keys[k]] = cf->get_value(sections[i], keys[k]);
		}
		return true;
	};
	Dictionary before;
	Dictionary after;
	read("res://project.godot", before);
	if (!read(incoming, after)) return;
	Array keys = after.keys();
	for (int64_t i = 0; i < keys.size(); i++) {
		String name = keys[i];
		if (name.begins_with("editor_plugins/")) continue; // which add-ons are enabled is up to each editor
		Variant v = after[keys[i]];
		if (!before.has(name) || before[name] != v) ps->set_setting(name, v);
	}
	keys = before.keys();
	for (int64_t i = 0; i < keys.size(); i++) {
		String name = keys[i];
		if (after.has(name) || name.begins_with("editor_plugins/")) continue;
		if (ps->property_can_revert(name)) {
			ps->set_setting(name, ps->property_get_revert(name));
		} else {
			ps->set_setting(name, Variant());
		}
	}
	ps->save();
}

void AssetSync::finish_batch() {
	if (touched_.empty() && replaced_.empty()) {
		for (const std::string &h : used_incoming_) {
			DirAccess::remove_absolute(incoming_path(h));
			downloaded_.erase(h);
		}
		used_incoming_.clear();
		return;
	}
	EditorFileSystem *fs = efs();
	std::set<String> sources;
	for (const String &p : touched_) {
		if (p == "res://project.godot") continue;
		if (is_imported_output(p)) {
			// A shared import result arrived: finish its source with it.
			for (const auto &kv : log_) {
				if (!kv.first.ends_with(".import") || !shares_import(key_of(kv.first))) continue;
				std::vector<String> outs = import_outputs(key_of(kv.first));
				if (std::find(outs.begin(), outs.end(), p) != outs.end()) sources.insert(key_of(kv.first));
			}
			continue;
		}
		sources.insert(key_of(p));
	}
	PackedStringArray reimport;
	std::vector<String> prebuilt;
	if (fs) {
		for (const String &s : sources) {
			if (s.begins_with("res://.")) continue;
			fs->update_file(s);
			if (!FileAccess::file_exists(s) || !FileAccess::file_exists(s + String(".import"))) continue;
			// The import result came with it (a .blend imported by a teammate
			// with Blender): use it as is, exactly like theirs.
			if (shares_import(s)) {
				bool complete = true;
				bool coming = false;
				for (const String &out : import_outputs(s)) {
					if (!FileAccess::file_exists(out)) {
						complete = false;
						coming = coming || log_.count(out) > 0;
					}
				}
				if (complete) {
					prebuilt.push_back(s);
					continue;
				}
				// Its import result is still on the way: importing now would
				// need Blender; the result's arrival finishes it (below).
				if (coming) continue;
			}
			reimport.push_back(s);
		}
		// Godot imports in dependency order (textures before the models and
		// scenes that use them) within one call.
		if (!reimport.is_empty()) fs->reimport_files(reimport);
	}
	for (const String &s : prebuilt) {
		ResourceLoader *loader = ResourceLoader::get_singleton();
		if (loader->has_cached(s)) loader->load(s, "", ResourceLoader::CACHE_MODE_REPLACE);
	}
	// Importing rewrites .import files (and may create .uid files): those are
	// consequences of what arrived, not edits made here.
	for (const String &s : sources) {
		for (const char *suffix : { ".import", ".uid" }) {
			String sc = s + String(suffix);
			if (FileAccess::file_exists(sc)) absorb(sc);
		}
	}
	// Reload what the editor already has in memory.
	ResourceLoader *loader = ResourceLoader::get_singleton();
	bool scripts = false;
	for (const String &s : sources) {
		if (!FileAccess::file_exists(s) || is_document_path(s) || FileAccess::file_exists(s + String(".import"))) continue;
		if (!loader->has_cached(s)) continue;
		Ref<Resource> cached = loader->get_cached_ref(s);
		Ref<Resource> fresh = loader->load(s, "", ResourceLoader::CACHE_MODE_IGNORE);
		if (cached.is_null() || fresh.is_null()) continue;
		Ref<Script> script = cached;
		Ref<Script> fresh_script = fresh;
		if (script.is_valid() && fresh_script.is_valid()) {
			script->set_source_code(fresh_script->get_source_code());
			script->reload(true);
			scripts = true;
		} else if (cached->get_class() == fresh->get_class()) {
			cached->copy_from_resource(fresh);
			cached->emit_changed();
		}
	}
	if (scripts || !sources.empty()) {
		EditorInterface *ei = EditorInterface::get_singleton();
		if (ScriptEditor *se = ei ? ei->get_script_editor() : nullptr) se->reload_open_files();
	}
	std::vector<std::pair<String, int64_t>> replaced;
	replaced.swap(replaced_);
	for (const auto &r : replaced) {
		if (hooks_.replaced) hooks_.replaced(r.first, r.second);
	}
	touched_.clear();
	for (const std::string &h : used_incoming_) {
		DirAccess::remove_absolute(incoming_path(h));
		downloaded_.erase(h);
	}
	used_incoming_.clear();
	if (applied_count_ > 0 && hooks_.notice && applied_count_ >= 5) {
		hooks_.notice(String("Received ") + String::num_int64(applied_count_) + " file changes", 0);
	}
	applied_count_ = 0;
	dirty_ = true;
}

// Code from others

bool AssetSync::is_native_or_build_file(const String &path) {
	String lower = path.to_lower();
	// Bundles (macOS frameworks, apps) are folders: everything inside counts.
	for (const char *bundle : { ".framework/", ".xcframework/", ".app/", ".bundle/" }) {
		if (lower.contains(bundle)) return true;
	}
	String file = lower.get_file();
	if (file.contains(".so.")) return true; // libfoo.so.1
	static const char *exts[] = { "gdextension", "dll", "so", "dylib", "exe", "com", "msi", "bat", "cmd", "ps1", "sh",
		"command", "csproj", "fsproj", "vbproj", "props", "targets", "sln", "jar" };
	String ext = file.get_extension();
	for (const char *e : exts) {
		if (ext == e) return true;
	}
	return false;
}

bool AssetSync::may_run_in_editor(const String &path, const String &bytes_file) {
	String ext = path.get_extension().to_lower();
	bool csharp = ext == "cs";
	if (!(ext == "gd" || csharp || ext == "tscn" || ext == "tres" || ext == "scn" || ext == "res")) return false;
	Ref<FileAccess> f = FileAccess::open(bytes_file, FileAccess::READ);
	if (f.is_null()) return true; // cannot tell: hold it
	// @tool scripts (also built into scenes and resources) and C# [Tool]
	// classes run inside the editor.
	std::vector<std::string> markers = { "@tool" };
	if (csharp) markers = { "[Tool", ".Tool" };
	const int64_t chunk = 1 << 20;
	const size_t overlap = 8;
	std::string window;
	while (!f->eof_reached()) {
		PackedByteArray buf = f->get_buffer(chunk);
		if (buf.is_empty()) break;
		window.append(reinterpret_cast<const char *>(buf.ptr()), size_t(buf.size()));
		for (const std::string &m : markers) {
			if (window.find(m) != std::string::npos) return true;
		}
		if (window.size() > overlap) window.erase(0, window.size() - overlap);
	}
	return false;
}

bool AssetSync::needs_approval(const String &path, const std::string &hash) {
	if (trust_code_ || path == "res://project.godot" || is_sidecar(path) || is_imported_output(path)) return false;
	bool native = is_native_or_build_file(path);
	String ext = path.get_extension().to_lower();
	bool script_like = ext == "gd" || ext == "cs" || ext == "tscn" || ext == "tres" || ext == "scn" || ext == "res";
	if (!native && !script_like) return false;
	// The same bytes are already code in this project (a move, a copy, an
	// update that was undone): nothing new runs. A copy that is harmless
	// where it is (a .txt) does not count, or renaming it would slip through.
	for (const auto &kv : disk_) {
		if (kv.second.hash != hash || held_code_.count(kv.first) || !FileAccess::file_exists(kv.first)) continue;
		if (int64_t(FileAccess::get_modified_time(kv.first)) != kv.second.mtime) continue;
		if (is_native_or_build_file(kv.first) || may_run_in_editor(kv.first, kv.first)) return false;
	}
	if (native) return true;
	String incoming = incoming_path(hash);
	String bytes;
	if (downloaded_.count(hash) && FileAccess::file_exists(incoming)) {
		bytes = incoming;
	} else if (!local_copy(hash, bytes)) {
		return true; // cannot look inside: hold it
	}
	return may_run_in_editor(path, bytes);
}

void AssetSync::release_accepted_code() {
	bool wrote = false;
	for (auto it = held_code_.begin(); it != held_code_.end();) {
		const String path = it->first;
		HeldCode &h = it->second;
		if (!h.accepted && !trust_code_) {
			++it;
			continue;
		}
		auto l = log_.find(path);
		if (l == log_.end() || l->second.hash != h.hash) {
			it = held_code_.erase(it); // the log moved on: its own operation decides
			dirty_ = true;
			continue;
		}
		// Its .uid/.import went ahead of it and the editor may have removed
		// them as orphans: they come back with it, or the uid would change.
		std::vector<String> sidecars;
		for (const char *suffix : { ".uid", ".import" }) {
			String sc = path + String(suffix);
			auto sl = log_.find(sc);
			if (sl == log_.end()) continue;
			if (FileAccess::file_exists(sc) && std_of(FileAccess::get_sha256(sc)) == sl->second.hash) continue;
			sidecars.push_back(sc);
		}
		bool missing = false;
		if (!have_bytes(h.hash)) {
			fetch(h.hash, h.size);
			missing = true;
		}
		for (const String &sc : sidecars) {
			const LogEntry &sl = log_[sc];
			if (!have_bytes(sl.hash)) {
				fetch(sl.hash, sl.size);
				missing = true;
			}
		}
		if (missing) {
			++it;
			continue;
		}
		for (const String &sc : sidecars) {
			const LogEntry &sl = log_[sc];
			if (!write_blob(sc, sl.hash, sl.size)) continue;
			DiskEntry sw;
			hash_now(sc, sw);
			sw.base = sl.hash;
			disk_[sc] = sw;
			touched_.push_back(sc);
		}
		auto d = disk_.find(path);
		bool exists = FileAccess::file_exists(path);
		bool local_change = exists && (d == disk_.end() || d->second.base.empty() ||
											  int64_t(FileAccess::get_modified_time(path)) != d->second.mtime);
		if (local_change) backup(path);
		if (!write_blob(path, h.hash, h.size)) {
			++it;
			continue;
		}
		DiskEntry w;
		hash_now(path, w);
		w.base = h.hash;
		disk_[path] = w;
		touched_.push_back(path);
		if (is_document_path(path)) replaced_.emplace_back(path, h.seq);
		applied_count_++;
		it = held_code_.erase(it);
		dirty_ = true;
		wrote = true;
	}
	if (wrote) finish_batch();
}

Array AssetSync::held_code() const {
	Array out;
	for (const auto &kv : held_code_) {
		if (kv.second.accepted) continue;
		Dictionary d;
		d["path"] = kv.first;
		d["by"] = kv.second.by;
		out.push_back(d);
	}
	return out;
}

void AssetSync::accept_code() {
	for (auto &kv : held_code_) kv.second.accepted = true;
	dirty_ = true;
	progress_ = true;
}

void AssetSync::set_trust_code(bool on) {
	trust_code_ = on;
	if (on) accept_code();
}

// UI

Dictionary AssetSync::status() const {
	Dictionary d;
	AssetWorker::Progress p = worker_.progress();
	d["active"] = active_;
	d["uploads"] = p.uploads + int64_t(to_check_.size());
	d["downloads"] = p.downloads;
	d["bytes_done"] = p.bytes_done;
	d["bytes_total"] = p.bytes_total;
	d["waiting"] = int64_t(pending_.size());
	d["importing"] = int64_t(waiting_.size());
	d["files"] = int64_t(log_.size());
	d["held_deletes"] = int64_t(held_deletes_.size());
	d["held_code"] = int64_t(held_code_.size());
	d["restoring"] = int64_t(restoring_.size());
	return d;
}

} // namespace yhde
