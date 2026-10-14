#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <stdexcept>
#include <vector>
namespace frametrail::audio {
// Shared monotonic clock. A sample on a pause's left boundary is excluded; the resume boundary is included.
class SessionClock {
public:
    struct Pause { int64_t begin, end; };
    int64_t epoch = 0, frequency = 0;
    std::vector<Pause> pauses;
    void set_epoch(int64_t e, int64_t f) {
        if (e <= 0 || f <= 0 || frequency) throw std::invalid_argument("invalid or duplicate epoch");
        epoch = e; frequency = f;
    }
    void pause(int64_t qpc) {
        if (!frequency || qpc < epoch || (!pauses.empty() && (pauses.back().end == 0 || qpc < pauses.back().end)))
            throw std::invalid_argument("pause out of order");
        pauses.push_back({qpc, 0});
    }
    void resume(int64_t qpc) {
        if (pauses.empty() || pauses.back().end || qpc < pauses.back().begin) throw std::invalid_argument("resume out of order");
        pauses.back().end = qpc;
    }
    bool active(int64_t qpc) const {
        if (!frequency || qpc < epoch) return false;
        for (auto p : pauses) if (qpc >= p.begin && (!p.end || qpc < p.end)) return false;
        return true;
    }
    int64_t frames_at(int64_t qpc, int64_t rate = 48000) const {
        if (!frequency || qpc <= epoch) return 0;
        int64_t elapsed = qpc - epoch;
        for (auto p : pauses) if (qpc > p.begin) elapsed -= std::min(qpc, p.end ? p.end : qpc) - p.begin;
        return static_cast<int64_t>(std::llround(static_cast<long double>(std::max<int64_t>(0, elapsed)) * rate / frequency));
    }
};
}
