#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>

namespace frametrail::core {
inline double cursor_idle_opacity(int64_t ticks, int64_t activity, bool hide, double delay) {
    return hide ? std::clamp(1.0-(double(ticks-activity)/10000000.0-delay)/0.2,0.0,1.0) : 1.0;
}
inline double cursor_click_progress(int64_t ticks, int64_t click) {
    return std::clamp(double(ticks-click)/5500000.0,0.0,1.0);
}
}
