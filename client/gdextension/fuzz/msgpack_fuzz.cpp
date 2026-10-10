// libFuzzer target for the MessagePack decoder: every input is
// either decoded or refused, never a crash, a hang or unbounded memory.
// Windows: cl /std:c++17 /EHsc /fsanitize=address /fsanitize=fuzzer /I ..\src msgpack_fuzz.cpp ..\src\core\msgpack.cpp
// Linux:   clang++ -std=c++17 -fsanitize=address,fuzzer -I ../src msgpack_fuzz.cpp ../src/core/msgpack.cpp
#include "core/msgpack.h"

#include <cstddef>
#include <cstdint>
#include <string>

extern "C" int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size) {
	yhde::MpValue value;
	std::string error;
	yhde::msgpack_decode(data, size, value, error);
	return 0;
}
