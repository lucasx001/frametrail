#pragma once
#include <cstdint>
#include <cstring>
#include <istream>
#include <ostream>
#include <stdexcept>

namespace frametrail::media {
// Small files keep the legacy 44-byte layout. Large files use EBU RF64/ds64.
inline uint64_t write_pcm_header(std::ostream& out, uint64_t frames) {
    const uint64_t bytes = frames * 4;
    auto u32 = [&](uint32_t n) { out.write(reinterpret_cast<const char*>(&n), 4); };
    auto u64 = [&](uint64_t n) { out.write(reinterpret_cast<const char*>(&n), 8); };
    const bool rf64 = bytes > UINT32_MAX - 36;
    out.write(rf64 ? "RF64" : "RIFF", 4); u32(rf64 ? UINT32_MAX : static_cast<uint32_t>(bytes + 36)); out.write("WAVE", 4);
    if (rf64) { out.write("ds64", 4); u32(28); u64(bytes + 72); u64(bytes); u64(frames); u32(0); }
    out.write("fmt ", 4); u32(16); u32(0x00020001); u32(48000); u32(192000); u32(0x00100004);
    out.write("data", 4); u32(rf64 ? UINT32_MAX : static_cast<uint32_t>(bytes));
    return rf64 ? 80 : 44;
}
inline uint64_t pcm_data_offset(std::istream& in) {
    char header[12]{}; in.read(header, 12);
    if (!in || (memcmp(header, "RIFF", 4) && memcmp(header, "RF64", 4)) || memcmp(header + 8, "WAVE", 4)) throw std::runtime_error("Invalid PCM file");
    for (int i = 0; i < 16; ++i) {
        char id[4]{}; uint32_t size{}; in.read(id, 4); in.read(reinterpret_cast<char*>(&size), 4);
        if (!in) break;
        if (!memcmp(id, "data", 4)) return static_cast<uint64_t>(in.tellg());
        in.seekg(static_cast<std::streamoff>(size) + (size & 1), std::ios::cur);
    }
    throw std::runtime_error("PCM data chunk missing");
}
}
