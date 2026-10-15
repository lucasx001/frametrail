#pragma once
#include <cstdint>
inline int64_t capture_frame_slot(int64_t time_100ns, uint32_t fps) {
    // Round to the nearest phase-locked slot. Floor-only buckets can reject
    // alternate 60 Hz callbacks that jitter across the boundary by a few us.
    return (time_100ns * fps + 5'000'000) / 10'000'000;
}
