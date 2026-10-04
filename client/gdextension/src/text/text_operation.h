#pragma once

#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/string.hpp>

#include <cstdint>
#include <vector>

namespace yhde {

// One edit of a text file (text_editing.md). Mirrors the server's
// TextOperation (server/src/YHDE.Server/Text/TextOperation.cs): components
// walk the whole text; lengths are Unicode code points (godot::String units).
// JSON form: a positive number retains, a negative number deletes, a string
// inserts.
class TextOperation {
public:
	enum class Kind : uint8_t { Retain, Insert, Delete };
	struct Component {
		Kind kind = Kind::Retain;
		int64_t count = 0;   // retain / delete
		godot::String text;  // insert
		int64_t length() const { return kind == Kind::Insert ? text.length() : count; }
	};

	TextOperation &retain(int64_t n);
	TextOperation &insert(const godot::String &s);
	TextOperation &del(int64_t n);

	const std::vector<Component> &components() const { return ops_; }
	int64_t base_length() const { return base_; }
	int64_t target_length() const { return target_; }
	bool is_noop() const;

	// Returns false (and leaves `out` untouched) when the text does not fit.
	bool apply(const godot::String &text, godot::String &out) const;

	// (a', b') with apply(apply(s, a), b') == apply(apply(s, b), a');
	// where both insert at one place, a's text goes first.
	static bool transform(const TextOperation &a, const TextOperation &b, TextOperation &a2, TextOperation &b2);
	// One edit that does what `a` then `b` do.
	static bool compose(const TextOperation &a, const TextOperation &b, TextOperation &out);
	// The edit that turns `before` into `after` (one changed region).
	static TextOperation diff(const godot::String &before, const godot::String &after);
	// The edit that undoes this one, given the text it was applied to.
	TextOperation invert(const godot::String &before) const;

	static bool from_array(const godot::Array &a, TextOperation &out);
	godot::Array to_array() const;

private:
	std::vector<Component> ops_;
	int64_t base_ = 0;
	int64_t target_ = 0;
};

// Editors show text without carriage returns and without a byte order mark;
// the server edits the same normalized text.
godot::String normalize_text(const godot::String &s);

} // namespace yhde
