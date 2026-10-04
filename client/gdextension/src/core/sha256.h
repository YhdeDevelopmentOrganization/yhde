#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <string>

namespace yhde {

// SHA-256 (FIPS 180-4), streaming. Names asset contents (assets.md); it
// runs on worker threads, so it depends on nothing from the engine.
class Sha256 {
public:
	Sha256() { reset(); }
	void reset();
	void update(const void *data, size_t size);
	std::array<uint8_t, 32> finish();
	// Lowercase hex of the digest.
	std::string finish_hex();

	static std::string hex(const void *data, size_t size);

private:
	void block(const uint8_t *p);

	uint32_t h_[8];
	uint8_t buffer_[64];
	size_t buffered_ = 0;
	uint64_t length_ = 0;
};

} // namespace yhde
