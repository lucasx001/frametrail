#pragma once
#include "project.hpp"
#include <optional>

namespace frametrail::core {

struct ResolvedSegment {
    std::string id;
    std::int64_t source_start{}, source_duration{}, output_start{}, output_duration{};
    Rational rate;
    std::int64_t source_end() const noexcept { return source_start + source_duration; }
    std::int64_t output_end() const noexcept { return output_start + output_duration; }
};
struct MappedTime { std::string segment_id; std::int64_t ticks{}; };
enum class EndpointPolicy { half_open, include_final_endpoint };
enum class PointerKind { move, left_down, left_up, right_down, right_up };
struct PointerEvent {
    std::int64_t ticks{};
    double x{}, y{}; // normalized content coordinates
    PointerKind kind{PointerKind::move};
    std::string cursor_id;
};
struct PointerGroup {
    std::string segment_id;
    std::int64_t output_start{}, output_end{};
    std::vector<PointerEvent> events;
};

class TimelineMap final {
public:
    TimelineMap(Time source_duration, const std::vector<Segment>& segments);
    const std::vector<ResolvedSegment>& segments() const noexcept { return segments_; }
    std::int64_t output_duration() const noexcept { return output_duration_; }
    const ResolvedSegment* segment_at(std::int64_t output_ticks) const noexcept;
    std::optional<MappedTime> source_time(std::int64_t output_ticks,
        EndpointPolicy policy = EndpointPolicy::half_open) const;
    std::optional<MappedTime> output_time(std::int64_t source_ticks,
        EndpointPolicy policy = EndpointPolicy::half_open) const;
    std::vector<PointerGroup> map_pointer_events(const std::vector<PointerEvent>& events) const;
private:
    std::vector<ResolvedSegment> segments_;
    std::int64_t output_duration_{};
};

// Sampling is confined to one edit segment. No interpolation can cross a cut.
std::optional<PointerEvent> sample_pointer(const PointerGroup& group, std::int64_t output_ticks);

} // namespace frametrail::core
