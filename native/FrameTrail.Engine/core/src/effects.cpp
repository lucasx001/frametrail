#include "frametrail/core/effects.hpp"
// Planner rules adapted from DogSC AutoZoomPlanner.swift; see THIRD_PARTY_NOTICES.md.
#include <algorithm>
#include <cmath>
#include <numbers>
#include <nlohmann/json.hpp>

namespace frametrail::core {
using Json = nlohmann::json;
namespace {
double number(const Json& p, const char* key, double fallback, double low, double high) {
    if (!p.contains(key)) return fallback;
    if (!p[key].is_number()) throw ValidationError(std::string("effects.parameters.") + key, "expected number");
    double value = p[key].get<double>();
    if (!std::isfinite(value) || value < low || value > high) throw ValidationError(std::string("effects.parameters.") + key, "outside allowed range");
    return value;
}
std::string choice(const Json& p, const char* key, const char* fallback, std::initializer_list<std::string_view> allowed) {
    auto value = p.value(key, std::string(fallback));
    if (std::find(allowed.begin(), allowed.end(), value) == allowed.end()) throw ValidationError(std::string("effects.parameters.") + key, "unsupported value");
    return value;
}
bool overlaps(const Effect& a, int64_t start, int64_t end) {
    auto first = normalized_ticks(a.source_start); return first < end && first + normalized_ticks(a.source_duration) > start;
}
}
VisualEffect parse_visual_effect(const Effect& effect) {
    const auto p = Json::parse(effect.parameters_json); VisualEffect v;
    v.id = effect.id; v.kind = effect.kind; v.start = normalized_ticks(effect.source_start); v.duration = normalized_ticks(effect.source_duration);
    v.rect = {number(p,"x",0.25,0,1),number(p,"y",0.25,0,1),number(p,"width",0.5,0.001,1),number(p,"height",0.5,0.001,1)};
    if ((effect.kind == "mask" || effect.kind == "focus" || effect.kind == "sticker") && (v.rect.x + v.rect.width > 1.00000001 || v.rect.y + v.rect.height > 1.00000001)) throw ValidationError("effects.rect", "rectangle must fit attachment");
    v.scale = number(p,"scale",1.6,1,4); v.focus_x = number(p,"focusX",0.5,0,1); v.focus_y = number(p,"focusY",0.5,0,1);
    bool motion=effect.kind=="zoom" || effect.kind=="tilt";
    v.enter = number(p,"enter",motion || effect.kind=="intro" ? 0.7 : 0,0,5); v.exit = number(p,"exit",motion ? 0.7 : 0,0,5);
    v.strength = number(p,"strength",16,1,64); v.opacity = number(p,"opacity",1,0,1);
    v.angle_x = number(p,"angleX",0,-35,35); v.angle_y = number(p,"angleY",0,-35,35); v.rotation = number(p,"rotation",0,-180,180);
    v.frequency = number(p,"springFrequency",3,1,12); v.damping = number(p,"damping",0.85,0.2,2);
    v.easing = choice(p,"easing","cubic",{"linear","cubic","spring"});
    v.attachment = choice(p,"attachment","screen",{"screen","canvas"}); v.target = choice(p,"target","screen",{"screen","camera"});
    if (effect.kind == "mask") v.mode = choice(p,"mode","solid",{"solid","pixelate","blur"});
    if (effect.kind == "intro") v.mode = choice(p,"style","fade",{"fade","rise","scale","slide"});
    v.path = p.value("path",std::string{}); v.text = p.value("text",std::string{});
    if (!v.path.empty() && (!is_project_relative_path(v.path) || !v.path.starts_with("assets/"))) throw ValidationError("effects.path", "import assets into the project first");
    if (v.text.size() > 1024) throw ValidationError("effects.text", "text too long");
    v.outside_hidden = p.value("outsideHidden",false);
    if (p.contains("origin")) (void)choice(p,"origin","manual",{"manual","automatic","edited"});
    if (p.contains("focusPoints")) {
        if (!p["focusPoints"].is_array() || p["focusPoints"].size() > 10000) throw ValidationError("effects.focusPoints", "invalid focus track");
        int64_t prior = -1;
        for (auto& key : p["focusPoints"]) {
            if (!key.at("ticks").is_number_integer()) throw ValidationError("effects.focusPoints", "integer source ticks required");
            auto ticks = key["ticks"].get<int64_t>();
            if (ticks < v.start || ticks >= v.start + v.duration || ticks <= prior) throw ValidationError("effects.focusPoints", "focus keys must increase inside effect");
            FocusPoint point{ticks,number(key,"x",0.5,0,1),number(key,"y",0.5,0,1)};
            if(v.focus_points.empty()){point.from_x=point.x;point.from_y=point.y;}
            else {auto& before=v.focus_points.back();double amount=motion_curve((ticks-before.ticks)/double(time_scale)/std::max(0.001,v.enter),v.easing,v.frequency,v.damping);point.from_x=before.from_x+(before.x-before.from_x)*amount;point.from_y=before.from_y+(before.y-before.from_y)*amount;}
            v.focus_points.push_back(point); prior = ticks;
        }
    }
    return v;
}
void validate_visual_parameters(const Effect& effect) { (void)parse_visual_effect(effect); }
double motion_curve(double t, std::string_view easing, double frequency, double damping) {
    t = std::clamp(t,0.0,1.0); if (t == 0 || t == 1 || easing == "linear") return t;
    if (easing != "spring") return t * t * (3 - 2 * t);
    // Closed form, normalized at the endpoint. No frame-by-frame integration.
    auto response = [&](double x) {
        const double w = 2 * std::numbers::pi * frequency;
        if (damping < 0.999) { double d = std::sqrt(1 - damping*damping), phase = w*d*x; return 1 - std::exp(-damping*w*x)*(std::cos(phase)+damping/d*std::sin(phase)); }
        if (damping <= 1.001) return 1 - (1+w*x)*std::exp(-w*x);
        double d=std::sqrt(damping*damping-1), a=-w*(damping-d), b=-w*(damping+d);
        return 1 - (b*std::exp(a*x)-a*std::exp(b*x))/(b-a);
    };
    return std::clamp(response(t)/std::max(0.0001,response(1)),0.0,1.2);
}
double effect_envelope(const VisualEffect& e, int64_t source_ticks) {
    if (source_ticks < e.start || source_ticks >= e.start + e.duration) return 0;
    double elapsed=(source_ticks-e.start)/double(time_scale), duration=e.duration/double(time_scale);
    double enter=std::min(e.enter,duration/2), exit=std::min(e.exit,duration/2);
    double in = enter > 0 ? motion_curve(elapsed/enter,e.easing,e.frequency,e.damping) : 1;
    double out = exit > 0 ? motion_curve((duration-elapsed)/exit,e.easing,e.frequency,e.damping) : 1;
    return std::min(in,out);
}
std::array<double,2> smooth_pointer(const std::vector<FocusPoint>& points, int64_t ticks, double seconds) {
    auto end=std::upper_bound(points.begin(),points.end(),ticks,[](auto t,auto& p){return t<p.ticks;});
    if (end==points.begin()) return {0.5,0.5};
    auto latest=std::prev(end); if (seconds<=0) return {latest->x,latest->y};
    double x=0,y=0,total=0; auto cutoff=ticks-static_cast<int64_t>(seconds*4*time_scale);
    for(auto it=end;it!=points.begin();) { --it; if(it->ticks<cutoff)break; double weight=std::exp(-(ticks-it->ticks)/double(time_scale)/seconds); x+=it->x*weight; y+=it->y*weight; total+=weight; }
    return total>0 ? std::array<double,2>{x/total,y/total} : std::array<double,2>{latest->x,latest->y};
}
VisualScene evaluate_visuals(const std::vector<VisualEffect>& effects, int64_t ticks) {
    VisualScene result;
    for (auto& e : effects) {
        bool active=ticks>=e.start && ticks<e.start+e.duration;
        if (e.kind=="intro" && e.target=="camera" && e.outside_hidden && !active) result.camera.opacity=0;
        if(!active)continue;
        double amount=effect_envelope(e,ticks); auto& plane=e.target=="camera"?result.camera:result.screen;
        if(e.kind=="zoom") {
            auto focus=std::array<double,2>{e.focus_x,e.focus_y};
            if(!e.focus_points.empty()) {
                auto next=std::upper_bound(e.focus_points.begin(),e.focus_points.end(),ticks,[](auto t,auto& p){return t<p.ticks;});
                auto key=next==e.focus_points.begin()?next:std::prev(next); focus={key->x,key->y};
                double focus_progress=motion_curve((ticks-key->ticks)/double(time_scale)/std::max(0.001,e.enter),e.easing,e.frequency,e.damping);
                focus={key->from_x+(key->x-key->from_x)*focus_progress,key->from_y+(key->y-key->from_y)*focus_progress};
            }
            result.screen.scale=std::max(result.screen.scale,1+(e.scale-1)*amount); result.screen.focus_x=focus[0]; result.screen.focus_y=focus[1];
        } else if(e.kind=="tilt") { plane.angle_x+=e.angle_x*amount; plane.angle_y+=e.angle_y*amount; }
        else if(e.kind=="intro") { plane.opacity*=std::clamp(amount,0.0,1.0); plane.reveal=std::min(plane.reveal,std::clamp(amount,0.0,1.0)); if(e.mode=="scale")plane.scale*=0.8+0.2*amount; if(e.mode=="rise")plane.offset_y+=0.12*(1-amount); if(e.mode=="slide")plane.offset_x-=0.12*(1-amount); }
        else if(e.kind=="mask" || e.kind=="focus") { auto value=e; value.opacity*=std::clamp(amount,0.0,1.0); result.regions.push_back(std::move(value)); }
        else if(e.kind=="sticker") { auto value=e; value.opacity*=std::clamp(amount,0.0,1.0); result.stickers.push_back(std::move(value)); }
    }
    return result;
}
void generate_auto_zoom(Project& project,std::string_view text) {
    const auto command=Json::parse(text);
    struct Event { int64_t ticks; double x,y; int kind; };
    auto events=command.at("events"); if(!events.is_array() || events.size()>100000)throw ValidationError("autoZoom.events","too many events");
    double scale=number(command,"scale",1.6,1.05,4), transition=number(command,"transition",0.7,0,5);
    auto easing=choice(command,"easing","spring",{"linear","cubic","spring"});
    std::vector<Event> ordered,clicks;
    auto duration=normalized_ticks(project.source_duration);
    for(auto& e:events) {
        auto t=e.at("ticks").get<int64_t>(); if(t<0 || t>=duration)continue;
        ordered.push_back({t,number(e,"x",0.5,0,1),number(e,"y",0.5,0,1),e.value("kind",0)});
    }
    std::stable_sort(ordered.begin(),ordered.end(),[](auto&a,auto&b){return a.ticks<b.ticks;});
    for(auto&e:ordered)if(e.kind==1 || e.kind==3)clicks.push_back(e);
    std::erase_if(project.effects,[](auto&e){auto p=Json::parse(e.parameters_json);return e.kind=="zoom" && p.value("origin","")=="automatic" && p.value("planner","")=="dogsc-v1";});
    int64_t earliest=0;
    for(size_t first=0;first<clicks.size();) {
        size_t end=first+1; while(end<clicks.size() && clicks[end].ticks-clicks[end-1].ticks<=14*time_scale/10)++end;
        auto start=std::max({int64_t(0),earliest,clicks[first].ticks-static_cast<int64_t>(transition*0.18*time_scale)});
        auto hold=std::max(clicks[end-1].ticks+9*time_scale/10,start+static_cast<int64_t>(transition*time_scale));
        auto activity=clicks[end-1].ticks;
        for(auto it=std::upper_bound(ordered.begin(),ordered.end(),activity,[](auto t,auto&e){return t<e.ticks;});it!=ordered.end();++it) {
            if(it->ticks-activity>14*time_scale/10 || (end<clicks.size() && it->ticks>=clicks[end].ticks-static_cast<int64_t>(std::llround(transition*0.18*time_scale))))break;
            activity=it->ticks;
        }
        if(activity>clicks[end-1].ticks)hold=std::max(hold,activity+55*time_scale/100);
        auto finish=std::min(duration,hold+static_cast<int64_t>(transition*time_scale));
        if(end<clicks.size())finish=std::min(finish,std::max(start+1,clicks[end].ticks-static_cast<int64_t>(transition*0.18*time_scale)));
        Json keys=Json::array();
        for(size_t group=first;group<end;) {
            size_t next=group+1; double minx=clicks[group].x,maxx=minx,miny=clicks[group].y,maxy=miny;
            while(next<end && std::max(maxx,clicks[next].x)-std::min(minx,clicks[next].x)<=0.5/scale && std::max(maxy,clicks[next].y)-std::min(miny,clicks[next].y)<=0.7/scale) {
                minx=std::min(minx,clicks[next].x);maxx=std::max(maxx,clicks[next].x);miny=std::min(miny,clicks[next].y);maxy=std::max(maxy,clicks[next].y);++next;
            }
            int64_t key=group==first?start:std::max(keys.back()["ticks"].get<int64_t>()+16*time_scale/100,clicks[group].ticks-static_cast<int64_t>(transition*time_scale));
            if(key<finish)keys.push_back({{"ticks",key},{"x",std::clamp((minx+maxx)/2,0.12,0.88)},{"y",std::clamp((miny+maxy)/2,0.12,0.88)}});
            group=next;
        }
        if(finish>start+time_scale/100 && !keys.empty() && std::none_of(project.effects.begin(),project.effects.end(),[&](auto&e){return e.kind=="zoom" && overlaps(e,start,finish);})) {
            Json p{{"origin","automatic"},{"planner","dogsc-v1"},{"scale",scale},{"focusX",keys[0]["x"]},{"focusY",keys[0]["y"]},{"focusPoints",keys},{"enter",transition},{"exit",std::min(transition,std::max(0.0,(finish-hold)/double(time_scale)))},{"easing",easing}};
            auto base="auto-zoom-"+std::to_string(start),id=base;size_t suffix=0;
            while(std::any_of(project.effects.begin(),project.effects.end(),[&](auto&e){return e.id==id;}))id=base+"-"+std::to_string(++suffix);
            project.effects.push_back({id,"zoom",canonical_time(start),canonical_time(finish-start),p.dump()});
        }
        earliest=finish;first=end;
    }
    // Connected activity bursts keep one envelope and continuous focus retargets.
    std::vector<Effect*> automatic;
    for(auto&e:project.effects){auto p=Json::parse(e.parameters_json);if(e.kind=="zoom" && p.value("origin","")=="automatic" && p.value("planner","")=="dogsc-v1")automatic.push_back(&e);}
    std::sort(automatic.begin(),automatic.end(),[](auto a,auto b){return normalized_ticks(a->source_start)<normalized_ticks(b->source_start);});
    std::vector<std::string> remove;
    Effect* prior=nullptr;
    for(auto* next:automatic){
        if(prior){auto a=Json::parse(prior->parameters_json),b=Json::parse(next->parameters_json);
            if(a.value("exit",0.7)<=0.000001 && std::abs(normalized_ticks(prior->source_start)+normalized_ticks(prior->source_duration)-normalized_ticks(next->source_start))<=10){
                for(auto& key:b["focusPoints"])a["focusPoints"].push_back(key);a["exit"]=b["exit"];prior->source_duration=canonical_time(normalized_ticks(next->source_start)+normalized_ticks(next->source_duration)-normalized_ticks(prior->source_start));prior->parameters_json=a.dump();remove.push_back(next->id);continue;
            }}prior=next;
    }
    std::erase_if(project.effects,[&](auto&e){return std::find(remove.begin(),remove.end(),e.id)!=remove.end();});
}
} // namespace frametrail::core
