#pragma once

#include <cstdint>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace frametrail::core {

inline constexpr std::int64_t time_scale = 10'000'000;
inline constexpr std::int64_t maximum_source_ticks = 7 * 24 * 60 * 60 * time_scale;
inline constexpr std::uint32_t schema_version = 2;

class ValidationError final : public std::runtime_error {
public:
    ValidationError(std::string path, std::string message);
    const std::string& path() const noexcept { return path_; }
private:
    std::string path_;
};

// The normalized representation is signed 100 ns ticks. Conversion uses nearest,
// ties away from zero; no float accumulation participates in timeline scheduling.
struct Time {
    std::int64_t ticks{};
    std::int32_t timescale{static_cast<std::int32_t>(time_scale)};
};
std::int64_t normalized_ticks(Time value);
Time canonical_time(std::int64_t ticks) noexcept;
std::int64_t multiply_ratio(std::int64_t value, std::int64_t numerator, std::int64_t denominator);
bool is_project_relative_path(std::string_view path);

struct Rational { std::int32_t numerator{1}; std::int32_t denominator{1}; };
struct Rect { double x{}, y{}, width{1}, height{1}; };
struct Color { double r{0.08}, g{0.08}, b{0.10}, a{1}; };
enum class MediaKind { screen, camera, system_audio, microphone, pointer_events, android_audio };
struct Media {
    std::string id;
    MediaKind kind{MediaKind::screen};
    std::string path;
    Time duration;
    Time start_offset;
    std::uint32_t width{}, height{}, sample_rate{}, channels{};
};
struct Segment {
    std::string id;
    Time source_start;
    Time source_duration;
    Rational rate;
};
struct CameraLayout { bool enabled{}; Rect rect{0.72, 0.68, 0.25, 0.28}; bool mirror{}; };
struct Canvas {
    std::uint32_t width{1920}, height{1080};
    double padding{0.05}; // fraction of shortest output dimension
    Color background;
    Rect screen;
    CameraLayout camera;
};
struct Effect {
    std::string id, kind;
    Time source_start, source_duration;
    // Versioned effect-specific JSON retained without dropping keys. Core v1
    // validates the envelope; later render tasks implement these parameters.
    std::string parameters_json{"{}"};
};
struct ExportSettings {
    std::uint32_t width{1920}, height{1080};
    Rational fps{30, 1};
    std::string video_codec{"h264"}, audio_codec{"aac"}, container{"mp4"};
};
struct RecordingMarker { std::string id; Time time; std::string label; };
struct Project {
    std::uint32_t schema_version{1};
    std::string project_id;
    std::int64_t revision{};
    Time source_duration;
    std::vector<Media> media;
    std::vector<Segment> segments;
    Canvas canvas;
    std::vector<Effect> effects;
    std::vector<RecordingMarker> markers;
    ExportSettings export_settings;
    // Optional schema-1 editor envelope. Old recordings default to neutral settings.
    std::string editor_json{"{}"};
    std::string primary_source_json;
};

Project parse_project(std::string_view json);
std::string serialize_project(const Project& project);
void validate_project(const Project& project);

} // namespace frametrail::core
