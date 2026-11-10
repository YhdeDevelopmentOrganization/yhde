#pragma once

#include <godot_cpp/classes/logger.hpp>
#include <godot_cpp/classes/script_backtrace.hpp>
#include <godot_cpp/variant/string.hpp>
#include <godot_cpp/variant/typed_array.hpp>

namespace yhde {

// What YHDE did last and what Godot reported last, kept in
// .godot/yhde/last.log, so that an editor that closes unexpectedly leaves
// something to send (reliability.md). A marker file says the editor is
// running; a clean exit removes it.
class CrashLog {
public:
	// Reads what the last run left (a report when it did not end cleanly) and
	// starts this run's log.
	static void start();
	// Clean exit.
	static void stop();
	// Something YHDE does. `now` writes the log at once (before work that
	// could take the editor down).
	static void note(const godot::String &line, bool now = false);
	// An error or warning from Godot (any thread).
	static void engine(const godot::String &line);
	// Writes the log if it changed and the last write is old enough.
	static void flush(bool force = false);
	// "" when the last run ended cleanly.
	static godot::String report();
	static void clear_report();
};

// Passes Godot's errors and warnings to the CrashLog.
class YhdeLogger : public godot::Logger {
	GDCLASS(YhdeLogger, godot::Logger)

public:
	void _log_error(const godot::String &function, const godot::String &file, int32_t line, const godot::String &code,
			const godot::String &rationale, bool editor_notify, int32_t error_type,
			const godot::TypedArray<godot::Ref<godot::ScriptBacktrace>> &script_backtraces) override;
	void _log_message(const godot::String &message, bool error) override;

protected:
	static void _bind_methods() {}
};

} // namespace yhde
