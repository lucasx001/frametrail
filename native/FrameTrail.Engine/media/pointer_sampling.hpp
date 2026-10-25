#pragma once
#include "frametrail/core/cursor.hpp"
#include "frametrail/core/project.hpp"
#include <optional>
#include <unordered_map>

namespace frametrail::media {
struct PointerSample {
    int64_t ticks;
    float x, y;
    uint32_t shape;
    bool visible;
    uint32_t kind{};
};
struct PointerTrack {
    std::vector<PointerSample> points;
    struct Shape { std::string file; float x{}, y{}; };
    std::unordered_map<uint32_t, Shape> shapes;
};

// All current cursor effects look back at most 8.2 source seconds: the largest
// idle delay (8 s) plus its fade. Smoothing needs 2 s and clicks need 0.55 s.
inline constexpr int64_t pointer_history_ticks = 82'000'000;
inline constexpr size_t maximum_pointer_history_tracks = 128;
inline constexpr size_t maximum_pointer_history_samples = 65'536;
inline constexpr size_t maximum_cached_pointer_samples = 2'000'000;
struct PointerWindow {
    int64_t ticks{}, begin{}, end{};
    std::vector<const core::Media*> media;
};

inline std::optional<PointerWindow> pointer_window(const core::Project& project, int64_t source_ticks, int64_t offset) {
    using namespace core;
    const auto segment = std::find_if(project.segments.begin(), project.segments.end(), [&](const auto& s) {
        const auto begin = normalized_ticks(s.source_start);
        return source_ticks >= begin && source_ticks < begin + normalized_ticks(s.source_duration);
    });
    if (segment == project.segments.end()) return std::nullopt;
    const auto begin = normalized_ticks(segment->source_start);
    const auto end = begin + normalized_ticks(segment->source_duration);
    // Calibration holds the edge position instead of borrowing another edit
    // segment (including a pause/resume take or content removed by trimming).
    const auto ticks = std::clamp(source_ticks + offset, begin, end - 1);
    std::vector<const Media*> ordered;
    for (const auto& media : project.media) if (media.kind == MediaKind::pointer_events) ordered.push_back(&media);
    std::sort(ordered.begin(), ordered.end(), [](auto a, auto b) {
        return normalized_ticks(a->start_offset) < normalized_ticks(b->start_offset);
    });
    const auto selected = std::find_if(ordered.begin(), ordered.end(), [&](auto media) {
        const auto start = normalized_ticks(media->start_offset);
        return ticks >= start && ticks < start + normalized_ticks(media->duration);
    });
    if (selected == ordered.end()) return std::nullopt;
    auto first = selected;
    const auto history_begin = std::max(begin, ticks - pointer_history_ticks);
    while (first != ordered.begin()) {
        auto previous = first - 1;
        const auto previous_end = normalized_ticks((*previous)->start_offset) + normalized_ticks((*previous)->duration);
        if (previous_end != normalized_ticks((*first)->start_offset) || previous_end <= history_begin) break;
        --first;
        if (static_cast<size_t>(selected - first + 1) > maximum_pointer_history_tracks)
            throw ValidationError("pointer", "pointer history exceeds file limit");
    }
    PointerWindow result{ticks, std::max(history_begin, normalized_ticks((*first)->start_offset)), end, {}};
    result.media.assign(first, selected + 1);
    return result;
}

struct PointerHistorySample {
    PointerSample point;
    const core::Media* media;
    const PointerTrack* track;
};
struct PointerPresentation {
    PointerHistorySample latest;
    double x{}, y{}, opacity{1};
    std::vector<PointerSample> clicks;
};

// A provider loads/caches each physical file. Origin travels with each sample:
// cursor shape IDs are local to a file and may be reused at every checkpoint.
template<class Provider>
std::optional<PointerPresentation> sample_pointer_window(const PointerWindow& window, Provider&& provider,
    double smoothing, bool hide, double idle_delay) {
    using namespace core;
    std::vector<PointerHistorySample> samples;
    for (const auto* media : window.media) {
        const auto& track = provider(*media);
        const auto start = normalized_ticks(media->start_offset);
        const auto duration = normalized_ticks(media->duration);
        auto first = std::lower_bound(track.points.begin(), track.points.end(), window.begin - start,
            [](const auto& point, auto ticks) { return point.ticks < ticks; });
        // One preceding position seeds the history, but never carries a click
        // or smoothing interval from trimmed-away content into the visible cut.
        if (first != track.points.begin()) --first;
        for (auto it = first; it != track.points.end(); ++it) {
            if (it->ticks < 0 || it->ticks >= duration) continue; // physical overlap tail
            auto point = *it;
            point.ticks += start;
            if (point.ticks > window.ticks || point.ticks >= window.end) break;
            if (point.ticks < window.begin) {
                point.ticks = window.begin;
                point.kind = 0;
            }
            samples.push_back({point, media, &track});
            if (samples.size() > maximum_pointer_history_samples)
                throw ValidationError("pointer", "pointer history exceeds sample limit");
        }
    }
    if (samples.empty() || !samples.back().point.visible) return std::nullopt;
    const auto& latest = samples.back();
    int64_t activity = samples.front().point.ticks;
    double x = 0, y = 0, weight = 0;
    PointerPresentation result{latest};
    const auto smoothing_begin = window.ticks - static_cast<int64_t>(smoothing * 4 * time_scale);
    for (size_t index = 0; index < samples.size(); ++index) {
        const auto& point = samples[index].point;
        if (index > 0) {
            const auto& previous = samples[index - 1].point;
            if (point.kind != 0 || point.visible != previous.visible ||
                std::abs(point.x - previous.x) > 0.000001f || std::abs(point.y - previous.y) > 0.000001f)
                activity = point.ticks;
        }
        if (smoothing > 0 && point.ticks >= smoothing_begin) {
            const auto w = std::exp(-(window.ticks - point.ticks) / double(time_scale) / smoothing);
            x += point.x * w; y += point.y * w; weight += w;
        }
        if (point.visible && (point.kind == 1 || point.kind == 3 || point.kind == 5) && window.ticks - point.ticks < 5'500'000)
            result.clicks.push_back(point);
    }
    result.x = weight > 0 ? x / weight : latest.point.x;
    result.y = weight > 0 ? y / weight : latest.point.y;
    result.opacity = cursor_idle_opacity(window.ticks, activity, hide, idle_delay);
    return result;
}
}
