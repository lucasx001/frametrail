#pragma once
#include "frametrail/core/scene.hpp"
#include <d2d1_1.h>
#include <dwrite.h>
#include <wrl/client.h>
#include <nlohmann/json.hpp>
#include <cstdint>
#include <string>
#include <string_view>

namespace frametrail::media {

struct SubtitleStyle {
    std::string preset{"classicOutline"};
    std::string font_family{"Alibaba PuHuiTi 3.0"};
    double font_size{44.0};
    std::string text_color{"#FFFFFF"};
    std::string outline_color{"#09090B"};
    double outline_width{3.0};
    bool shadow_enabled{true};
    double shadow_blur{6.0};
    double shadow_opacity{0.65};
    std::string background_color{"#09090B"};
    double background_opacity{0.0};
    double background_radius{12.0};
    std::string highlight_color{"#FACC15"};
    std::string highlight_mode{"none"};
    double position_x{0.5};
    double position_y{0.88};
    std::string alignment{"center"};
    double max_width_ratio{0.84};
    int max_chars_per_line{18};
    bool bilingual_enabled{false};
    double secondary_scale{0.72};
    double fade_seconds{0.12};
};

struct SubtitleLayoutResult {
    Microsoft::WRL::ComPtr<IDWriteTextLayout> primary_layout;
    Microsoft::WRL::ComPtr<IDWriteTextLayout> secondary_layout;
    std::wstring primary_wide;
    std::wstring secondary_wide;
    float origin_x{};
    float primary_y{};
    float secondary_y{};
    float layout_width{};
    float scale_ref{1.0f};
    float font_size_px{44.0f};
    D2D1_RECT_F box_px{};
    core::Rect normalized_rect{0.25, 0.84, 0.5, 0.08};
};

SubtitleStyle parse_subtitle_style(const nlohmann::json& editor_settings);
SubtitleStyle resolve_cue_style(const SubtitleStyle& base, const core::SubtitleCueStyleOverride& ov);
D2D1_COLOR_F parse_hex_color(std::string_view hex, float alpha = 1.0f);

SubtitleLayoutResult layout_subtitle(
    IDWriteFactory* text_factory,
    const core::VisualEffect& cue,
    const SubtitleStyle& style,
    uint32_t canvas_width,
    uint32_t canvas_height,
    float stack_offset_y = 0.0f);

// Shared measurement used by BOTH Renderer::subtitles and edit_geometry.hpp so
// canvas selection bounds match the rendered subtitle bounds 1:1.
// If text_factory is null, creates a lightweight shared IDWriteFactory.
core::Rect measure_subtitle_rect(
    IDWriteFactory* text_factory,
    const core::VisualEffect& cue,
    const SubtitleStyle& style,
    uint32_t canvas_width,
    uint32_t canvas_height,
    float stack_offset_y = 0.0f);

void draw_subtitles(
    ID2D1Factory1* factory,
    ID2D1DeviceContext* d2,
    IDWriteFactory* text_factory,
    const nlohmann::json& editor_settings,
    const core::FrameScene& scene);

} // namespace frametrail::media
