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
    v.focus_transition = number(p,"focusTransition",v.enter,0,5);
    v.strength = number(p,"strength",16,1,64); v.opacity = number(p,"opacity",1,0,1);
    v.angle_x = number(p,"angleX",0,-35,35); v.angle_y = number(p,"angleY",0,-35,35); v.rotation = number(p,"rotation",0,-180,180);
    v.frequency = number(p,"springFrequency",default_spring_frequency,1,12); v.damping = number(p,"damping",default_spring_damping,0.2,2);
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
            if(key.contains("phaseTicks") && !key["phaseTicks"].is_number_integer())throw ValidationError("effects.focusPoints.phaseTicks","integer phase ticks required");
            auto phase=key.value("phaseTicks",int64_t(0));
            if(phase<0 || phase>5*time_scale)throw ValidationError("effects.focusPoints.phaseTicks","phase must be between zero and five seconds");
            point.transition_start=ticks-phase;
            if(v.focus_points.empty()){point.from_x=number(key,"fromX",point.x,0,1);point.from_y=number(key,"fromY",point.y,0,1);}
            else {auto& before=v.focus_points.back();double amount=motion_curve((ticks-before.transition_start)/double(time_scale)/std::max(0.001,v.focus_transition),v.easing,v.frequency,v.damping);point.from_x=before.from_x+(before.x-before.from_x)*amount;point.from_y=before.from_y+(before.y-before.from_y)*amount;}
            v.focus_points.push_back(point); prior = ticks;
        }
    }
    return v;
}
void validate_visual_parameters(const Effect& effect) { (void)parse_visual_effect(effect); }
double motion_curve(double t, std::string_view easing, double frequency, double damping) {
    t = std::clamp(t,0.0,1.0); if (t == 0 || t == 1 || easing == "linear") return t;
    if (easing != "spring") {
        // DogSC ZoomBezierCurve.cubic: solve X before sampling Y (0.65,0,0.35,1).
        auto x = [](double u) { double v=1-u; return 3*v*v*u*0.65+3*v*u*u*0.35+u*u*u; };
        auto y = [](double u) { return u*u*(3-2*u); };
        double u=t;
        for(int i=0;i<7;++i) {
            double error=x(u)-t; if(std::abs(error)<0.00001)return y(u);
            double v=1-u,derivative=3*v*v*0.65+6*v*u*(0.35-0.65)+3*u*u*(1-0.35);
            if(std::abs(derivative)<0.00001)break;
            double next=u-error/derivative; if(next<0 || next>1)break; u=next;
        }
        double lower=0,upper=1;u=t;
        for(int i=0;i<18;++i) { double value=x(u);if(std::abs(value-t)<0.00001)return y(u);if(value<t)lower=u;else upper=u;u=(lower+upper)/2; }
        return y(u);
    }
    // DogSC ZoomInterpolator.springProgress samples 0.72 seconds of its
    // physical spring, then normalizes to exact endpoints without overshoot.
    const double w=2*std::numbers::pi*frequency, zeta=std::min(damping,0.9999);
    const double wd=w*std::sqrt(1-zeta*zeta);
    auto response = [&](double x) {
        return 1-std::exp(-zeta*w*x)*(std::cos(wd*x)+zeta*w/wd*std::sin(wd*x));
    };
    return std::clamp(response(t*0.72)/response(0.72),0.0,1.0);
}
double effect_envelope(const VisualEffect& e, int64_t source_ticks) {
    if (source_ticks < e.start || source_ticks >= e.start + e.duration) return 0;
    double elapsed=(source_ticks-e.start)/double(time_scale), duration=e.duration/double(time_scale);
    if(e.kind=="zoom") {
        // Stored ranges include the return phase. DogSC holds the target until
        // the return starts, then samples 1-ease(elapsed), not ease(remaining).
        double exit=std::min(e.exit,duration), hold_end=duration-exit, enter=std::min(e.enter,hold_end);
        if(enter>0 && elapsed<enter)return motion_curve(elapsed/enter,e.easing,e.frequency,e.damping);
        if(elapsed<=hold_end)return 1;
        return exit>0 ? 1-motion_curve((elapsed-hold_end)/exit,e.easing,e.frequency,e.damping) : 1;
    }
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
                double focus_progress=motion_curve((ticks-key->transition_start)/double(time_scale)/std::max(0.001,e.focus_transition),e.easing,e.frequency,e.damping);
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
    std::erase_if(project.effects,[](auto&e){auto p=Json::parse(e.parameters_json);auto planner=p.value("planner","");return e.kind=="zoom" && p.value("origin","")=="automatic" && (planner=="dogsc-v1" || planner=="frametrail-incremental-v1");});
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
// Recording consumes a bounded batch once. The final automatic effect carries
// only the open burst's sufficient statistics, never the historical event list.
void append_auto_zoom(Project& project, std::string_view text) {
    const auto command = Json::parse(text);
    const auto& input = command.at("events");
    if (!input.is_array() || input.size() > 8192) throw ValidationError("autoZoomAppend.events", "batch limit exceeded");
    const auto total = normalized_ticks(project.source_duration);
    const auto through = command.at("throughTicks").get<int64_t>();
    if (through <= 0 || through > total) throw ValidationError("autoZoomAppend.throughTicks", "outside source duration");
    const double scale = number(command, "scale", 1.6, 1.05, 4), transition = 0.7;
    constexpr int64_t activity_gap = 14 * time_scale / 10;
    constexpr int64_t advance = 126 * time_scale / 1000;
    struct Event { int64_t ticks; double x, y; int kind; };
    std::vector<Event> events;
    for (auto& e : input) {
        auto t = e.at("ticks").get<int64_t>();
        if (t < 0 || t >= through) throw ValidationError("autoZoomAppend.events", "event outside batch horizon");
        events.push_back({t, number(e,"x",0.5,0,1), number(e,"y",0.5,0,1), e.value("kind",0)});
    }
    std::stable_sort(events.begin(), events.end(), [](auto& a, auto& b) { return a.ticks < b.ticks; });
    size_t current = project.effects.size();
    Json parameters, state;
    for (size_t i = project.effects.size(); i > 0; --i) {
        const auto& effect = project.effects[i - 1];
        auto p = Json::parse(effect.parameters_json);
        if (effect.kind == "zoom" && p.value("origin", "") == "automatic" &&
            p.value("planner", "") == "frametrail-incremental-v1" && p.contains("plannerState")) {
            current = i - 1; parameters = p; state = p["plannerState"]; break;
        }
    }
    auto exists = [&] { return current < project.effects.size(); };
    auto hold_until = [&](int64_t activity) {
        auto hold = std::max(state["lastClick"].get<int64_t>() + 9 * time_scale / 10,
                             state["groupStart"].get<int64_t>() + 7 * time_scale / 10);
        if (activity > state["lastClick"].get<int64_t>()) hold = std::max(hold, activity + 55 * time_scale / 100);
        return hold;
    };
    auto materialize = [&](int64_t horizon, int64_t activity) {
        auto& effect = project.effects[current];
        const auto start = normalized_ticks(effect.source_start);
        const auto hold = hold_until(activity);
        auto end = std::min(horizon, hold + 7 * time_scale / 10);
        // Authored effects always win. Never extend an automatic camera into one.
        for (const auto& other : project.effects) {
            if (other.kind != "zoom" || other.id == effect.id) continue;
            auto p = Json::parse(other.parameters_json);
            if (p.value("origin", "") == "automatic") continue;
            auto t = normalized_ticks(other.source_start);
            if (t >= start) end = std::min(end, t);
        }
        end = std::max(start + 1, end);
        effect.source_duration = canonical_time(end - start);
        parameters["exit"] = std::min(transition, std::max(0.0, (end - hold) / double(time_scale)));
        parameters["focusX"]=parameters["focusPoints"][0]["x"];
        parameters["focusY"]=parameters["focusPoints"][0]["y"];
        parameters["plannerState"] = state;
        effect.parameters_json = parameters.dump();
    };
    auto new_effect = [&](int64_t start, double x, double y, double enter, Json first) {
        auto id = "auto-zoom-" + std::to_string(start), base = id;
        size_t suffix = 0;
        while (std::any_of(project.effects.begin(),project.effects.end(),[&](auto& e){return e.id==id;})) id=base+"-"+std::to_string(++suffix);
        parameters = Json{{"origin","automatic"},{"planner","frametrail-incremental-v1"},{"scale",scale},
            {"focusX",x},{"focusY",y},{"focusPoints",Json::array({first})},{"enter",enter},{"focusTransition",transition},
            {"exit",0},{"easing","spring"},{"plannerState",state}};
        current = project.effects.size();
        project.effects.push_back({id,"zoom",canonical_time(start),canonical_time(std::max<int64_t>(1,through-start)),parameters.dump()});
    };
    auto add_focus = [&](int64_t ticks, int64_t click_ticks, double x, double y) {
        auto& keys = parameters["focusPoints"];
        ticks = std::min(click_ticks, std::max(keys.back()["ticks"].get<int64_t>() + 16 * time_scale / 100, ticks));
        if (ticks <= keys.back()["ticks"].get<int64_t>()) {
            keys.back()["x"]=x; keys.back()["y"]=y; return;
        }
        // Limit each saved focus track below the schema limit without changing
        // its trajectory: the successor starts with the sampled previous focus.
        if (keys.size() >= 2048) {
            materialize(std::max(through, ticks + 1), state["activity"].get<int64_t>());
            auto visual = parse_visual_effect(project.effects[current]);
            auto focus = evaluate_visuals({visual}, ticks).screen;
            auto start = normalized_ticks(project.effects[current].source_start);
            project.effects[current].source_duration = canonical_time(ticks - start);
            parameters["exit"] = 0; parameters.erase("plannerState");
            project.effects[current].parameters_json = parameters.dump();
            new_effect(ticks,x,y,0,Json{{"ticks",ticks},{"x",x},{"y",y},{"fromX",focus.focus_x},{"fromY",focus.focus_y}});
        } else keys.push_back({{"ticks",ticks},{"x",x},{"y",y}});
    };
    for (const auto& event : events) {
        const bool click = event.kind == 1 || event.kind == 3;
        if (!click) {
            if (exists() && event.ticks - state["activity"].get<int64_t>() <= activity_gap) {
                state["activity"] = event.ticks;
                auto& recent = state["recentActivity"];
                recent.push_back(event.ticks);
                while (recent.size() > 1 && recent[1].get<int64_t>() < event.ticks - advance) recent.erase(recent.begin());
                while (recent.size() > 128) recent.erase(recent.begin()+1);
            }
            continue;
        }
        bool same_group = exists() && event.ticks - state["lastClick"].get<int64_t>() <= activity_gap;
        bool joined = false;
        int64_t start = std::max<int64_t>(0,event.ticks-advance);
        if (exists() && !same_group) {
            auto activity = state["lastClick"].get<int64_t>();
            for (auto& value : state["recentActivity"]) if (value.get<int64_t>() < start) activity=std::max(activity,value.get<int64_t>());
            auto previous_start = normalized_ticks(project.effects[current].source_start);
            start = std::max(start,previous_start+1);
            materialize(start,activity);
            auto end = previous_start + normalized_ticks(project.effects[current].source_duration);
            joined = parameters["exit"].get<double>() <= 0.000001 && end == start;
            if (!joined) {
                parameters.erase("plannerState"); project.effects[current].parameters_json=parameters.dump();
                current=project.effects.size();
            }
        }
        if (!exists()) {
            auto end = std::min(through,event.ticks+16*time_scale/10);
            if (std::any_of(project.effects.begin(),project.effects.end(),[&](auto& e){return e.kind=="zoom" && overlaps(e,start,end);})) continue;
            state=Json{{"lastClick",event.ticks},{"activity",event.ticks},{"groupStart",start},
                {"minX",event.x},{"maxX",event.x},{"minY",event.y},{"maxY",event.y},{"recentActivity",Json::array({event.ticks})}};
            auto x=std::clamp(event.x,0.12,0.88),y=std::clamp(event.y,0.12,0.88);
            new_effect(start,x,y,transition,Json{{"ticks",start},{"x",x},{"y",y}});
        } else {
            double minx=std::min(state["minX"].get<double>(),event.x),maxx=std::max(state["maxX"].get<double>(),event.x);
            double miny=std::min(state["minY"].get<double>(),event.y),maxy=std::max(state["maxY"].get<double>(),event.y);
            bool spatial_group=same_group && maxx-minx<=0.5/scale && maxy-miny<=0.7/scale;
            if (!spatial_group) {
                minx=maxx=event.x;miny=maxy=event.y;
                add_focus(joined ? start : event.ticks-7*time_scale/10,event.ticks,std::clamp(event.x,0.12,0.88),std::clamp(event.y,0.12,0.88));
            } else {
                auto& key=parameters["focusPoints"].back();
                key["x"]=std::clamp((minx+maxx)/2,0.12,0.88);key["y"]=std::clamp((miny+maxy)/2,0.12,0.88);
            }
            state["minX"]=minx;state["maxX"]=maxx;state["minY"]=miny;state["maxY"]=maxy;
            state["lastClick"]=event.ticks;state["activity"]=event.ticks;
            if (!same_group) {state["groupStart"]=start;state["recentActivity"]=Json::array();}
            auto& recent=state["recentActivity"];
            if(recent.empty() || recent.back().get<int64_t>()!=event.ticks)recent.push_back(event.ticks);
            while(recent.size()>1 && recent[1].get<int64_t>()<event.ticks-advance)recent.erase(recent.begin());
            while(recent.size()>128)recent.erase(recent.begin()+1);
        }
    }
    if (exists()) materialize(through,state["activity"].get<int64_t>());
}
} // namespace frametrail::core
