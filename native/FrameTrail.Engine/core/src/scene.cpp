#include "frametrail/core/scene.hpp"
#include "frametrail/core/mockup.hpp"
#include <algorithm>
#include <utility>
#include <nlohmann/json.hpp>

namespace frametrail::core {
namespace {
Rect fit(Rect region, double source_width, double source_height) {
    const auto scale = (std::min)(region.width / source_width, region.height / source_height);
    const auto width = source_width * scale;
    const auto height = source_height * scale;
    return {region.x + (region.width - width) / 2, region.y + (region.height - height) / 2, width, height};
}
Rect pixels(Rect normalized, const Canvas& canvas) {
    return {normalized.x * canvas.width, normalized.y * canvas.height, normalized.width * canvas.width, normalized.height * canvas.height};
}
const Media* media_at(const Project& project, MediaKind kind, std::int64_t ticks) {
    for (const auto& media : project.media) {
        if (media.kind != kind) continue;
        const auto start = normalized_ticks(media.start_offset);
        if (ticks >= start && ticks < start + normalized_ticks(media.duration)) return &media;
    }
    return nullptr;
}
} // namespace

FrameSceneEvaluator::FrameSceneEvaluator(Project project)
    : project_(std::move(project)), timeline_(project_.source_duration, project_.segments) {
    validate_project(project_);
    const auto settings = nlohmann::json::parse(project_.editor_json);
    const auto crop = settings.value("crop", nlohmann::json::object()); crop_width_ = crop.value("width", 1.0); crop_height_ = crop.value("height", 1.0);
    frame_style_=settings.value("frameStyle",std::string("none")); camera_shape_=settings.value("cameraShape",std::string("rounded"));
    frame_scale_=settings.value("frameScale",1.0); frame_toolbar_scale_=settings.value("frameToolbarScale",1.0);
    double subtitle_fade = 0.12;
    if (settings.contains("subtitleStyle") && settings["subtitleStyle"].is_object()) {
        const auto& ss = settings["subtitleStyle"];
        if (ss.contains("fadeSeconds") && ss["fadeSeconds"].is_number())
            subtitle_fade = std::clamp(ss["fadeSeconds"].get<double>(), 0.0, 1.5);
    }
    for (auto& e : project_.effects) {
        auto parsed = parse_visual_effect(e);
        if (parsed.kind == "subtitle" && !parsed.custom_fade) {
            parsed.enter = subtitle_fade;
            parsed.exit = subtitle_fade;
        }
        effects_.push_back(std::move(parsed));
    }
}

std::optional<FrameScene> FrameSceneEvaluator::evaluate(std::int64_t output_ticks) const {
    const auto mapped = timeline_.source_time(output_ticks);
    if (!mapped) return std::nullopt;
    const auto& canvas = project_.canvas;
    auto region = pixels(canvas.screen, canvas);
    const auto padding = canvas.padding * (std::min)(canvas.width, canvas.height);
    region.x += padding; region.y += padding; region.width -= 2 * padding; region.height -= 2 * padding;
    auto make_screen_frame = [&](std::int64_t source_ticks) -> MediaFrame {
        const auto* media = media_at(project_, MediaKind::screen, source_ticks);
        if (!media) throw ValidationError("media", "no screen frame covers mapped source instant");
        MediaFrame frame{media->id, source_ticks - normalized_ticks(media->start_offset),
            fit(region, media->width * crop_width_, media->height * crop_height_), false};
        if (frame_style_ != "none") {
            for (int iteration = 0; iteration < 3; iteration++) {
                auto r = frame.output_rect;
                auto f = frame_decoration(frame_style_, frame_scale_, frame_toolbar_scale_, -1, 0,
                    std::min(canvas.width, canvas.height), r.width, r.height);
                frame.output_rect = fit({region.x + f.inset, region.y + f.top, region.width - 2 * f.inset, region.height - f.top - f.inset},
                    media->width * crop_width_, media->height * crop_height_);
            }
        }
        return frame;
    };
    FrameScene scene{project_.revision, output_ticks, mapped->ticks, mapped->segment_id, canvas.width, canvas.height, canvas.background,
        make_screen_frame(mapped->ticks), std::nullopt};
    if (canvas.camera.enabled) {
        if (const auto* camera = media_at(project_, MediaKind::camera, mapped->ticks)) {
            scene.camera = MediaFrame{camera->id, mapped->ticks - normalized_ticks(camera->start_offset),
                fit(pixels(canvas.camera.rect, canvas), camera->width, camera->height), canvas.camera.mirror};
        }
    }
    scene.visual=evaluate_visuals(effects_,mapped->ticks); scene.visual.frame_style=frame_style_; scene.visual.camera_shape=camera_shape_;
    // Authored focus is normalized to the fitted/cropped screen. GPU projection
    // uses canvas coordinates, which also include padding and letterboxing.
    scene.visual.screen.focus_x=(scene.screen.output_rect.x+scene.visual.screen.focus_x*scene.screen.output_rect.width)/canvas.width;
    scene.visual.screen.focus_y=(scene.screen.output_rect.y+scene.visual.screen.focus_y*scene.screen.output_rect.height)/canvas.height;
    if (project_.editor_json.find("motionBlur") != std::string::npos) {
        const auto settings = nlohmann::json::parse(project_.editor_json);
        const double motion_blur = std::clamp(settings.value("motionBlur", 0.0), 0.0, 1.0);
        if (motion_blur > 0.001) {
            const auto prev_source_ticks = (std::max<std::int64_t>)(0, mapped->ticks - time_scale / 60);
            const auto prev_visual = evaluate_visuals(effects_, prev_source_ticks);
            const double prev_focus_x = (scene.screen.output_rect.x + prev_visual.screen.focus_x * scene.screen.output_rect.width) / canvas.width;
            const double prev_focus_y = (scene.screen.output_rect.y + prev_visual.screen.focus_y * scene.screen.output_rect.height) / canvas.height;
            scene.visual.screen.motion_blur_x = (scene.visual.screen.focus_x - prev_focus_x) * motion_blur;
            scene.visual.screen.motion_blur_y = (scene.visual.screen.focus_y - prev_focus_y) * motion_blur;
            scene.visual.screen.motion_blur_scale = (scene.visual.screen.scale - prev_visual.screen.scale) * motion_blur;
        }
    }
    if(scene.camera && camera_shape_=="circle") { auto box=pixels(canvas.camera.rect,canvas); auto side=std::min(box.width,box.height); scene.camera->output_rect={box.x+(box.width-side)/2,box.y+(box.height-side)/2,side,side}; }
    if(scene.camera){auto r=scene.camera->output_rect;scene.visual.camera.focus_x=(r.x+r.width/2)/canvas.width;scene.visual.camera.focus_y=(r.y+r.height/2)/canvas.height;scene.visual.camera.fixed_anchor=true;}

    const auto& segs = timeline_.segments();
    if (segs.size() >= 2) {
        for (const auto& e : effects_) {
            if (e.kind != "transition") continue;
            std::size_t seam_index = segs.size();
            if (!e.from_segment_id.empty() && !e.to_segment_id.empty()) {
                for (std::size_t i = 0; i + 1 < segs.size(); ++i) {
                    if (segs[i].id == e.from_segment_id && segs[i + 1].id == e.to_segment_id) {
                        seam_index = i;
                        break;
                    }
                }
            } else {
                const auto e_end = e.start + e.duration;
                for (std::size_t i = 0; i + 1 < segs.size(); ++i) {
                    if ((segs[i + 1].source_start >= e.start && segs[i + 1].source_start <= e_end) ||
                        (segs[i].source_end() >= e.start && segs[i].source_end() <= e_end)) {
                        seam_index = i;
                        break;
                    }
                }
            }
            if (seam_index + 1 >= segs.size()) continue;
            const auto& left = segs[seam_index];
            const auto& right = segs[seam_index + 1];
            const auto cut = left.output_end();
            const auto desired = static_cast<std::int64_t>(std::llround(e.transition_duration * time_scale));
            const auto half = std::min({desired / 2, left.output_duration / 2, right.output_duration / 2});
            if (half <= 0) continue;
            if (output_ticks < cut - half || output_ticks >= cut + half) continue;

            const double raw_u = static_cast<double>(output_ticks - (cut - half)) / static_cast<double>(half * 2);
            const double progress = motion_curve(raw_u, e.easing, e.frequency, e.damping);

            std::int64_t out_source = left.source_end() - 1;
            if (output_ticks < cut) {
                out_source = mapped->ticks;
            } else {
                const auto extrap = left.source_end() + multiply_ratio(output_ticks - cut, left.rate.numerator, left.rate.denominator);
                const auto* left_media = media_at(project_, MediaKind::screen, left.source_end() - 1);
                if (left_media) {
                    const auto m_start = normalized_ticks(left_media->start_offset);
                    const auto m_end = m_start + normalized_ticks(left_media->duration);
                    if (extrap >= m_start && extrap < m_end) out_source = extrap;
                }
            }

            std::int64_t in_source = right.source_start;
            if (output_ticks >= cut) {
                in_source = mapped->ticks;
            } else {
                const auto delta = multiply_ratio(cut - output_ticks, right.rate.numerator, right.rate.denominator);
                const auto extrap = right.source_start - delta;
                const auto* right_media = media_at(project_, MediaKind::screen, right.source_start);
                if (right_media) {
                    const auto m_start = normalized_ticks(right_media->start_offset);
                    const auto m_end = m_start + normalized_ticks(right_media->duration);
                    if (extrap >= m_start && extrap < m_end) in_source = extrap;
                }
            }

            scene.transition = TransitionScene{e.mode, progress, make_screen_frame(out_source), make_screen_frame(in_source)};
            break;
        }
    }
    return scene;
}
} // namespace frametrail::core
