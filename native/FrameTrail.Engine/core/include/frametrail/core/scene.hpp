#pragma once
#include "timeline.hpp"
#include "effects.hpp"

namespace frametrail::core {
struct MediaFrame {
    std::string media_id;
    std::int64_t local_ticks{};
    Rect output_rect; // physical output pixels, fitted with source aspect ratio
    bool mirror{};
};
struct FrameScene {
    std::int64_t revision{}, output_ticks{}, source_ticks{};
    std::string segment_id;
    std::uint32_t width{}, height{};
    Color background;
    MediaFrame screen;
    std::optional<MediaFrame> camera;
    VisualScene visual;
};

// Returns nullopt outside the half-open output range. Immutable project input;
// validates once at construction and reuses a single authoritative TimelineMap.
class FrameSceneEvaluator final {
public:
    explicit FrameSceneEvaluator(Project project);
    const TimelineMap& timeline() const noexcept { return timeline_; }
    std::optional<FrameScene> evaluate(std::int64_t output_ticks) const;
private:
    Project project_;
    TimelineMap timeline_;
    double crop_width_{1}, crop_height_{1};
    std::vector<VisualEffect> effects_;
    std::string frame_style_, camera_shape_;
};
} // namespace frametrail::core
