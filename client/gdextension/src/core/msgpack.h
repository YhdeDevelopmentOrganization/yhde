#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <utility>
#include <vector>

namespace yhde {

// Minimal, allocation-conscious MessagePack codec for the YHDE wire protocol
// (network_protocol.md). The server uses MessagePack-CSharp with integer
// keys, so every message is a positional array.

class MsgPackWriter {
public:
	std::vector<uint8_t> buffer;

	void nil();
	void boolean(bool v);
	void integer(int64_t v);
	void str(const std::string &v);
	void bin(const uint8_t *data, size_t size);
	void bin(const std::vector<uint8_t> &v) { bin(v.data(), v.size()); }
	void array_header(uint32_t count);
	void map_header(uint32_t count);

private:
	void put8(uint8_t v) { buffer.push_back(v); }
	void put16(uint16_t v);
	void put32(uint32_t v);
	void put64(uint64_t v);
};

// A decoded MessagePack value. Strings and binaries both land in `bytes`.
struct MpValue {
	enum Type : uint8_t { NIL, BOOL, INT, FLOAT, STR, BIN, ARRAY, MAP, EXT };

	Type type = NIL;
	bool b = false;
	int64_t i = 0;
	uint64_t u = 0; // valid when INT and the value exceeds int64 range
	bool is_unsigned_big = false;
	double f = 0.0;
	std::string bytes;
	std::vector<MpValue> items;
	std::vector<std::pair<MpValue, MpValue>> pairs;

	bool is_nil() const { return type == NIL; }
	int64_t as_int(int64_t fallback = 0) const;
	bool as_bool(bool fallback = false) const;
	const std::string &as_bytes() const { return bytes; }
	std::vector<uint8_t> as_byte_vector() const { return std::vector<uint8_t>(bytes.begin(), bytes.end()); }

	// Positional access for [Key(n)] objects; out-of-range yields a shared NIL.
	const MpValue &at(size_t index) const;
	size_t size() const { return items.size(); }
};

// Decodes exactly one value spanning the whole buffer.
bool msgpack_decode(const uint8_t *data, size_t size, MpValue &out, std::string &error);

} // namespace yhde
