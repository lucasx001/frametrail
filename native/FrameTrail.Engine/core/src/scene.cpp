#include "frametrail/core/scene.hpp"
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
    for(auto&e:project_.effects)effects_.push_back(parse_visual_effect(e));
}

std::optional<FrameScene> FrameSceneEvaluator::evaluate(std::int64_t output_ticks) const {
    const auto mapped = timeline_.source_time(output_ticks);
    if (!mapped) return std::nullopt;
    const auto* screen = media_at(project_, MediaKind::screen, mapped->ticks);
    if (!screen) throw ValidationError("media", "no screen frame covers mapped source instant");
    const auto& canvas = project_.canvas;
    auto region = pixels(canvas.screen, canvas);
    const auto padding = canvas.padding * (std::min)(canvas.width, canvas.height);
    region.x += padding; region.y += padding; region.width -= 2 * padding; region.height -= 2 * padding;
    FrameScene scene{project_.revision, output_ticks, mapped->ticks, mapped->segment_id, canvas.width, canvas.height, canvas.background,
        {screen->id, mapped->ticks - normalized_ticks(screen->start_offset), fit(region, screen->width * crop_width_, screen->height * crop_height_), false}, std::nullopt};
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
    if(scene.camera && camera_shape_=="circle") { auto box=pixels(canvas.camera.rect,canvas); auto side=std::min(box.width,box.height); scene.camera->output_rect={box.x+(box.width-side)/2,box.y+(box.height-side)/2,side,side}; }
    if(scene.camera){auto r=scene.camera->output_rect;scene.visual.camera.focus_x=(r.x+r.width/2)/canvas.width;scene.visual.camera.focus_y=(r.y+r.height/2)/canvas.height;scene.visual.camera.fixed_anchor=true;}
    return scene;
}
} // namespace frametrail::core
