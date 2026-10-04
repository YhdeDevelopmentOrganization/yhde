#include "core/msgpack.h"

#include <cstring>

namespace yhde {

// Writer

void MsgPackWriter::put16(uint16_t v) {
	buffer.push_back(uint8_t(v >> 8));
	buffer.push_back(uint8_t(v));
}

void MsgPackWriter::put32(uint32_t v) {
	for (int s = 24; s >= 0; s -= 8) buffer.push_back(uint8_t(v >> s));
}

void MsgPackWriter::put64(uint64_t v) {
	for (int s = 56; s >= 0; s -= 8) buffer.push_back(uint8_t(v >> s));
}

void MsgPackWriter::nil() { put8(0xC0); }

void MsgPackWriter::boolean(bool v) { put8(v ? 0xC3 : 0xC2); }

void MsgPackWriter::integer(int64_t v) {
	if (v >= 0) {
		if (v <= 0x7F) {
			put8(uint8_t(v));
		} else if (v <= 0xFF) {
			put8(0xCC);
			put8(uint8_t(v));
		} else if (v <= 0xFFFF) {
			put8(0xCD);
			put16(uint16_t(v));
		} else if (v <= 0xFFFFFFFFll) {
			put8(0xCE);
			put32(uint32_t(v));
		} else {
			put8(0xCF);
			put64(uint64_t(v));
		}
	} else {
		if (v >= -32) {
			put8(uint8_t(int8_t(v)));
		} else if (v >= -128) {
			put8(0xD0);
			put8(uint8_t(int8_t(v)));
		} else if (v >= -32768) {
			put8(0xD1);
			put16(uint16_t(int16_t(v)));
		} else if (v >= -2147483648ll) {
			put8(0xD2);
			put32(uint32_t(int32_t(v)));
		} else {
			put8(0xD3);
			put64(uint64_t(v));
		}
	}
}

void MsgPackWriter::str(const std::string &v) {
	size_t n = v.size();
	if (n <= 31) {
		put8(uint8_t(0xA0 | n));
	} else if (n <= 0xFF) {
		put8(0xD9);
		put8(uint8_t(n));
	} else if (n <= 0xFFFF) {
		put8(0xDA);
		put16(uint16_t(n));
	} else {
		put8(0xDB);
		put32(uint32_t(n));
	}
	buffer.insert(buffer.end(), v.begin(), v.end());
}

void MsgPackWriter::bin(const uint8_t *data, size_t size) {
	if (size <= 0xFF) {
		put8(0xC4);
		put8(uint8_t(size));
	} else if (size <= 0xFFFF) {
		put8(0xC5);
		put16(uint16_t(size));
	} else {
		put8(0xC6);
		put32(uint32_t(size));
	}
	if (size) buffer.insert(buffer.end(), data, data + size);
}

void MsgPackWriter::array_header(uint32_t count) {
	if (count <= 15) {
		put8(uint8_t(0x90 | count));
	} else if (count <= 0xFFFF) {
		put8(0xDC);
		put16(uint16_t(count));
	} else {
		put8(0xDD);
		put32(count);
	}
}

void MsgPackWriter::map_header(uint32_t count) {
	if (count <= 15) {
		put8(uint8_t(0x80 | count));
	} else if (count <= 0xFFFF) {
		put8(0xDE);
		put16(uint16_t(count));
	} else {
		put8(0xDF);
		put32(count);
	}
}

// Value helpers

int64_t MpValue::as_int(int64_t fallback) const {
	switch (type) {
		case INT:
			return is_unsigned_big ? fallback : i;
		case BOOL:
			return b ? 1 : 0;
		case FLOAT:
			return int64_t(f);
		default:
			return fallback;
	}
}

bool MpValue::as_bool(bool fallback) const {
	if (type == BOOL) return b;
	if (type == INT) return i != 0;
	return fallback;
}

const MpValue &MpValue::at(size_t index) const {
	static const MpValue kNil;
	return index < items.size() ? items[index] : kNil;
}

// Reader

namespace {

constexpr int kMaxDepth = 32;

struct Reader {
	const uint8_t *p;
	const uint8_t *end;
	std::string &error;

	bool need(size_t n) {
		if (size_t(end - p) < n) {
			error = "truncated message";
			return false;
		}
		return true;
	}

	bool u8(uint8_t &v) {
		if (!need(1)) return false;
		v = *p++;
		return true;
	}
	bool u16(uint16_t &v) {
		if (!need(2)) return false;
		v = uint16_t((p[0] << 8) | p[1]);
		p += 2;
		return true;
	}
	bool u32(uint32_t &v) {
		if (!need(4)) return false;
		v = (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) | uint32_t(p[3]);
		p += 4;
		return true;
	}
	bool u64(uint64_t &v) {
		if (!need(8)) return false;
		v = 0;
		for (int k = 0; k < 8; k++) v = (v << 8) | p[k];
		p += 8;
		return true;
	}

	bool raw(size_t n, std::string &out) {
		if (!need(n)) return false;
		out.assign(reinterpret_cast<const char *>(p), n);
		p += n;
		return true;
	}

	bool array(uint32_t n, MpValue &out, int depth) {
		// Every element needs at least one byte: reject absurd counts up front.
		if (!need(n)) return false;
		out.type = MpValue::ARRAY;
		out.items.resize(n);
		for (uint32_t k = 0; k < n; k++) {
			if (!value(out.items[k], depth + 1)) return false;
		}
		return true;
	}

