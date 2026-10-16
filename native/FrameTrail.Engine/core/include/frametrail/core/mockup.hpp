#pragma once
#include <algorithm>
#include <string_view>

namespace frametrail::core {
struct FrameDecoration {
    double top{}, inset{}, scale{}, outer_radius{}, content_radius{};
    bool device{}, browser{}, dark{};
};
// DogSC ScreenFrameGeometry, canonical 1080-point short edge (MIT).
inline FrameDecoration frame_decoration(std::string_view style, double frame_scale, double toolbar_scale,
    double outer_radius, double content_radius, double canvas_short, double width, double height) {
    FrameDecoration f{};
    if (style == "none") return f;
    f.device = style == "device" || style == "devicePhone";
    f.browser = style == "browser" || style == "browserLight" || style == "browserDark";
    f.dark = f.device || style == "window" || style == "browser" || style == "windowDark" || style == "browserDark";
    auto canonical = std::max(canvas_short, 1.0) / 1080.0;
    f.scale = canonical * std::clamp(frame_scale, 0.6, 1.6);
    auto short_edge = std::max(std::min(width, height), 1.0);
    f.inset = f.device ? std::clamp(short_edge * 0.026 * frame_scale, 2 * canonical, short_edge * 0.07) : 0;
    f.top = f.device ? f.inset : std::clamp(46 * f.scale * std::clamp(toolbar_scale, 0.65, 1.6), 4 * canonical, std::max(height * 0.25, 4 * canonical));
    auto authored_radius = outer_radius >= 0 ? outer_radius : f.device ? 64.0 : f.browser ? 22.0 : 18.0;
    f.outer_radius = std::min(authored_radius * f.scale, std::min(width + f.inset * 2, height + f.top + f.inset) / 2);
    f.content_radius = std::min(content_radius * f.scale, short_edge / 2);
    return f;
}
}
