#pragma once
#include "internal.hpp"
#include "subtitle_renderer.hpp"
#include "frametrail/core/plane_projection.hpp"
#include <cmath>
#include <numbers>

namespace frametrail::media {
inline Json edit_geometry(const Project& p,const FrameScene& scene,bool crop_mode) {
    Json items=Json::array();
    auto quad=[&](Rect r,const PlaneMotion& motion,double rotation=0) {
        Json q=Json::array(); double a=rotation*std::numbers::pi/180, cx=r.x+r.width/2,cy=r.y+r.height/2;
        for(auto uv:std::array<std::array<double,2>,4>{{{0,0},{1,0},{1,1},{0,1}}}) {
            double x=r.x+uv[0]*r.width-cx,y=r.y+uv[1]*r.height-cy;
            auto v=plane_vertex((cx+x*std::cos(a)-y*std::sin(a))/scene.width,(cy+x*std::sin(a)+y*std::cos(a))/scene.height,motion,scene.width,scene.height);
            q.push_back({(v[0]/v[2]+1)/2,(1-v[1]/v[2])/2});
        }return q;
    };
    auto add=[&](const std::string& id,const std::string& kind,Rect rect,Rect base,const PlaneMotion& m,double rotation=0) {
        if(m.opacity<0.0001 || m.scale<0.0001 || base.width<=0 || base.height<=0)return;
        Rect r{base.x+rect.x*base.width,base.y+rect.y*base.height,rect.width*base.width,rect.height*base.height};
        items.push_back({{"id",id},{"kind",kind},{"rotation",rotation},{"aspect",base.width/base.height},{"rect",{{"x",rect.x},{"y",rect.y},{"width",rect.width},{"height",rect.height}}},{"quad",quad(r,m,rotation)},{"base",quad(base,m)}});
    };
    const Rect canvas{0,0,double(scene.width),double(scene.height)};
    if(crop_mode) {
        // In crop mode the caller displays the uncropped source; handles use its fitted rect.
        auto settings=Json::parse(p.editor_json);auto c=settings.value("crop",Json::object());
        add("crop","crop",{c.value("x",0.0),c.value("y",0.0),c.value("width",1.0),c.value("height",1.0)},scene.screen.output_rect,scene.visual.screen);
    } else {
        for(auto const& e:scene.visual.regions) add(e.id,e.kind,e.rect,e.attachment=="screen"?scene.screen.output_rect:canvas,e.attachment=="screen"?scene.visual.screen:PlaneMotion{});
        for(auto const& e:scene.visual.stickers) add(e.id,e.kind,e.rect,e.attachment=="screen"?scene.screen.output_rect:canvas,e.attachment=="screen"?scene.visual.screen:PlaneMotion{},e.rotation);
        if (!scene.visual.subtitles.empty()) {
            const auto settings = Json::parse(p.editor_json);
            const auto base_style = parse_subtitle_style(settings);
            const float scale_ref = static_cast<float>(std::min(scene.width, scene.height)) / 1080.0f;
            float stack_offset_y = 0.0f;
            for (const auto& e : scene.visual.subtitles) {
                const auto cue_style = resolve_cue_style(base_style, e.subtitle_override);
                const auto rect = measure_subtitle_rect(nullptr, e, cue_style, scene.width, scene.height, stack_offset_y);
                add(e.id, "subtitle", rect, canvas, PlaneMotion{});
                stack_offset_y += static_cast<float>(rect.height * scene.height) + 12.0f * scale_ref;
            }
        }
    }
    return items;
}
}
