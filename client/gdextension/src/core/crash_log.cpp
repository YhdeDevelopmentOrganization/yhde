#include "core/crash_log.h"

#include <godot_cpp/classes/dir_access.hpp>
#include <godot_cpp/classes/file_access.hpp>
#include <godot_cpp/classes/os.hpp>
#include <godot_cpp/classes/time.hpp>

#include <deque>
#include <mutex>

using namespace godot;

namespace yhde {

namespace {

const char *kDir = "res://.godot/yhde";
const char *kMarker = "res://.godot/yhde/running";
const char *kLog = "res://.godot/yhde/last.log";
constexpr size_t kActions = 150;
constexpr size_t kEngine = 60;
constexpr uint64_t kFlushMs = 200;

std::mutex lock;
bool changed = false;
bool running = false;
uint64_t last_flush = 0;

// Godot values cannot be globals: they would be built when the library is
// loaded, before Godot's functions are there.
struct State {
	std::deque<String> actions;
	std::deque<String> engine_lines;
	String last_report;
	Ref<YhdeLogger> logger;
};

State &state() {
	static State *s = new State(); // never freed: Godot may log during exit
	return *s;
}

String stamp() {
	return Time::get_singleton()->get_time_string_from_system();
}

void push(std::deque<String> &ring, size_t cap, const String &line) {
	ring.push_back(stamp() + " " + line);
	while (ring.size() > cap) ring.pop_front();
	changed = true;
}

String text_locked() {
	String out = "YHDE, last actions:\n";
	for (const String &l : state().actions) out += l + String("\n");
	out += "\nGodot, last errors and warnings:\n";
	for (const String &l : state().engine_lines) out += l + String("\n");
	return out;
}

} // namespace

void CrashLog::start() {
	DirAccess::make_dir_recursive_absolute(kDir);
	{
		std::lock_guard<std::mutex> g(lock);
		state().last_report = String();
		if (FileAccess::file_exists(kMarker)) {
			String before = FileAccess::file_exists(kLog) ? FileAccess::get_file_as_string(kLog) : String();
			state().last_report = String("Godot closed unexpectedly while YHDE was running.\n\n") + before;
		}
		state().actions.clear();
		state().engine_lines.clear();
		changed = true;
		running = true;
	}
	Ref<FileAccess> f = FileAccess::open(kMarker, FileAccess::WRITE);
	if (f.is_valid()) f->store_string(Time::get_singleton()->get_datetime_string_from_system());
	if (state().logger.is_null()) {
		state().logger.instantiate();
		OS::get_singleton()->add_logger(state().logger);
	}
	note("Editor started");
	flush(true);
}

void CrashLog::stop() {
	note("Editor closed");
	flush(true);
	if (state().logger.is_valid()) {
		OS::get_singleton()->remove_logger(state().logger);
		state().logger.unref();
	}
	{
		std::lock_guard<std::mutex> g(lock);
		running = false;
	}
	DirAccess::remove_absolute(kMarker);
}

void CrashLog::note(const String &line, bool now) {
	{
		std::lock_guard<std::mutex> g(lock);
		if (!running) return;
		push(state().actions, kActions, line);
	}
	flush(now);
}

void CrashLog::engine(const String &line) {
	// Never wait here: Godot may be reporting from inside a crash.
	std::unique_lock<std::mutex> g(lock, std::try_to_lock);
	if (!g.owns_lock() || !running) return;
	push(state().engine_lines, kEngine, line);
}

void CrashLog::flush(bool force) {
	String text;
	{
		std::lock_guard<std::mutex> g(lock);
		if (!running || !changed) return;
		uint64_t t = Time::get_singleton()->get_ticks_msec();
		if (!force && t - last_flush < kFlushMs) return;
		last_flush = t;
		changed = false;
		text = text_locked();
	}
	Ref<FileAccess> f = FileAccess::open(kLog, FileAccess::WRITE);
	if (f.is_valid()) f->store_string(text);
}

String CrashLog::report() {
	std::lock_guard<std::mutex> g(lock);
	return state().last_report;
}

void CrashLog::clear_report() {
	std::lock_guard<std::mutex> g(lock);
	state().last_report = String();
}

void YhdeLogger::_log_error(const String &function, const String &file, int32_t line, const String &code,
		const String &rationale, bool editor_notify, int32_t error_type,
		const TypedArray<Ref<ScriptBacktrace>> &script_backtraces) {
	static const char *kinds[] = { "ERROR", "WARNING", "SCRIPT ERROR", "SHADER ERROR" };
	const char *kind = error_type >= 0 && error_type < 4 ? kinds[error_type] : "ERROR";
	String what = rationale.is_empty() ? code : rationale;
	CrashLog::engine(String(kind) + ": " + what + " (" + file + ":" + String::num_int64(line) + ")");
}

void YhdeLogger::_log_message(const String &message, bool error) {
	if (error) CrashLog::engine(message.strip_edges());
}

} // namespace yhde
