#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <optional>

namespace frametrail::media {
struct ImageDecodeSize { uint32_t width, height; };
inline std::optional<ImageDecodeSize> image_decode_size(uint32_t width, uint32_t height) {
    if (!width || !height || width > 8192 || height > 8192) return std::nullopt;
    // Accept full desktop wallpaper originals while keeping each GPU bitmap at <= 64 MiB.
    const double scale = std::min(1.0, std::sqrt(16'777'216.0 / (double(width) * height)));
    return ImageDecodeSize{std::max(1u, uint32_t(std::floor(width * scale))), std::max(1u, uint32_t(std::floor(height * scale)))};
}
}
