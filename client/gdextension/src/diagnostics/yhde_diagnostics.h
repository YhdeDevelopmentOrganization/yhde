#pragma once

#include <godot_cpp/classes/object.hpp>
#include <godot_cpp/classes/ref_counted.hpp>
#include <godot_cpp/variant/dictionary.hpp>

#include <memory>
#include <vector>

namespace yhde {
class SyncDocument;
}

namespace godot {

// Test-only hooks into the native codec. Compiled only with
// -DYHDE_DIAGNOSTICS=ON; release builds never contain this class.
class YhdeDiagnostics : public RefCounted {
	GDCLASS(YhdeDiagnostics, RefCounted)

public:
	// Encode -> JSON text -> parse -> decode. Returns {ok, value, json, error}.
	Dictionary roundtrip(const Variant &value, bool allow_builtin_scripts);
	// Serialize every stored property of `object`, rebuild a fresh object of
	// the same class from the JSON, and list properties that differ.
	Dictionary roundtrip_object(Object *object);
	bool values_equal(const Variant &a, const Variant &b);
	PackedStringArray stored_properties(Object *object);

	// Mirror test: two documents for the same file, A edited locally, its
	// operations sent through JSON to B. `a`/`b` are scene roots or resources.
	int64_t mirror_open(const String &path, Object *a, Object *b);
	// Diff A, apply to B, then diff B (must be silent). Returns
	// {ops: [json], errors: [String], echo: [json]}.
	Dictionary mirror_step(int64_t handle);
	void mirror_close(int64_t handle);

	// The code-approval gate (AssetSync::may_run_in_editor) on a file.
	bool may_run_in_editor(const String &path, const String &bytes_file);
	// Server address rules (core/url.h): {ok, scheme, host, userinfo, loopback}.
	Dictionary parse_url(const String &address);
	// Why a project path is not shared ("" when it is): AssetSync::exclusion_reason.
	String exclusion_reason(const String &path);

	YhdeDiagnostics();
	~YhdeDiagnostics();

private:
	std::vector<std::pair<std::unique_ptr<yhde::SyncDocument>, std::unique_ptr<yhde::SyncDocument>>> mirrors_;

protected:
	static void _bind_methods();
};

} // namespace godot
