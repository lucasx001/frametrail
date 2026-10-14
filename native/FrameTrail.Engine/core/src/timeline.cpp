#include "frametrail/core/timeline.hpp"
#include <algorithm>
#include <cmath>
#include <limits>
#include <unordered_set>

namespace frametrail::core {
ValidationError::ValidationError(std::string path, std::string message)
    : std::runtime_error(path + ": " + message), path_(std::move(path)) {}

Time canonical_time(std::int64_t ticks) noexcept { return {ticks, static_cast<std::int32_t>(time_scale)}; }

std::int64_t multiply_ratio(std::int64_t value, std::int64_t numerator, std::int64_t denominator) {
    if (numerator <= 0 || numerator > 1'000'000'000 || denominator <= 0 || denominator > 1'000'000'000)
        throw ValidationError("time", "invalid conversion ratio");
    if (value == (std::numeric_limits<std::int64_t>::min)())
        throw ValidationError("time", "integer overflow");
    const bool negative = value < 0;
    const auto absolute = negative ? -value : value;
    const auto whole = absolute / denominator;
    const auto remainder = absolute % denominator;
    const auto limit = (std::numeric_limits<std::int64_t>::max)();
    if (whole > limit / numerator || remainder > limit / numerator)
        throw ValidationError("time", "integer overflow");
    const auto product = remainder * numerator;
    const auto fraction = product / denominator + ((product % denominator) * 2 >= denominator ? 1 : 0);
    const auto integer = whole * numerator;
    if (integer > limit - fraction) throw ValidationError("time", "integer overflow");
    return negative ? -(integer + fraction) : integer + fraction;
}

std::int64_t normalized_ticks(Time value) {
    if (value.timescale <= 0 || value.timescale > 1'000'000'000)
        throw ValidationError("time.timescale", "must be 1..1000000000");
    const auto input_limit = static_cast<std::int64_t>(value.timescale) * 7 * 24 * 60 * 60;
    if (value.ticks < -input_limit || value.ticks > input_limit)
        throw ValidationError("time.ticks", "time exceeds seven-day source limit");
    const auto result = multiply_ratio(value.ticks, time_scale, value.timescale);
    if (result < -maximum_source_ticks || result > maximum_source_ticks)
        throw ValidationError("time.ticks", "time exceeds seven-day source limit");
    return result;
}

TimelineMap::TimelineMap(Time source_duration, const std::vector<Segment>& authored) {
    const auto full_duration = normalized_ticks(source_duration);
    if (full_duration <= 0) throw ValidationError("sourceDuration", "must be positive");
    if (authored.empty() || authored.size() > 100'000)
        throw ValidationError("segments", "must contain 1..100000 segments");
    std::unordered_set<std::string> identifiers;
    for (const auto& segment : authored) {
        if (segment.id.empty() || segment.id.size() > 128 || !identifiers.insert(segment.id).second)
            throw ValidationError("segments.id", "empty, too long, or duplicate ID");
        const auto start = normalized_ticks(segment.source_start);
        const auto duration = normalized_ticks(segment.source_duration);
        const auto numerator = static_cast<std::int64_t>(segment.rate.numerator);
        const auto denominator = static_cast<std::int64_t>(segment.rate.denominator);
        if (start < 0 || duration <= 0 || start > full_duration || duration > full_duration - start)
            throw ValidationError("segments.sourceDuration", "invalid or outside source range");
        if (numerator <= 0 || denominator <= 0 || numerator > 1'000'000 || denominator > 1'000'000 ||
            numerator * 4 < denominator || numerator > denominator * 4)
            throw ValidationError("segments.rate", "must be a rational rate in 0.25..4");
        const auto output_duration = multiply_ratio(duration, denominator, numerator);
        if (output_duration <= 0) throw ValidationError("segments.sourceDuration", "quantizes to empty output");
        if (output_duration_ > (std::numeric_limits<std::int64_t>::max)() - output_duration)
            throw ValidationError("segments", "output duration overflow");
        segments_.push_back({segment.id, start, duration, output_duration_, output_duration, segment.rate});
        output_duration_ += output_duration;
    }
    auto sorted = segments_;
    std::sort(sorted.begin(), sorted.end(), [](const auto& a, const auto& b) { return a.source_start < b.source_start; });
    for (std::size_t i = 1; i < sorted.size(); ++i)
        if (sorted[i].source_start < sorted[i - 1].source_end())
            throw ValidationError("segments", "source intervals overlap; reusing source content is disallowed");
}

const ResolvedSegment* TimelineMap::segment_at(std::int64_t time) const noexcept {
    if (time < 0 || time >= output_duration_) return nullptr;
    const auto it = std::upper_bound(segments_.begin(), segments_.end(), time,
        [](const auto t, const ResolvedSegment& segment) { return t < segment.output_end(); });
    return it == segments_.end() ? nullptr : &*it;
}

std::optional<MappedTime> TimelineMap::source_time(std::int64_t time, EndpointPolicy policy) const {
    if (policy == EndpointPolicy::include_final_endpoint && time == output_duration_)
        return MappedTime{segments_.back().id, segments_.back().source_end()};
    const auto* segment = segment_at(time);
    if (!segment) return std::nullopt;
    const auto delta = multiply_ratio(time - segment->output_start, segment->rate.numerator, segment->rate.denominator);
    return MappedTime{segment->id, segment->source_start + (std::min)(delta, segment->source_duration - 1)};
}

std::optional<MappedTime> TimelineMap::output_time(std::int64_t time, EndpointPolicy policy) const {
    // Exact authored cut boundaries belong to the following source interval.
    for (const auto& segment : segments_) {
        if (time >= segment.source_start && time < segment.source_end()) {
            const auto delta = multiply_ratio(time - segment.source_start, segment.rate.denominator, segment.rate.numerator);
            return MappedTime{segment.id, segment.output_start + (std::min)(delta, segment.output_duration - 1)};
        }
    }
    if (policy == EndpointPolicy::include_final_endpoint && time == segments_.back().source_end())
        return MappedTime{segments_.back().id, output_duration_};
    return std::nullopt;
}

std::vector<PointerGroup> TimelineMap::map_pointer_events(const std::vector<PointerEvent>& events) const {
    if (events.size() > 2'000'000) throw ValidationError("pointer", "event limit exceeded");
    auto ordered = events;
    for (const auto& event : ordered) {
        if (event.ticks < 0 || event.ticks > maximum_source_ticks || !std::isfinite(event.x) || !std::isfinite(event.y) ||
            event.x < 0 || event.x > 1 || event.y < 0 || event.y > 1)
            throw ValidationError("pointer", "invalid time or normalized coordinates");
    }
    std::stable_sort(ordered.begin(), ordered.end(), [](const auto& a, const auto& b) { return a.ticks < b.ticks; });
    std::vector<PointerGroup> result;
    for (const auto& segment : segments_) {
        PointerGroup group{segment.id, segment.output_start, segment.output_end(), {}};
        const auto lower = std::lower_bound(ordered.begin(), ordered.end(), segment.source_start,
            [](const auto& event, const auto time) { return event.ticks < time; });
        const auto upper = std::lower_bound(lower, ordered.end(), segment.source_end(),
            [](const auto& event, const auto time) { return event.ticks < time; });
        if ((lower == upper || lower->ticks != segment.source_start) && lower != ordered.begin()) {
            auto seed = *(lower - 1);
            seed.ticks = segment.output_start;
            seed.kind = PointerKind::move; // A seed is a position, never a synthetic click.
            group.events.push_back(std::move(seed));
        }
        for (auto it = lower; it != upper; ++it) {
            auto event = *it;
            const auto delta = multiply_ratio(event.ticks - segment.source_start, segment.rate.denominator, segment.rate.numerator);
            event.ticks = segment.output_start + (std::min)(delta, segment.output_duration - 1);
            group.events.push_back(std::move(event));
        }
        result.push_back(std::move(group));
    }
    return result;
}

std::optional<PointerEvent> sample_pointer(const PointerGroup& group, std::int64_t time) {
    if (time < group.output_start || time >= group.output_end || group.events.empty() || time < group.events.front().ticks)
        return std::nullopt;
    const auto upper = std::upper_bound(group.events.begin(), group.events.end(), time,
        [](const auto t, const auto& event) { return t < event.ticks; });
    auto result = *(upper - 1);
    result.ticks = time;
    result.kind = PointerKind::move;
    if (upper != group.events.end() && upper->ticks > (upper - 1)->ticks) {
        const auto fraction = static_cast<double>(time - (upper - 1)->ticks) / static_cast<double>(upper->ticks - (upper - 1)->ticks);
        result.x += (upper->x - result.x) * fraction;
        result.y += (upper->y - result.y) * fraction;
    }
    return result;
}
} // namespace frametrail::core
