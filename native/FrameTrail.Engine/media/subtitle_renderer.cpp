#include "subtitle_renderer.hpp"
#include "internal.hpp"
#include <d2d1_1helper.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cwctype>
#include <vector>

namespace frametrail::media {
namespace {

int hex_nibble(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
    if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
    return 0;
}

std::wstring utf8_to_wide(std::string_view utf8) {
    if (utf8.empty()) return {};
    const int needed = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
    if (needed <= 0) return {};
    std::wstring result(static_cast<size_t>(needed), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), result.data(), needed);
    return result;
}

std::wstring trim_ws(std::wstring_view value) {
    size_t start = 0;
    while (start < value.size() && std::iswspace(value[start])) ++start;
    size_t end = value.size();
    while (end > start && std::iswspace(value[end - 1])) --end;
    return std::wstring(value.substr(start, end - start));
}

DWRITE_TEXT_ALIGNMENT parse_alignment(std::string_view alignment) {
    if (alignment == "left") return DWRITE_TEXT_ALIGNMENT_LEADING;
    if (alignment == "right") return DWRITE_TEXT_ALIGNMENT_TRAILING;
    return DWRITE_TEXT_ALIGNMENT_CENTER;
}

ComPtr<IDWriteFactory> ensure_write_factory(IDWriteFactory* text_factory) {
    if (text_factory) {
        ComPtr<IDWriteFactory> existing(text_factory);
        return existing;
    }
    ComPtr<IDWriteFactory> created;
    FT_MEDIA_CHECK(DWriteCreateFactory(
        DWRITE_FACTORY_TYPE_SHARED,
        __uuidof(IDWriteFactory),
        reinterpret_cast<IUnknown**>(created.GetAddressOf())));
    return created;
}

struct HighlightMarker final : IUnknown {
    std::atomic<ULONG> refs{1};
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown)) {
            *ppv = static_cast<IUnknown*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override {
        const ULONG remaining = --refs;
        if (remaining == 0) delete this;
        return remaining;
    }
};

