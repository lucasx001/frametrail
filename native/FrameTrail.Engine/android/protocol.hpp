#pragma once
#include <cstdint>
#include <stdexcept>
namespace frametrail::android {
inline uint32_t be32(const uint8_t* p) { return uint32_t(p[0]) << 24 | uint32_t(p[1]) << 16 | uint32_t(p[2]) << 8 | p[3]; }
struct PacketHeader { int64_t pts_us; uint32_t size; bool config, key; };
inline PacketHeader parse_header(const uint8_t* p) {
    uint64_t flags = uint64_t(be32(p)) << 32 | be32(p + 4);
    PacketHeader h{int64_t(flags & ((uint64_t(1) << 62) - 1)), be32(p + 8), bool(flags >> 63), bool(flags & (uint64_t(1) << 62))};
    if (!h.size || h.size > 16 * 1024 * 1024) throw std::runtime_error("Invalid scrcpy packet length (limit 16 MiB)");
    if (!h.config && h.pts_us > INT64_MAX / 10) throw std::runtime_error("Invalid scrcpy media timestamp");
    return h;
}
}
