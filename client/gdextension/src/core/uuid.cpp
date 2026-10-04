#include "core/uuid.h"

#include <godot_cpp/variant/char_string.hpp>

#include <chrono>
#include <cstring>
#include <mutex>
#include <random>
#include <vector>

namespace yhde {

namespace {

inline uint32_t rotl(uint32_t v, int n) { return (v << n) | (v >> (32 - n)); }

std::mt19937_64 &rng() {
	static std::mt19937_64 engine = [] {
		std::random_device rd;
		std::seed_seq seq{ rd(), rd(), rd(), rd(),
			static_cast<unsigned>(std::chrono::high_resolution_clock::now().time_since_epoch().count()) };
		return std::mt19937_64(seq);
	}();
	return engine;
}

std::mutex &rng_mutex() {
	static std::mutex m;
	return m;
}

int hex_value(char32_t c) {
	if (c >= '0' && c <= '9') return int(c - '0');
	if (c >= 'a' && c <= 'f') return int(c - 'a' + 10);
	if (c >= 'A' && c <= 'F') return int(c - 'A' + 10);
	return -1;
}

} // namespace

std::array<uint8_t, 20> sha1(const uint8_t *data, size_t size) {
	uint32_t h[5] = { 0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u, 0xC3D2E1F0u };

	const uint64_t bit_len = uint64_t(size) * 8u;
	size_t padded = ((size + 8) / 64 + 1) * 64;
	std::vector<uint8_t> msg(padded, 0);
	if (size) std::memcpy(msg.data(), data, size);
	msg[size] = 0x80;
	for (int i = 0; i < 8; i++) {
		msg[padded - 1 - i] = uint8_t(bit_len >> (8 * i));
	}

	uint32_t w[80];
	for (size_t off = 0; off < padded; off += 64) {
		for (int i = 0; i < 16; i++) {
			w[i] = (uint32_t(msg[off + 4 * i]) << 24) | (uint32_t(msg[off + 4 * i + 1]) << 16) |
					(uint32_t(msg[off + 4 * i + 2]) << 8) | uint32_t(msg[off + 4 * i + 3]);
		}
		for (int i = 16; i < 80; i++) {
			w[i] = rotl(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
		}
		uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
		for (int i = 0; i < 80; i++) {
			uint32_t f, k;
			if (i < 20) {
				f = (b & c) | (~b & d);
				k = 0x5A827999u;
			} else if (i < 40) {
				f = b ^ c ^ d;
				k = 0x6ED9EBA1u;
			} else if (i < 60) {
				f = (b & c) | (b & d) | (c & d);
				k = 0x8F1BBCDCu;
			} else {
				f = b ^ c ^ d;
				k = 0xCA62C1D6u;
			}
			uint32_t t = rotl(a, 5) + f + e + k + w[i];
			e = d;
			d = c;
			c = rotl(b, 30);
			b = a;
			a = t;
		}
		h[0] += a;
		h[1] += b;
		h[2] += c;
		h[3] += d;
		h[4] += e;
	}

	std::array<uint8_t, 20> out{};
	for (int i = 0; i < 5; i++) {
		out[4 * i] = uint8_t(h[i] >> 24);
		out[4 * i + 1] = uint8_t(h[i] >> 16);
		out[4 * i + 2] = uint8_t(h[i] >> 8);
		out[4 * i + 3] = uint8_t(h[i]);
	}
	return out;
}

bool Uuid::is_nil() const {
	for (uint8_t b : bytes) {
		if (b) return false;
	}
	return true;
}

Uuid Uuid::random() {
	Uuid u;
	{
		std::lock_guard<std::mutex> lock(rng_mutex());
		uint64_t a = rng()();
		uint64_t b = rng()();
		std::memcpy(u.bytes.data(), &a, 8);
		std::memcpy(u.bytes.data() + 8, &b, 8);
	}
	u.bytes[6] = uint8_t((u.bytes[6] & 0x0F) | 0x40);
	u.bytes[8] = uint8_t((u.bytes[8] & 0x3F) | 0x80);
	return u;
}

Uuid Uuid::from_name(const Uuid &ns, const godot::String &name) {
	godot::CharString utf8 = name.utf8();
	std::vector<uint8_t> buf(16 + size_t(utf8.length()));
	std::memcpy(buf.data(), ns.bytes.data(), 16);
	if (utf8.length() > 0) std::memcpy(buf.data() + 16, utf8.get_data(), size_t(utf8.length()));
	std::array<uint8_t, 20> digest = sha1(buf.data(), buf.size());
	Uuid u;
	std::memcpy(u.bytes.data(), digest.data(), 16);
	u.bytes[6] = uint8_t((u.bytes[6] & 0x0F) | 0x50);
	u.bytes[8] = uint8_t((u.bytes[8] & 0x3F) | 0x80);
	return u;
}

godot::String Uuid::to_string() const {
	static const char *hex = "0123456789abcdef";
	char out[37];
	int p = 0;
	for (int i = 0; i < 16; i++) {
		if (i == 4 || i == 6 || i == 8 || i == 10) out[p++] = '-';
		out[p++] = hex[bytes[i] >> 4];
		out[p++] = hex[bytes[i] & 0xF];
	}
	out[p] = 0;
	return godot::String(out);
}

bool Uuid::parse(const godot::String &text, Uuid &out) {
	if (text.length() != 36) return false;
	Uuid u;
	int bi = 0;
	for (int i = 0; i < 36;) {
		if (i == 8 || i == 13 || i == 18 || i == 23) {
			if (text[i] != '-') return false;
			i++;
			continue;
		}
		int hi = hex_value(text[i]);
		int lo = hex_value(text[i + 1]);
		if (hi < 0 || lo < 0) return false;
		u.bytes[bi++] = uint8_t((hi << 4) | lo);
		i += 2;
	}
	out = u;
	return true;
}

Uuid Uuid::parse_or_nil(const godot::String &text) {
	Uuid u;
	parse(text, u);
	return u;
}

std::array<uint8_t, 16> Uuid::to_wire() const {
	std::array<uint8_t, 16> w = bytes;
	std::swap(w[0], w[3]);
	std::swap(w[1], w[2]);
	std::swap(w[4], w[5]);
	std::swap(w[6], w[7]);
	return w;
}

bool Uuid::from_wire(const uint8_t *data, size_t size, Uuid &out) {
	if (size != 16) return false;
	std::memcpy(out.bytes.data(), data, 16);
	std::swap(out.bytes[0], out.bytes[3]);
	std::swap(out.bytes[1], out.bytes[2]);
	std::swap(out.bytes[4], out.bytes[5]);
	std::swap(out.bytes[6], out.bytes[7]);
	return true;
}

size_t UuidHash::operator()(const Uuid &u) const noexcept {
	uint64_t a, b;
	std::memcpy(&a, u.bytes.data(), 8);
	std::memcpy(&b, u.bytes.data() + 8, 8);
	return size_t(a ^ (b * 0x9E3779B97F4A7C15ull));
}

const Uuid &yhde_namespace() {
	// Fixed, arbitrary namespace for YHDE name-based ids. Never change it:
	// derived ids must match across every client and every build.
	static const Uuid ns = Uuid::parse_or_nil("5f0e6c1a-9a55-4f3e-8b0b-79d2a1c3e4f5");
	return ns;
}

} // namespace yhde
