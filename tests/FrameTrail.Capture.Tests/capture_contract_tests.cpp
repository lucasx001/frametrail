#include "capture.h"
#include "frame_pacing.h"
#include <iostream>
#include <limits>

namespace {
int failures{};
void expect(bool value, char const* name) { if (!value) { ++failures; std::cerr << "FAIL: " << name << '\n'; } }
}
int main() {
    int admitted = 0; int64_t previous_slot = -1;
    for (int64_t i = 0; i < 600; ++i) {
        auto slot = capture_frame_slot(i * 10'000'000 / 60 + (i % 2 ? -2000 : 2000), 60);
        if (slot > previous_slot) { ++admitted; previous_slot = slot; }
    }
    expect(admitted == 600, "60 Hz jitter must not drop alternate capture frames");
    static_assert(sizeof(ft_capture_source) == 552);
    static_assert(sizeof(ft_capture_options) == 56);
    static_assert(sizeof(ft_capture_status) == 1136);
    static_assert(sizeof(ft_capture_capabilities) == 24);
    expect(ft_capture_get_capabilities(nullptr) < 0, "capability null rejected");
    expect(ft_capture_get_codec_capabilities(nullptr) < 0, "codec capability null rejected");
    ft_video_codec_capabilities unsupported{sizeof(ft_video_codec_capabilities), 99};
    expect(ft_capture_get_codec_capabilities(&unsupported) < 0, "unknown codec rejected");
    ft_capture_handle invalid_codec_handle = reinterpret_cast<void*>(1);
    expect(ft_capture_start_with_codec(nullptr, 99, &invalid_codec_handle) < 0 && !invalid_codec_handle, "invalid codec start clears handle");
    expect(ft_capture_enumerate_sources(nullptr, 1, nullptr) < 0, "source invalid array rejected");
    expect(ft_capture_start(nullptr, nullptr) < 0, "invalid start rejected");
    expect(ft_capture_pause(nullptr) < 0, "null pause rejected");
    expect(ft_capture_stop(nullptr) < 0, "null stop rejected");
    expect(ft_capture_get_performance(nullptr, nullptr) < 0, "null performance rejected");
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
