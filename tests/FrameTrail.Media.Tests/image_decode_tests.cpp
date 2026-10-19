#include "image_decode.hpp"
#include <cstdlib>
#include <iostream>

int main() {
    using frametrail::media::image_decode_size;
    auto require = [](bool value) { if (!value) { std::cerr << "Image decode bounds failed\n"; std::exit(1); } };
    require(!image_decode_size(0, 2160) && !image_decode_size(3840, 0));
    require(!image_decode_size(8193, 1) && !image_decode_size(1, 8193));
    auto native = image_decode_size(3840, 2160); require(native && native->width == 3840 && native->height == 2160);
    auto wallpaper = image_decode_size(7680, 7680); require(wallpaper && wallpaper->width == 4096 && wallpaper->height == 4096);
    for (uint32_t width = 1; width <= 8192; width += 137) for (uint32_t height = 1; height <= 8192; height += 131) {
        auto size = image_decode_size(width, height);
        require(size && size->width <= width && size->height <= height && uint64_t(size->width) * size->height <= 16'777'216);
        require(size->width && size->height);
    }
    std::cout << "Full-resolution wallpaper inputs retain bounded decode dimensions\n";
}
