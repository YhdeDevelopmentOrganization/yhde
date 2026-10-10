// Decoder limits: a crafted message of many small elements is
// refused before it takes much memory; real-sized messages still decode.
// Build and run: see msgpack_fuzz.cpp (same flags, without fuzzer).
#include "core/msgpack.h"

#include <cstdio>
#include <string>
#include <vector>

static int fails = 0;
static void check(bool ok, const char *what) {
	if (!ok) {
		std::printf("FAIL %s\n", what);
		fails++;
	}
}

static std::vector<uint8_t> array32(uint32_t n, uint8_t element) {
	std::vector<uint8_t> v = { 0xDD, uint8_t(n >> 24), uint8_t(n >> 16), uint8_t(n >> 8), uint8_t(n) };
	v.insert(v.end(), n, element);
	return v;
}

int main() {
	yhde::MpValue out;
	std::string error;

	// 4 million nils: 4 MB on the wire, ~500 MB decoded. Refused.
	std::vector<uint8_t> flood = array32(4u << 20, 0xC0);
	check(!yhde::msgpack_decode(flood.data(), flood.size(), out, error), "a 4M-element array is refused");
	check(error == "message has too many values", "it says why");

	// Many small arrays nested in one: the total counts, not each array.
	std::vector<uint8_t> nested = { 0xDD, 0x00, 0x10, 0x00, 0x00 }; // 1M arrays...
	for (int i = 0; i < (1 << 20); i++) nested.push_back(0x91), nested.push_back(0xC0); // ...of one nil each
	error.clear();
	check(!yhde::msgpack_decode(nested.data(), nested.size(), out, error), "2M values across many arrays are refused");

	// 33 levels deep: refused.
	std::vector<uint8_t> deep(33, 0x91);
	deep.push_back(0xC0);
	error.clear();
	check(!yhde::msgpack_decode(deep.data(), deep.size(), out, error), "too deep is refused");

	// A real-sized message (a catch-up page: 256 ops of 12 fields) decodes.
	std::vector<uint8_t> page = { 0xDC, 0x01, 0x00 };
	for (int op = 0; op < 256; op++) {
		page.push_back(0x9C);
		for (int f = 0; f < 12; f++) page.push_back(0x01);
	}
	error.clear();
	check(yhde::msgpack_decode(page.data(), page.size(), out, error) && out.items.size() == 256, "a catch-up page decodes");

	std::printf("msgpack limits: fails=%d\n", fails);
	return fails == 0 ? 0 : 1;
}
