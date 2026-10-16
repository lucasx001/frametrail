#pragma once
#include "project.hpp"
#include <array>

namespace frametrail::core {
// DogSC MotionStyle: sqrt(210 / 2.4) / (2*pi), 42 / (2*sqrt(210*2.4)).
inline constexpr double default_spring_frequency = 1.4887581711534412;
inline constexpr double default_spring_damping = 0.9354143466934853;
struct FocusPoint { int64_t ticks{}; double x{0.5}, y{0.5}, from_x{0.5}, from_y{0.5}; };
struct VisualEffect {
    std::string id, kind, mode, path, text, attachment{"screen"}, target{"screen"}, easing{"cubic"};
    int64_t start{}, duration{};
    Rect rect{0.25, 0.25, 0.5, 0.5};
    double scale{1.6}, focus_x{0.5}, focus_y{0.5}, enter{0.7}, exit{0.7};
    double strength{16}, opacity{1}, angle_x{}, angle_y{}, rotation{}, frequency{default_spring_frequency}, damping{default_spring_damping};
    bool outside_hidden{};
    std::vector<FocusPoint> focus_points;
};
struct PlaneMotion {
    double scale{1}, focus_x{0.5}, focus_y{0.5}, angle_x{}, angle_y{}, opacity{1}, reveal{1}, offset_x{}, offset_y{};
    bool fixed_anchor{};
};
struct VisualScene {
    PlaneMotion screen, camera;
    std::string camera_shape{"rounded"}, frame_style{"none"};
    std::vector<VisualEffect> regions, stickers;
};
void validate_visual_parameters(const Effect& effect);
VisualEffect parse_visual_effect(const Effect& effect);
double motion_curve(double progress, std::string_view easing, double frequency = default_spring_frequency, double damping = default_spring_damping);
double effect_envelope(const VisualEffect& effect, int64_t source_ticks);
VisualScene evaluate_visuals(const std::vector<VisualEffect>& effects, int64_t source_ticks);
void generate_auto_zoom(Project& project, std::string_view command);
std::array<double, 2> smooth_pointer(const std::vector<FocusPoint>& points, int64_t ticks, double smoothing_seconds);
} // namespace frametrail::core