	bool map(uint32_t n, MpValue &out, int depth) {
		if (!need(size_t(n) * 2)) return false;
		out.type = MpValue::MAP;
		out.pairs.resize(n);
		for (uint32_t k = 0; k < n; k++) {
			if (!value(out.pairs[k].first, depth + 1)) return false;
			if (!value(out.pairs[k].second, depth + 1)) return false;
		}
		return true;
	}

	bool value(MpValue &out, int depth) {
		if (depth > kMaxDepth) {
			error = "message nested too deeply";
			return false;
		}
		uint8_t t;
		if (!u8(t)) return false;

		if (t <= 0x7F) {
			out.type = MpValue::INT;
			out.i = t;
			return true;
		}
		if (t >= 0xE0) {
			out.type = MpValue::INT;
			out.i = int8_t(t);
			return true;
		}
		if ((t & 0xF0) == 0x80) return map(t & 0x0F, out, depth);
		if ((t & 0xF0) == 0x90) return array(t & 0x0F, out, depth);
		if ((t & 0xE0) == 0xA0) {
			out.type = MpValue::STR;
			return raw(t & 0x1F, out.bytes);
		}

		uint8_t a8;
		uint16_t a16;
		uint32_t a32;
		uint64_t a64;
		switch (t) {
			case 0xC0:
				out.type = MpValue::NIL;
				return true;
			case 0xC2:
			case 0xC3:
				out.type = MpValue::BOOL;
				out.b = (t == 0xC3);
				return true;
			case 0xC4:
				out.type = MpValue::BIN;
				return u8(a8) && raw(a8, out.bytes);
			case 0xC5:
				out.type = MpValue::BIN;
				return u16(a16) && raw(a16, out.bytes);
			case 0xC6:
				out.type = MpValue::BIN;
				return u32(a32) && raw(a32, out.bytes);
			case 0xC7:
			case 0xC8:
			case 0xC9: {
				uint32_t n = 0;
				if (t == 0xC7) {
					if (!u8(a8)) return false;
					n = a8;
				} else if (t == 0xC8) {
					if (!u16(a16)) return false;
					n = a16;
				} else {
					if (!u32(a32)) return false;
					n = a32;
				}
				out.type = MpValue::EXT;
				return need(1) && (p++, raw(n, out.bytes));
			}
			case 0xCA: {
				if (!u32(a32)) return false;
				float fl;
				std::memcpy(&fl, &a32, 4);
				out.type = MpValue::FLOAT;
				out.f = fl;
				return true;
			}
			case 0xCB: {
				if (!u64(a64)) return false;
				std::memcpy(&out.f, &a64, 8);
				out.type = MpValue::FLOAT;
				return true;
			}
			case 0xCC:
				out.type = MpValue::INT;
				if (!u8(a8)) return false;
				out.i = a8;
				return true;
			case 0xCD:
				out.type = MpValue::INT;
				if (!u16(a16)) return false;
				out.i = a16;
				return true;
			case 0xCE:
				out.type = MpValue::INT;
				if (!u32(a32)) return false;
				out.i = a32;
				return true;
			case 0xCF:
				out.type = MpValue::INT;
				if (!u64(a64)) return false;
				out.u = a64;
				out.i = int64_t(a64);
				out.is_unsigned_big = a64 > uint64_t(INT64_MAX);
				return true;
			case 0xD0:
				out.type = MpValue::INT;
				if (!u8(a8)) return false;
				out.i = int8_t(a8);
				return true;
			case 0xD1:
				out.type = MpValue::INT;
				if (!u16(a16)) return false;
				out.i = int16_t(a16);
				return true;
			case 0xD2:
				out.type = MpValue::INT;
				if (!u32(a32)) return false;
				out.i = int32_t(a32);
				return true;
			case 0xD3:
				out.type = MpValue::INT;
				if (!u64(a64)) return false;
				out.i = int64_t(a64);
				return true;
			case 0xD4:
			case 0xD5:
			case 0xD6:
			case 0xD7:
			case 0xD8: {
				static const uint32_t sizes[] = { 1, 2, 4, 8, 16 };
				out.type = MpValue::EXT;
				return need(1) && (p++, raw(sizes[t - 0xD4], out.bytes));
			}
			case 0xD9:
				out.type = MpValue::STR;
				return u8(a8) && raw(a8, out.bytes);
			case 0xDA:
				out.type = MpValue::STR;
				return u16(a16) && raw(a16, out.bytes);
			case 0xDB:
				out.type = MpValue::STR;
				return u32(a32) && raw(a32, out.bytes);
			case 0xDC:
				return u16(a16) && array(a16, out, depth);
			case 0xDD:
				return u32(a32) && array(a32, out, depth);
			case 0xDE:
				return u16(a16) && map(a16, out, depth);
			case 0xDF:
				return u32(a32) && map(a32, out, depth);
			default:
				error = "invalid MessagePack type byte";
				return false;
		}
	}
};

} // namespace

bool msgpack_decode(const uint8_t *data, size_t size, MpValue &out, std::string &error) {
	Reader r{ data, data + size, error };
	out = MpValue();
	if (!r.value(out, 0)) return false;
	if (r.p != r.end) {
		error = "trailing bytes after message";
		return false;
	}
	return true;
}

} // namespace yhde
