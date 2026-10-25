#include "frametrail/core/plane_projection.hpp"
#include "../../native/FrameTrail.Engine/media/pcm_wave.hpp"
#include <sstream>
#include "frametrail/core/editor.hpp"
#include "frametrail/core/scene.hpp"
#include "frametrail/core/cursor.hpp"
#include <nlohmann/json.hpp>
#include <fstream>
#include <iostream>
#include <cmath>
#include <random>

using namespace frametrail::core;
using Json=nlohmann::json;
namespace {
int checks{};
void expect(bool condition,const char* name){++checks;if(!condition)throw std::runtime_error(name);}
template<class F>void rejects(F&& fn,const char* name){++checks;try{fn();}catch(const ValidationError&){return;}throw std::runtime_error(name);}
Effect effect(std::string id,std::string kind,Json parameters,int64_t start=0,int64_t duration=30'000'000){return {id,kind,canonical_time(start),canonical_time(duration),parameters.dump()};}
}
int main(){try{
    expect(cursor_idle_opacity(10'000'000,0,true,1.2)==1,"cursor must remain visible before idle delay");
    expect(std::abs(cursor_idle_opacity(13'000'000,0,true,1.2)-0.5)<1e-9,"idle hide must fade using media time");
    expect(cursor_idle_opacity(20'000'000,0,true,1.2)==0,"stationary cursor must hide");
    expect(cursor_idle_opacity(20'000'000,0,false,1.2)==1,"disabled idle hide must preserve legacy output");
    expect(cursor_click_progress(8'000'000,2'500'000)==1,"click feedback must end deterministically");
    std::ifstream file(std::string(FRAMETRAIL_FIXTURES)+"/project-v1.json");std::string text((std::istreambuf_iterator<char>(file)),{});auto p=parse_project(text);p.effects.clear();
    Json events=Json::array({{{"ticks",10'000'000},{"x",0.3},{"y",0.4},{"kind",1}},{{"ticks",12'000'000},{"x",0.35},{"y",0.45},{"kind",3}},{{"ticks",15'000'000},{"x",0.36},{"y",0.46},{"kind",0}},{{"ticks",20'000'000},{"x",0.5},{"y",0.5},{"kind",0}},{{"ticks",25'000'000},{"x",0.6},{"y",0.5},{"kind",0}},{{"ticks",30'000'000},{"x",0.75},{"y",0.5},{"kind",0}},{{"ticks",34'000'000},{"x",0.8},{"y",0.6},{"kind",1}}});
    Json command{{"op","autoZoom"},{"events",events}};auto a=edit_project(p,command.dump());
    expect(a.effects.size()==1,"connected activity groups must merge into one zoom envelope");
    auto parameters=Json::parse(a.effects[0].parameters_json);expect(parameters["focusPoints"].size()==2,"nearby clicks must share one stable spatial focus");
    {
        auto one=p;
        append_auto_zoom(one,Json{{"events",events},{"throughTicks",100'000'000}}.dump());
        for (int seed=0;seed<20;++seed) {
            std::mt19937 random(seed);auto split=p;size_t offset=0;
            while(offset<events.size()) {
                auto count=std::min<size_t>(1+random()%4,events.size()-offset);Json batch=Json::array();
                for(size_t i=0;i<count;++i)batch.push_back(events[offset+i]);
                auto horizon=batch.back()["ticks"].get<int64_t>()+1;
                append_auto_zoom(split,Json{{"events",batch},{"throughTicks",horizon}}.dump());offset+=count;
            }
            append_auto_zoom(split,Json{{"events",Json::array()},{"throughTicks",100'000'000}}.dump());
            expect(split.effects.size()==one.effects.size(),"batch splitting changed automatic effect count");
            for(size_t i=0;i<one.effects.size();++i) {
                expect(split.effects[i].parameters_json==one.effects[i].parameters_json,"batch splitting changed the saved trajectory");
                expect(normalized_ticks(split.effects[i].source_duration)==normalized_ticks(one.effects[i].source_duration),"batch splitting changed the envelope");
            }
        }
        for(int64_t t=0;t<100'000'000;t+=100'000) {
            auto old=evaluate_visuals({parse_visual_effect(a.effects[0])},t).screen;
            auto next=evaluate_visuals({parse_visual_effect(one.effects[0])},t).screen;
            expect(std::abs(old.scale-next.scale)<1e-9 && std::abs(old.focus_x-next.focus_x)<1e-9 && std::abs(old.focus_y-next.focus_y)<1e-9,"incremental baseline differs from DogSC reference");
        }
        auto edited_incremental=one;auto authored=Json::parse(edited_incremental.effects[0].parameters_json);authored["origin"]="edited";
        edited_incremental.effects[0].parameters_json=authored.dump();
        append_auto_zoom(edited_incremental,Json{{"events",Json::array({{{"ticks",38'000'000},{"x",0.1},{"y",0.1},{"kind",1}}})},{"throughTicks",100'000'000}}.dump());
        expect(edited_incremental.effects[0].parameters_json==authored.dump(),"incremental planning overwrote authored parameters");
    }
    {
        Json boundary_events=Json::array({{{"ticks",0},{"x",0.2},{"y",0.4},{"kind",1}},
            {{"ticks",14'000'000},{"x",0.8},{"y",0.4},{"kind",1}},
            {{"ticks",28'000'000},{"x",0.2},{"y",0.4},{"kind",1}},
            {{"ticks",39'000'000},{"x",0.3},{"y",0.4},{"kind",0}},
            {{"ticks",41'000'000},{"x",0.4},{"y",0.4},{"kind",0}},
            {{"ticks",42'000'001},{"x",0.8},{"y",0.4},{"kind",1}},
            {{"ticks",80'000'000},{"x",0.4},{"y",0.5},{"kind",1}}});
        auto whole=p;append_auto_zoom(whole,Json{{"events",boundary_events},{"throughTicks",100'000'000}}.dump());
        for(size_t split_at=1;split_at<boundary_events.size();++split_at) {
            auto partial=p;Json first=Json::array(),second=Json::array();
            for(size_t i=0;i<boundary_events.size();++i)(i<split_at?first:second).push_back(boundary_events[i]);
            append_auto_zoom(partial,Json{{"events",first},{"throughTicks",first.back()["ticks"].get<int64_t>()+1}}.dump());
            append_auto_zoom(partial,Json{{"events",second},{"throughTicks",100'000'000}}.dump());
            expect(partial.effects.size()==whole.effects.size(),"1.4 second boundary split changed camera count");
            for(size_t i=0;i<whole.effects.size();++i)expect(partial.effects[i].parameters_json==whole.effects[i].parameters_json,"1.4 second or pre-click lead boundary depends on checkpoint split");
        }
    }
    {
        // More than the old lifetime event cap, including a single continuously
        // retargeted burst. The saved tracks stay under their own schema limit.
        auto long_plan=p;long_plan.source_duration=canonical_time(120'010'000'000LL);
        int64_t tick=10'000'000;
        for(int batch_index=0;batch_index<30;++batch_index) {
            Json batch=Json::array();
            for(int i=0;i<4000;++i,tick+=1'000'000)batch.push_back({{"ticks",tick},{"x",i%2?0.8:0.2},{"y",0.4},{"kind",1}});
            append_auto_zoom(long_plan,Json{{"events",batch},{"throughTicks",tick}}.dump());
        }
        expect(long_plan.effects.size()>1,"continuous burst must split before exhausting focus-point capacity");
        for(size_t i=0;i<long_plan.effects.size();++i) {
            auto visual=parse_visual_effect(long_plan.effects[i]);
            expect(visual.focus_points.size()<=2048,"incremental focus track exceeded bounded capacity");
            if(i>0) {
                auto boundary=normalized_ticks(long_plan.effects[i].source_start);
                auto previous=parse_visual_effect(long_plan.effects[i-1]);
                auto left=evaluate_visuals({previous},boundary-1).screen;
                auto right=evaluate_visuals({visual},boundary).screen;
                expect(std::abs(left.scale-right.scale)<1e-5,"bounded focus continuation introduced an enter/exit pulse");
                expect(std::abs(left.focus_x-right.focus_x)<1e-5,"bounded focus continuation jumped at its boundary");
            }
        }
        auto editable=long_plan;
        auto screen=p.media.front();screen.duration=editable.source_duration;screen.start_offset=canonical_time(0);
        editable.media={screen};editable.markers.clear();
        editable.segments={{"continuous",canonical_time(0),editable.source_duration,{1,1}}};
        editable.effects={long_plan.effects[1]};validate_project(editable);
        auto original=editable.effects[0];auto start=normalized_ticks(original.source_start),end=start+normalized_ticks(original.source_duration);
        auto source_visual=parse_visual_effect(original);
        for(auto edge:{"start","end","move"}) {
            auto destination=std::string_view(edge)=="start"?start+500'000:std::string_view(edge)=="end"?end-500'000:int64_t(0);
            auto edited=edit_project(editable,Json{{"op","effectAtOutput"},{"effectId",original.id},{"edge",edge},{"outputTicks",destination}}.dump());
            validate_project(edited);auto after=parse_visual_effect(edited.effects[0]);
            auto moved=std::string_view(edge)=="move";auto new_start=normalized_ticks(edited.effects[0].source_start);
            for(int64_t delta=0;delta<1'000'000;delta+=10'000) {
                auto before=evaluate_visuals({source_visual},moved?start+delta:new_start+delta).screen;
                auto now=evaluate_visuals({after},new_start+delta).screen;
                expect(std::abs(before.focus_x-now.focus_x)<1e-9 && std::abs(before.focus_y-now.focus_y)<1e-9,"trim/move changed a continuation focus trajectory");
            }
            auto properties=Json::parse(edited.effects[0].parameters_json);
            expect(properties["origin"]=="edited" && !properties.contains("plannerState"),"authored continuation retained mutable planner state");
        }
    }
    expect(normalized_ticks(a.effects[0].source_start)==8'740'000,"reference click lead must be 18 percent of enter duration");
    auto again=edit_project(a,command.dump());expect(again.effects.size()==1 && again.effects[0].id==a.effects[0].id && again.effects[0].parameters_json==a.effects[0].parameters_json,"regeneration must be idempotent");
    auto serialized=Json::parse(serialize_project(a));auto edited=serialized["effects"][0];edited["parameters"]["scale"]=2.1;
    a=edit_project(a,Json{{"op","effectUpsert"},{"effectId",a.effects[0].id},{"effect",edited}}.dump());
    expect(Json::parse(a.effects[0].parameters_json)["origin"]=="edited","manual update must protect automatic effect");
    auto preserved=edit_project(a,command.dump());expect(preserved.effects.size()==1 && preserved.effects[0].parameters_json==a.effects[0].parameters_json,"automatic regeneration overwrote authored changes");
    auto collision=p;collision.effects={effect("auto-zoom-8740000","zoom",{{"origin","edited"}},80'000'000,10'000'000)};
    auto regenerated=edit_project(collision,command.dump());expect(regenerated.effects.size()==2 && regenerated.effects[0].id!=regenerated.effects[1].id,"regeneration collided with moved authored ID");
    auto moved=edit_project(preserved,Json{{"op","effectAtOutput"},{"effectId",preserved.effects[0].id},{"outputTicks",0},{"edge","move"}}.dump());validate_project(moved);expect(normalized_ticks(moved.effects[0].source_start)==std::min(TimelineMap(p.source_duration,p.segments).source_time(0)->ticks,normalized_ticks(p.source_duration)-normalized_ticks(preserved.effects[0].source_duration)),"effect drag failed to map output to source and clamp at source end");
    rejects([&]{edit_project(preserved,Json{{"op","effectAtOutput"},{"effectId",preserved.effects[0].id},{"outputTicks",0},{"edge","unknown"}}.dump());},"unknown effect drag edge accepted");
    p.effects={effect("zoom-new","zoom",{{"scale",2.0},{"focusX",0.7},{"focusY",0.4},{"easing","spring"},{"enter",0.7},{"exit",0.7}})};
    FrameSceneEvaluator evaluator(p);auto reference=evaluator.evaluate(50'000'000)->visual.screen;
    expect(reference.scale>1.5,"trajectory test must sample an active zoom");
    auto sampled=evaluator.evaluate(50'000'000);auto rect=sampled->screen.output_rect;
    expect(std::abs(reference.focus_x-(rect.x+0.7*rect.width)/sampled->width)<0.000001,"zoom focus ignored screen padding and letterboxing");
    for(int i=0;i<75;++i)(void)evaluator.evaluate(i*666'667);auto low=evaluator.evaluate(50'000'000)->visual.screen;
    for(int i=0;i<300;++i)(void)evaluator.evaluate(i*166'667);auto high=evaluator.evaluate(50'000'000)->visual.screen;
    expect(reference.scale==low.scale && low.scale==high.scale && low.focus_x==high.focus_x,"preview history must not change trajectory");
    for(auto easing:{"linear","cubic","spring"}){expect(motion_curve(0,easing)==0 && motion_curve(1,easing)==1,"curve endpoints must be exact");for(int i=0;i<=100;++i)expect(std::isfinite(motion_curve(i/100.0,easing,6,0.3)),"spring produced nonfinite motion");}
    // Golden samples from DogSC ZoomInterpolator.springProgress with
    // MotionStyle(mass:2.4, stiffness:210, damping:42), normalized over 0.72s.
    const std::array<std::array<double,2>,6> dogsc{{{0.1,0.1503683906240969},{0.2,0.4046790524244621},{0.25,0.5225369497098733},{0.5,0.8805944634127069},{0.75,0.9809439492960286},{0.9,0.996208694702683}}};
    for(auto [time,value]:dogsc)expect(std::abs(motion_curve(time,"spring")-value)<1e-12,"spring differs from DogSC reference trajectory");
    expect(motion_curve(0.25,"cubic")<0.08 && motion_curve(0.75,"cubic")>0.92,"cubic must solve DogSC Bezier time, not use smoothstep");
    auto dogsc_zoom=parse_visual_effect(effect("reference-zoom","zoom",{{"scale",1.6},{"enter",0.7},{"exit",0.7},{"easing","spring"}}));
    expect(std::abs(dogsc_zoom.frequency-default_spring_frequency)<1e-12 && std::abs(dogsc_zoom.damping-default_spring_damping)<1e-12,"stored automatic effects without spring parameters must use DogSC defaults");
    for(auto [time,value]:dogsc) {
        auto entry=static_cast<int64_t>(std::llround(time*0.7*time_scale));
        auto returning=static_cast<int64_t>(std::llround((2.3+time*0.7)*time_scale));
        expect(std::abs(effect_envelope(dogsc_zoom,entry)-value)<1e-6,"zoom entry differs from reference timing");
        expect(std::abs(effect_envelope(dogsc_zoom,returning)-(1-value))<1e-6,"zoom return must ease forward from held scale");
    }
    expect(effect_envelope(dogsc_zoom,20'000'000)==1,"zoom must keep its held scale before returning");
    auto short_zoom=dogsc_zoom;short_zoom.duration=8'000'000;short_zoom.exit=0.1;
    expect(std::abs(effect_envelope(short_zoom,3'500'000)-motion_curve(0.5,"spring"))<1e-12,"short zoom must retain full entry window until hold ends");
    auto authored=parse_visual_effect(effect("custom-spring","zoom",{{"springFrequency",4.0},{"damping",0.6}}));
    expect(authored.frequency==4 && authored.damping==0.6,"explicitly authored spring tuning must be preserved");
    expect(evaluator.evaluate(0)->visual.screen.scale==1,"zoom must start neutral");
    auto empty=p;empty.effects.clear();auto stable=FrameSceneEvaluator(empty).evaluate(10'000'000);expect(stable->visual.screen.scale==1 && stable->visual.screen.opacity==1,"legacy projects must retain neutral motion");
    std::vector<FocusPoint> points{{0,0.2,0.2},{1'000'000,0.8,0.8}};auto smoothed=smooth_pointer(points,1'000'000,0.1);expect(smoothed[0]>0.2 && smoothed[0]<0.8,"cursor smoothing must preserve causal weighted position");
    auto bad=p;bad.effects={effect("mask-bad","mask",{{"x",0.9},{"width",0.5}})};rejects([&]{validate_project(bad);},"out-of-bounds mask accepted");
    bad.effects={effect("sticker-bad","sticker",{{"path","C:/external.png"}})};rejects([&]{validate_project(bad);},"absolute visual path accepted");
    bad.effects={effect("zoom-bad","zoom",{{"scale",9}})};rejects([&]{validate_project(bad);},"excessive zoom accepted");
    bad.effects={effect("mask-bad","mask",{{"mode","unknown"}})};rejects([&]{validate_project(bad);},"unknown masking mode accepted");
    bad.effects={effect("keys-bad","zoom",{{"focusPoints",Json::array({{{"ticks",-1},{"x",0.5},{"y",0.5}}})}})};rejects([&]{validate_project(bad);},"focus key outside effect accepted");
    bad.effects.clear();for(int i=0;i<32;++i)bad.effects.push_back(effect("region-"+std::to_string(i),"mask",Json::object()));validate_project(bad);
    bad.effects.push_back(effect("region-33","focus",Json::object()));rejects([&]{validate_project(bad);},"shader region overflow accepted");
    bad.effects.back().source_start=canonical_time(30'000'000);validate_project(bad);expect(true,"half-open mask intervals may touch without overlap");
    auto focus_effect=effect("focus-keys","zoom",{{"scale",2.0},{"enter",0.7},{"focusPoints",Json::array({{{"ticks",0},{"x",0.2},{"y",0.3}},{{"ticks",10'000'000},{"x",0.8},{"y",0.7}}})}});
    auto parsed=parse_visual_effect(focus_effect);auto at=evaluate_visuals({parsed},10'000'000).screen,just_before=evaluate_visuals({parsed},9'999'999).screen;
    expect(std::abs(at.focus_x-just_before.focus_x)<0.000001,"focus retarget must be continuous");
    p.effects={focus_effect};auto trimmed=edit_project(p,Json{{"op","effectAtOutput"},{"effectId","focus-keys"},{"outputTicks",60'000'000},{"edge","start"}}.dump());
    auto trimmed_start=normalized_ticks(trimmed.effects[0].source_start);auto expected_focus=evaluate_visuals({parsed},trimmed_start).screen;auto actual_focus=evaluate_visuals({parse_visual_effect(trimmed.effects[0])},trimmed_start).screen;
    expect(std::abs(expected_focus.focus_x-actual_focus.focus_x)<0.000001,"trimming must retain sampled focus at boundary");
    {
        std::ostringstream small;auto offset=frametrail::media::write_pcm_header(small,48000);
        std::istringstream input(small.str());expect(offset==44 && frametrail::media::pcm_data_offset(input)==44,"RIFF PCM keeps legacy layout");
        std::ostringstream large;offset=frametrail::media::write_pcm_header(large,7ULL*3600*48000);
        expect(large.str().substr(0,4)=="RF64" && large.str().size()==80,"long PCM uses bounded RF64 header");
        std::istringstream rf(large.str());expect(frametrail::media::pcm_data_offset(rf)==80 && offset==80,"RF64 data seek skips ds64");
        auto center=plane_vertex(0.5,0.5,PlaneMotion{},1920,1080);expect(std::abs(center[0])+std::abs(center[1])<1e-10 && center[2]==1,"identity projection center");
        PlaneMotion motion;motion.scale=2;motion.angle_x=15;motion.angle_y=-20;
        auto point=plane_vertex(0.2,0.7,motion,1920,1080);expect(std::isfinite(point[0]) && point[2]>0,"tilted zoom has finite hit geometry");
    }
    std::cout<<"effects checks="<<checks<<'\n';return 0;
}catch(const std::exception&e){std::cerr<<e.what()<<'\n';return 1;}}