class SubtitleOutlineRenderer final : public IDWriteTextRenderer {
public:
    SubtitleOutlineRenderer(
        ID2D1Factory1* factory,
        ID2D1DeviceContext* d2,
        ID2D1SolidColorBrush* text_brush,
        ID2D1SolidColorBrush* highlight_brush,
        ID2D1SolidColorBrush* outline_brush,
        ID2D1SolidColorBrush* shadow_brush,
        ID2D1StrokeStyle* round_stroke,
        IUnknown* highlight_marker,
        float outline_width,
        bool shadow_enabled,
        float shadow_blur,
        float shadow_opacity,
        float cue_alpha)
        : factory_(factory),
          d2_(d2),
          text_brush_(text_brush),
          highlight_brush_(highlight_brush),
          outline_brush_(outline_brush),
          shadow_brush_(shadow_brush),
          round_stroke_(round_stroke),
          highlight_marker_(highlight_marker),
          outline_width_(outline_width),
          shadow_enabled_(shadow_enabled),
          shadow_blur_(shadow_blur),
          shadow_opacity_(shadow_opacity),
          cue_alpha_(cue_alpha) {}

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IDWritePixelSnapping) || riid == __uuidof(IDWriteTextRenderer)) {
            *ppv = static_cast<IDWriteTextRenderer*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }

    ULONG STDMETHODCALLTYPE Release() override {
        const ULONG remaining = --refs_;
        if (remaining == 0) delete this;
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE IsPixelSnappingDisabled(void*, BOOL* is_disabled) override {
        if (!is_disabled) return E_POINTER;
        *is_disabled = TRUE;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetCurrentTransform(void*, DWRITE_MATRIX* transform) override {
        if (!transform) return E_POINTER;
        D2D1_MATRIX_3X2_F matrix{};
        d2_->GetTransform(&matrix);
        transform->m11 = matrix._11;
        transform->m12 = matrix._12;
        transform->m21 = matrix._21;
        transform->m22 = matrix._22;
        transform->dx = matrix._31;
        transform->dy = matrix._32;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetPixelsPerDip(void*, FLOAT* pixels_per_dip) override {
        if (!pixels_per_dip) return E_POINTER;
        *pixels_per_dip = 1.0f;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE DrawGlyphRun(
        void*,
        FLOAT baseline_x,
        FLOAT baseline_y,
        DWRITE_MEASURING_MODE measuring_mode,
        DWRITE_GLYPH_RUN const* glyph_run,
        DWRITE_GLYPH_RUN_DESCRIPTION const*,
        IUnknown* client_drawing_effect) override {
        if (!glyph_run || glyph_run->glyphCount == 0 || !glyph_run->fontFace) return S_OK;

        const bool highlighted = highlight_marker_ != nullptr && client_drawing_effect == highlight_marker_;
        ID2D1SolidColorBrush* fill_brush = (highlighted && highlight_brush_) ? highlight_brush_ : text_brush_;

        ComPtr<ID2D1PathGeometry> path;
        HRESULT hr = factory_->CreatePathGeometry(&path);
        if (FAILED(hr)) return hr;

        ComPtr<ID2D1GeometrySink> sink;
        hr = path->Open(&sink);
        if (FAILED(hr)) return hr;

        hr = glyph_run->fontFace->GetGlyphRunOutline(
            glyph_run->fontEmSize,
            glyph_run->glyphIndices,
            glyph_run->glyphAdvances,
            glyph_run->glyphOffsets,
            glyph_run->glyphCount,
            glyph_run->isSideways,
            (glyph_run->bidiLevel % 2) != 0,
            sink.Get());
        if (FAILED(hr)) {
            sink->Close();
            d2_->DrawGlyphRun(D2D1::Point2F(baseline_x, baseline_y), glyph_run, fill_brush, measuring_mode);
            return S_OK;
        }

        hr = sink->Close();
        if (FAILED(hr)) return hr;

        D2D1_MATRIX_3X2_F saved{};
        d2_->GetTransform(&saved);

        if (shadow_enabled_ && shadow_opacity_ > 0.001f && shadow_brush_) {
            const float offset_y = std::max(1.5f, shadow_blur_ * 0.42f);
            const float base_stroke = outline_width_ * 2.0f;
            d2_->SetTransform(D2D1::Matrix3x2F::Translation(baseline_x, baseline_y + offset_y) * saved);

            if (shadow_blur_ > 0.5f) {
                shadow_brush_->SetOpacity(shadow_opacity_ * cue_alpha_ * 0.24f);
                d2_->DrawGeometry(path.Get(), shadow_brush_, base_stroke + shadow_blur_ * 1.4f, round_stroke_);
                shadow_brush_->SetOpacity(shadow_opacity_ * cue_alpha_ * 0.42f);
                d2_->DrawGeometry(path.Get(), shadow_brush_, base_stroke + shadow_blur_ * 0.65f, round_stroke_);
            }
            shadow_brush_->SetOpacity(shadow_opacity_ * cue_alpha_ * 0.72f);
            if (base_stroke > 0.01f) {
                d2_->DrawGeometry(path.Get(), shadow_brush_, base_stroke, round_stroke_);
            }
            d2_->FillGeometry(path.Get(), shadow_brush_);
        }

        d2_->SetTransform(D2D1::Matrix3x2F::Translation(baseline_x, baseline_y) * saved);

        if (outline_width_ > 0.01f && outline_brush_) {
            d2_->DrawGeometry(path.Get(), outline_brush_, outline_width_ * 2.0f, round_stroke_);
        }

        d2_->FillGeometry(path.Get(), fill_brush);
        d2_->SetTransform(saved);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE DrawUnderline(void*, FLOAT, FLOAT, DWRITE_UNDERLINE const*, IUnknown*) override {
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE DrawStrikethrough(void*, FLOAT, FLOAT, DWRITE_STRIKETHROUGH const*, IUnknown*) override {
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE DrawInlineObject(void*, FLOAT, FLOAT, IDWriteInlineObject*, BOOL, BOOL, IUnknown*) override {
        return S_OK;
    }

private:
    std::atomic<ULONG> refs_{1};
    ID2D1Factory1* factory_;
    ID2D1DeviceContext* d2_;
    ID2D1SolidColorBrush* text_brush_;
    ID2D1SolidColorBrush* highlight_brush_;
    ID2D1SolidColorBrush* outline_brush_;
    ID2D1SolidColorBrush* shadow_brush_;
    ID2D1StrokeStyle* round_stroke_;
    IUnknown* highlight_marker_;
    float outline_width_;
    bool shadow_enabled_;
    float shadow_blur_;
    float shadow_opacity_;
    float cue_alpha_;
};

std::optional<DWRITE_TEXT_RANGE> find_active_word_range(
    const core::VisualEffect& cue,
    const std::wstring& primary_wide,
    double cue_offset_seconds) {
    if (cue.words.empty() || primary_wide.empty()) return std::nullopt;

    size_t active_index = cue.words.size();
    for (size_t i = 0; i < cue.words.size(); ++i) {
        const auto& w = cue.words[i];
        const bool is_last = (i + 1 == cue.words.size());
        if (cue_offset_seconds >= w.start_offset &&
            (cue_offset_seconds < w.end_offset || (is_last && cue_offset_seconds <= w.end_offset + 0.001))) {
            active_index = i;
            break;
        }
    }
    if (active_index >= cue.words.size()) return std::nullopt;

    size_t cursor = 0;
    for (size_t i = 0; i <= active_index; ++i) {
        const auto needle = trim_ws(utf8_to_wide(cue.words[i].text));
        if (needle.empty()) continue;

        size_t pos = primary_wide.find(needle, cursor);
        if (pos == std::wstring::npos) {
            // Case-insensitive fallback for ASCII words
            std::wstring lower_haystack = primary_wide;
            std::wstring lower_needle = needle;
            std::transform(lower_haystack.begin(), lower_haystack.end(), lower_haystack.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
            std::transform(lower_needle.begin(), lower_needle.end(), lower_needle.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
            pos = lower_haystack.find(lower_needle, cursor);
        }
        if (pos == std::wstring::npos) {
            if (i == active_index) return std::nullopt;
            continue;
        }
        if (i == active_index) {
            return DWRITE_TEXT_RANGE{static_cast<UINT32>(pos), static_cast<UINT32>(needle.size())};
        }
        cursor = pos + needle.size();
    }
    return std::nullopt;
}

} // namespace

SubtitleStyle parse_subtitle_style(const nlohmann::json& editor_settings) {
    SubtitleStyle s;
    if (!editor_settings.is_object() || !editor_settings.contains("subtitleStyle") || !editor_settings["subtitleStyle"].is_object())
        return s;
    const auto& ss = editor_settings["subtitleStyle"];
    auto str = [&](const char* key, std::string& out) {
        if (ss.contains(key) && ss[key].is_string()) out = ss[key].get<std::string>();
    };
    auto dbl = [&](const char* key, double& out, double lo, double hi) {
        if (ss.contains(key) && ss[key].is_number()) out = std::clamp(ss[key].get<double>(), lo, hi);
    };
    auto flag = [&](const char* key, bool& out) {
        if (ss.contains(key) && ss[key].is_boolean()) out = ss[key].get<bool>();
    };
    str("preset", s.preset);
    str("fontFamily", s.font_family);
    dbl("fontSize", s.font_size, 12.0, 160.0);
    str("textColor", s.text_color);
    str("outlineColor", s.outline_color);
    dbl("outlineWidth", s.outline_width, 0.0, 16.0);
    flag("shadowEnabled", s.shadow_enabled);
    dbl("shadowBlur", s.shadow_blur, 0.0, 32.0);
    dbl("shadowOpacity", s.shadow_opacity, 0.0, 1.0);
    str("backgroundColor", s.background_color);
    dbl("backgroundOpacity", s.background_opacity, 0.0, 1.0);
    dbl("backgroundRadius", s.background_radius, 0.0, 48.0);
    str("highlightColor", s.highlight_color);
    str("highlightMode", s.highlight_mode);
    dbl("positionX", s.position_x, 0.05, 0.95);
    dbl("positionY", s.position_y, 0.05, 0.96);
    str("alignment", s.alignment);
    dbl("maxWidthRatio", s.max_width_ratio, 0.3, 0.96);
    if (ss.contains("maxCharsPerLine") && ss["maxCharsPerLine"].is_number_integer())
        s.max_chars_per_line = std::clamp(ss["maxCharsPerLine"].get<int>(), 6, 80);
    flag("bilingualEnabled", s.bilingual_enabled);
    dbl("secondaryScale", s.secondary_scale, 0.4, 1.2);
    dbl("fadeSeconds", s.fade_seconds, 0.0, 1.5);
    return s;
}

SubtitleStyle resolve_cue_style(const SubtitleStyle& base, const core::SubtitleCueStyleOverride& ov) {
    SubtitleStyle s = base;
    if (ov.position_x) s.position_x = std::clamp(*ov.position_x, 0.05, 0.95);
    if (ov.position_y) s.position_y = std::clamp(*ov.position_y, 0.05, 0.96);
    if (ov.font_size) s.font_size = std::clamp(*ov.font_size, 12.0, 160.0);
    if (ov.text_color) s.text_color = *ov.text_color;
    if (ov.outline_color) s.outline_color = *ov.outline_color;
    if (ov.background_color) s.background_color = *ov.background_color;
    if (ov.highlight_color) s.highlight_color = *ov.highlight_color;
    if (ov.alignment) s.alignment = *ov.alignment;
    return s;
}

D2D1_COLOR_F parse_hex_color(std::string_view hex, float alpha) {
    if (hex.size() == 7 && hex[0] == '#') {
        const float r = static_cast<float>((hex_nibble(hex[1]) << 4) | hex_nibble(hex[2])) / 255.0f;
        const float g = static_cast<float>((hex_nibble(hex[3]) << 4) | hex_nibble(hex[4])) / 255.0f;
        const float b = static_cast<float>((hex_nibble(hex[5]) << 4) | hex_nibble(hex[6])) / 255.0f;
        return D2D1::ColorF(r, g, b, std::clamp(alpha, 0.0f, 1.0f));
    }
    return D2D1::ColorF(1.0f, 1.0f, 1.0f, std::clamp(alpha, 0.0f, 1.0f));
}

SubtitleLayoutResult layout_subtitle(
    IDWriteFactory* text_factory,
    const core::VisualEffect& cue,
    const SubtitleStyle& style,
    uint32_t canvas_width,
    uint32_t canvas_height,
    float stack_offset_y) {
    SubtitleLayoutResult out;
    auto factory = ensure_write_factory(text_factory);

    const float W = static_cast<float>(std::max<uint32_t>(1, canvas_width));
    const float H = static_cast<float>(std::max<uint32_t>(1, canvas_height));
    const float scale_ref = static_cast<float>(std::min(canvas_width, canvas_height)) / 1080.0f;
    const float font_size_px = std::max(8.0f, static_cast<float>(style.font_size) * scale_ref);
    const float max_width = std::max(64.0f, static_cast<float>(style.max_width_ratio) * W);

    out.scale_ref = scale_ref;
    out.font_size_px = font_size_px;
    out.layout_width = max_width;
    out.primary_wide = utf8_to_wide(cue.text);
    const std::wstring font_wide = utf8_to_wide(style.font_family.empty() ? "Alibaba PuHuiTi 3.0" : style.font_family);
    const DWRITE_TEXT_ALIGNMENT align = parse_alignment(style.alignment);

    if (out.primary_wide.empty()) {
        const float half_w = std::min(W * 0.12f, font_size_px * 2.0f);
        const float half_h = font_size_px * 0.7f;
        const float cx = static_cast<float>(style.position_x) * W;
        const float cy = static_cast<float>(style.position_y) * H - stack_offset_y;
        out.box_px = D2D1::RectF(
            std::clamp(cx - half_w, 0.0f, W),
            std::clamp(cy - half_h, 0.0f, H),
            std::clamp(cx + half_w, 0.0f, W),
            std::clamp(cy + half_h, 0.0f, H));
        out.normalized_rect = {
            out.box_px.left / W,
            out.box_px.top / H,
            std::max(0.001, static_cast<double>(out.box_px.right - out.box_px.left) / W),
            std::max(0.001, static_cast<double>(out.box_px.bottom - out.box_px.top) / H)
        };
        return out;
    }

    ComPtr<IDWriteTextFormat> primary_format;
    FT_MEDIA_CHECK(factory->CreateTextFormat(
        font_wide.c_str(),
        nullptr,
        DWRITE_FONT_WEIGHT_BOLD,
        DWRITE_FONT_STYLE_NORMAL,
        DWRITE_FONT_STRETCH_NORMAL,
        font_size_px,
        L"zh-CN",
        &primary_format));
    FT_MEDIA_CHECK(primary_format->SetTextAlignment(align));
    FT_MEDIA_CHECK(primary_format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR));
    FT_MEDIA_CHECK(primary_format->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP));

    FT_MEDIA_CHECK(factory->CreateTextLayout(
        out.primary_wide.c_str(),
        static_cast<UINT32>(out.primary_wide.size()),
        primary_format.Get(),
        max_width,
        H,
        &out.primary_layout));

    DWRITE_TEXT_METRICS primary_metrics{};
    FT_MEDIA_CHECK(out.primary_layout->GetMetrics(&primary_metrics));

    float content_left = primary_metrics.left;
    float content_right = primary_metrics.left + primary_metrics.width;
    float content_height = primary_metrics.height;
    const float line_gap = font_size_px * 0.16f;

    if (style.bilingual_enabled && !cue.secondary_text.empty()) {
        out.secondary_wide = utf8_to_wide(cue.secondary_text);
        if (!out.secondary_wide.empty()) {
            const float secondary_size_px = std::max(8.0f, font_size_px * static_cast<float>(style.secondary_scale));
            ComPtr<IDWriteTextFormat> secondary_format;
            FT_MEDIA_CHECK(factory->CreateTextFormat(
                font_wide.c_str(),
                nullptr,
                DWRITE_FONT_WEIGHT_SEMI_BOLD,
                DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH_NORMAL,
                secondary_size_px,
                L"zh-CN",
                &secondary_format));
            FT_MEDIA_CHECK(secondary_format->SetTextAlignment(align));
            FT_MEDIA_CHECK(secondary_format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR));
            FT_MEDIA_CHECK(secondary_format->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP));

            FT_MEDIA_CHECK(factory->CreateTextLayout(
                out.secondary_wide.c_str(),
                static_cast<UINT32>(out.secondary_wide.size()),
                secondary_format.Get(),
                max_width,
                H,
                &out.secondary_layout));

            DWRITE_TEXT_METRICS sec_metrics{};
            FT_MEDIA_CHECK(out.secondary_layout->GetMetrics(&sec_metrics));
            content_left = std::min(content_left, sec_metrics.left);
            content_right = std::max(content_right, sec_metrics.left + sec_metrics.width);
            content_height += line_gap + sec_metrics.height;
        }
    }

    const float pad_x = std::max(8.0f * scale_ref, font_size_px * 0.36f);
    const float pad_y = std::max(6.0f * scale_ref, font_size_px * 0.22f);
    const float outline_margin = static_cast<float>(style.outline_width) * scale_ref;

    const float anchor_x = static_cast<float>(style.position_x) * W;
    const float anchor_y = static_cast<float>(style.position_y) * H - stack_offset_y;
    const float content_center_local_x = (content_left + content_right) * 0.5f;

    float origin_x = anchor_x - content_center_local_x;
    float primary_y = anchor_y - content_height * 0.5f;

    float box_left = origin_x + content_left - pad_x - outline_margin;
    float box_right = origin_x + content_right + pad_x + outline_margin;
    float box_top = primary_y - pad_y - outline_margin;
    float box_bottom = primary_y + content_height + pad_y + outline_margin;

    // Keep box inside canvas bounds without distorting width/height.
    if (box_left < 0.0f) {
        const float shift = -box_left;
        origin_x += shift;
        box_left += shift;
        box_right += shift;
    } else if (box_right > W) {
        const float shift = W - box_right;
        origin_x += shift;
        box_left += shift;
        box_right += shift;
    }
    if (box_top < 0.0f) {
        const float shift = -box_top;
        primary_y += shift;
        box_top += shift;
        box_bottom += shift;
    } else if (box_bottom > H) {
        const float shift = H - box_bottom;
        primary_y += shift;
        box_top += shift;
        box_bottom += shift;
    }

    out.origin_x = origin_x;
    out.primary_y = primary_y;
    out.secondary_y = primary_y + primary_metrics.height + line_gap;
    out.box_px = D2D1::RectF(
        std::clamp(box_left, 0.0f, W),
        std::clamp(box_top, 0.0f, H),
        std::clamp(box_right, 0.0f, W),
        std::clamp(box_bottom, 0.0f, H));
    out.normalized_rect = {
        static_cast<double>(out.box_px.left) / W,
        static_cast<double>(out.box_px.top) / H,
        std::max(0.001, static_cast<double>(out.box_px.right - out.box_px.left) / W),
        std::max(0.001, static_cast<double>(out.box_px.bottom - out.box_px.top) / H)
    };
    return out;
}

core::Rect measure_subtitle_rect(
    IDWriteFactory* text_factory,
    const core::VisualEffect& cue,
    const SubtitleStyle& style,
    uint32_t canvas_width,
    uint32_t canvas_height,
    float stack_offset_y) {
    return layout_subtitle(text_factory, cue, style, canvas_width, canvas_height, stack_offset_y).normalized_rect;
}

void draw_subtitles(
    ID2D1Factory1* factory,
    ID2D1DeviceContext* d2,
    IDWriteFactory* text_factory,
    const nlohmann::json& editor_settings,
    const core::FrameScene& scene) {
    if (scene.visual.subtitles.empty() || !factory || !d2 || !text_factory) return;

    const SubtitleStyle base_style = parse_subtitle_style(editor_settings);

    D2D1_STROKE_STYLE_PROPERTIES stroke_props = D2D1::StrokeStyleProperties(
        D2D1_CAP_STYLE_ROUND,
        D2D1_CAP_STYLE_ROUND,
        D2D1_CAP_STYLE_ROUND,
        D2D1_LINE_JOIN_ROUND,
        2.0f,
        D2D1_DASH_STYLE_SOLID,
        0.0f);
    ComPtr<ID2D1StrokeStyle> round_stroke;
    FT_MEDIA_CHECK(factory->CreateStrokeStyle(stroke_props, nullptr, 0, &round_stroke));

    ComPtr<HighlightMarker> highlight_marker;
    highlight_marker.Attach(new HighlightMarker());

    float stack_offset_y = 0.0f;
    for (const auto& cue : scene.visual.subtitles) {
        const float cue_alpha = static_cast<float>(std::clamp(cue.opacity, 0.0, 1.0));
        if (cue_alpha <= 0.001f || cue.text.empty()) continue;

        const SubtitleStyle style = resolve_cue_style(base_style, cue.subtitle_override);
        const SubtitleLayoutResult laid_out = layout_subtitle(
            text_factory, cue, style, scene.width, scene.height, stack_offset_y);
        if (!laid_out.primary_layout) continue;

        const float scale_ref = laid_out.scale_ref;
        const float outline_px = static_cast<float>(style.outline_width) * scale_ref;
        const float shadow_blur_px = static_cast<float>(style.shadow_blur) * scale_ref;
        const float bg_radius_px = static_cast<float>(style.background_radius) * scale_ref;

        // 1. Rounded pill background behind the full subtitle block
        if (style.background_opacity > 0.001) {
            ComPtr<ID2D1SolidColorBrush> bg_brush;
            FT_MEDIA_CHECK(d2->CreateSolidColorBrush(
                parse_hex_color(style.background_color, static_cast<float>(style.background_opacity) * cue_alpha),
                &bg_brush));
            const float max_r = std::min(laid_out.box_px.right - laid_out.box_px.left, laid_out.box_px.bottom - laid_out.box_px.top) * 0.5f;
            const float radius = std::min(bg_radius_px, max_r);
            d2->FillRoundedRectangle(D2D1::RoundedRect(laid_out.box_px, radius, radius), bg_brush.Get());
        }

        // 2. Word-level karaoke highlight detection
        const double cue_offset_sec = static_cast<double>(scene.source_ticks - cue.start) / static_cast<double>(core::time_scale);
        std::optional<DWRITE_TEXT_RANGE> active_range;
        if (style.highlight_mode == "wordColor" || style.highlight_mode == "wordPill") {
            active_range = find_active_word_range(cue, laid_out.primary_wide, cue_offset_sec);
        }

        ComPtr<ID2D1SolidColorBrush> text_brush;
        ComPtr<ID2D1SolidColorBrush> highlight_brush;
        ComPtr<ID2D1SolidColorBrush> outline_brush;
        ComPtr<ID2D1SolidColorBrush> shadow_brush;
        FT_MEDIA_CHECK(d2->CreateSolidColorBrush(parse_hex_color(style.text_color, cue_alpha), &text_brush));
        FT_MEDIA_CHECK(d2->CreateSolidColorBrush(parse_hex_color(style.highlight_color, cue_alpha), &highlight_brush));
        FT_MEDIA_CHECK(d2->CreateSolidColorBrush(parse_hex_color(style.outline_color, cue_alpha), &outline_brush));
        FT_MEDIA_CHECK(d2->CreateSolidColorBrush(D2D1::ColorF(0.0f, 0.0f, 0.0f, 1.0f), &shadow_brush));

        if (active_range && style.highlight_mode == "wordPill") {
            UINT32 hit_count = 0;
            HRESULT hr = laid_out.primary_layout->HitTestTextRange(
                active_range->startPosition,
                active_range->length,
                laid_out.origin_x,
                laid_out.primary_y,
                nullptr,
                0,
                &hit_count);
            if ((hr == E_NOT_SUFFICIENT_BUFFER || SUCCEEDED(hr)) && hit_count > 0) {
                std::vector<DWRITE_HIT_TEST_METRICS> hits(hit_count);
                if (SUCCEEDED(laid_out.primary_layout->HitTestTextRange(
                        active_range->startPosition,
                        active_range->length,
                        laid_out.origin_x,
                        laid_out.primary_y,
                        hits.data(),
                        hit_count,
                        &hit_count))) {
                    const float word_pad_x = laid_out.font_size_px * 0.14f;
                    const float word_pad_y = laid_out.font_size_px * 0.06f;
                    for (UINT32 i = 0; i < hit_count; ++i) {
                        const auto& m = hits[i];
                        const D2D1_RECT_F pill = D2D1::RectF(
                            m.left - word_pad_x,
                            m.top - word_pad_y,
                            m.left + m.width + word_pad_x,
                            m.top + m.height + word_pad_y);
                        const float pill_r = std::min((pill.bottom - pill.top) * 0.32f, std::max(6.0f * scale_ref, bg_radius_px * 0.75f));
                        d2->FillRoundedRectangle(D2D1::RoundedRect(pill, pill_r, pill_r), highlight_brush.Get());
                    }
                }
            }
        } else if (active_range && style.highlight_mode == "wordColor") {
            FT_MEDIA_CHECK(laid_out.primary_layout->SetDrawingEffect(highlight_marker.Get(), *active_range));
        }

        // 3. Draw primary text with vector outline + drop shadow + fill
        ComPtr<SubtitleOutlineRenderer> primary_renderer;
        primary_renderer.Attach(new SubtitleOutlineRenderer(
            factory,
            d2,
            text_brush.Get(),
            highlight_brush.Get(),
            outline_brush.Get(),
            shadow_brush.Get(),
            round_stroke.Get(),
            highlight_marker.Get(),
            outline_px,
            style.shadow_enabled,
            shadow_blur_px,
            static_cast<float>(style.shadow_opacity),
            cue_alpha));
        FT_MEDIA_CHECK(laid_out.primary_layout->Draw(nullptr, primary_renderer.Get(), laid_out.origin_x, laid_out.primary_y));

        // 4. Draw bilingual secondary line if enabled
        if (laid_out.secondary_layout) {
            ComPtr<ID2D1SolidColorBrush> sec_text_brush;
            FT_MEDIA_CHECK(d2->CreateSolidColorBrush(parse_hex_color(style.text_color, cue_alpha * 0.92f), &sec_text_brush));
            ComPtr<SubtitleOutlineRenderer> secondary_renderer;
            secondary_renderer.Attach(new SubtitleOutlineRenderer(
                factory,
                d2,
                sec_text_brush.Get(),
                nullptr,
                outline_brush.Get(),
                shadow_brush.Get(),
                round_stroke.Get(),
                nullptr,
                outline_px * static_cast<float>(style.secondary_scale),
                style.shadow_enabled,
                shadow_blur_px * static_cast<float>(style.secondary_scale),
                static_cast<float>(style.shadow_opacity),
                cue_alpha * 0.92f));
            FT_MEDIA_CHECK(laid_out.secondary_layout->Draw(nullptr, secondary_renderer.Get(), laid_out.origin_x, laid_out.secondary_y));
        }

        // Stack any simultaneous overlapping cue cleanly above this one.
        stack_offset_y += (laid_out.box_px.bottom - laid_out.box_px.top) + 12.0f * scale_ref;
    }
}

} // namespace frametrail::media
