#include "capture.h"
#include <iostream>
#include <limits>

namespace {
int failures{};
void expect(bool value, char const* name) { if (!value) { ++failures; std::cerr << "FAIL: " << name << '\n'; } }
}
int main() {
    static_assert(sizeof(ft_capture_source) == 552);
    static_assert(sizeof(ft_capture_options) == 56);
    static_assert(sizeof(ft_capture_status) == 1136);
    static_assert(sizeof(ft_capture_capabilities) == 24);
    expect(ft_capture_get_capabilities(nullptr) < 0, "capability null rejected");
    expect(ft_capture_enumerate_sources(nullptr, 1, nullptr) < 0, "source invalid array rejected");
    expect(ft_capture_start(nullptr, nullptr) < 0, "invalid start rejected");
    expect(ft_capture_pause(nullptr) < 0, "null pause rejected");
    expect(ft_capture_stop(nullptr) < 0, "null stop rejected");
    ft_capture_destroy(nullptr);
    int32_t region[4]{};
    expect(ft_capture_region_from_dip(120, 10, 20, 800, 600, 2560, 1440, region) == 0, "125 percent region accepted");
    expect(region[0] == 13 && region[1] == 25 && region[2] == 998 && region[3] == 750, "125 percent inward crop and even dimensions");
    expect(ft_capture_region_from_dip(144, 10, 20, 800, 600, 2560, 1440, region) == 0, "150 percent region accepted");
    expect(region[0] == 15 && region[1] == 30 && region[2] == 1200 && region[3] == 900, "150 percent physical crop");
    expect(ft_capture_region_from_dip(192, 10, 20, 800, 600, 2560, 1440, region) == 0, "200 percent region accepted");
    expect(region[0] == 20 && region[1] == 40 && region[2] == 1600 && region[3] == 1200, "200 percent physical crop");
    expect(ft_capture_region_from_dip(96, -1, 0, 100, 100, 1920, 1080, region) < 0, "negative local origin rejected");
    expect(ft_capture_region_from_dip(96, 1800, 0, 200, 100, 1920, 1080, region) < 0, "cross-display region rejected");
    expect(ft_capture_region_from_dip(96, std::numeric_limits<double>::quiet_NaN(), 0, 100, 100, 1920, 1080, region) < 0, "NaN rejected");
    expect(ft_capture_region_from_dip(96, 0, 0, std::numeric_limits<double>::max(), 100, 1920, 1080, region) < 0, "overflow rejected");
    expect(ft_capture_region_from_dip(96, 0, 0, 1, 1, 1920, 1080, region) < 0, "sub-encoding-size rejected");
    std::cout << "capture-contract failures=" << failures << '\n'; return failures ? 1 : 0;
}
