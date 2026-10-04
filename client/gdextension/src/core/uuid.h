#pragma once

#include <godot_cpp/variant/string.hpp>

#include <array>
#include <cstddef>
#include <cstdint>
#include <functional>

namespace yhde {

// 128-bit identifier. Bytes are stored in RFC 4122 (big-endian) order, so the
// canonical string form matches what the C# server prints for the same Guid.
//
// The server speaks .NET Guid.ToByteArray() layout on the wire (the first three
// fields little-endian); to_wire()/from_wire() convert between the two.
struct Uuid {
	std::array<uint8_t, 16> bytes{};

	bool is_nil() const;

	bool operator==(const Uuid &o) const { return bytes == o.bytes; }
	bool operator!=(const Uuid &o) const { return bytes != o.bytes; }
	bool operator<(const Uuid &o) const { return bytes < o.bytes; }

	// Random (version 4) identifier.
	static Uuid random();
	// Name-based (version 5, SHA-1) identifier: deterministic for (ns, name).
	static Uuid from_name(const Uuid &ns, const godot::String &name);

	// Canonical 8-4-4-4-12 lowercase hex.
	godot::String to_string() const;
	static bool parse(const godot::String &text, Uuid &out);
	static Uuid parse_or_nil(const godot::String &text);

	std::array<uint8_t, 16> to_wire() const;
	static bool from_wire(const uint8_t *data, size_t size, Uuid &out);
};

struct UuidHash {
	size_t operator()(const Uuid &u) const noexcept;
};

// Root namespace for every name-based id YHDE derives.
const Uuid &yhde_namespace();

// SHA-1 of arbitrary bytes (used for v5 ids and color hashing).
std::array<uint8_t, 20> sha1(const uint8_t *data, size_t size);

} // namespace yhde
