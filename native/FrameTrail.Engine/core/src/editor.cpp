#include "frametrail/core/editor.hpp"
#include "frametrail/core/timeline.hpp"
#include "frametrail/core/effects.hpp"
#include <nlohmann/json.hpp>
#include <algorithm>
#include <limits>

namespace frametrail::core {
using Json = nlohmann::json;
namespace {
Time parse_time_field(const Json& value, const char* path) {
    if (!value.is_object()) throw ValidationError(path, "expected time object");
    const auto& ticks_val = value.at("ticks");
    const auto& timescale_val = value.at("timescale");
    if (!ticks_val.is_number_integer() || !timescale_val.is_number_integer())
        throw ValidationError(path, "expected integer time fields");
    const auto ts = timescale_val.get<int64_t>();
    if (ts < 1 || ts > 1'000'000'000) throw ValidationError(path, "timescale outside allowed range");
    Time result{ticks_val.get<int64_t>(), static_cast<int32_t>(ts)};
    (void)normalized_ticks(result);
    return result;
}
int64_t parse_int_field(const Json& obj, const char* key, int64_t min_val, int64_t max_val, const char* path) {
    const auto& v = obj.at(key);
    if (!v.is_number_integer() || (v.is_number_unsigned() && v.get<uint64_t>() > static_cast<uint64_t>((std::numeric_limits<int64_t>::max)())))
        throw ValidationError(path, "expected signed 64-bit integer");
    const auto result = v.get<int64_t>();
    if (result < min_val || result > max_val) throw ValidationError(path, "integer outside allowed range");
    return result;
}
void apply_command(Project& p, const Json& cmd) {
    const auto op = cmd.at("op").get<std::string>();
    auto find = [&]() {
        const auto id = cmd.at("segmentId").get<std::string>();
        auto it = std::find_if(p.segments.begin(), p.segments.end(), [&](auto& s) { return s.id == id; });
        if (it == p.segments.end()) throw ValidationError("command.segmentId", "clip no longer exists");
        return it;
    };
    if (op == "split") {
        auto it = find(); const auto start = normalized_ticks(it->source_start), duration = normalized_ticks(it->source_duration);
        const auto cut = cmd.at("sourceTicks").get<int64_t>();
        if (cut <= start || cut >= start + duration) throw ValidationError("split", "split must be inside clip");
        auto right = *it; right.id = cmd.at("newId").get<std::string>();
        right.source_start = canonical_time(cut); right.source_duration = canonical_time(start + duration - cut);
        auto settings = Json::parse(p.editor_json);
        if (settings.contains("clipAudio") && settings["clipAudio"].contains(it->id)) { settings["clipAudio"][right.id] = settings["clipAudio"][it->id]; p.editor_json = settings.dump(); }
        it->source_duration = canonical_time(cut - start); p.segments.insert(it + 1, std::move(right));
    } else if (op == "trimOutput") {
        auto it = find(); TimelineMap timeline(p.source_duration, p.segments);
        const auto resolved = std::find_if(timeline.segments().begin(), timeline.segments().end(), [&](auto& s) { return s.id == it->id; });
        const auto full_duration = normalized_ticks(p.source_duration);
        int64_t min_source_start = 0, max_source_end = full_duration;
        for (const auto& m : p.media) {
            if (m.kind != MediaKind::screen || !m.path.starts_with("media/import-")) continue;
            const auto m_start = normalized_ticks(m.start_offset);
            const auto m_end = m_start + normalized_ticks(m.duration);
            if (resolved->source_start >= m_start && resolved->source_end() <= m_end) {
                min_source_start = std::max(min_source_start, m_start);
                max_source_end = std::min(max_source_end, m_end);
            } else {
                if (m_end <= resolved->source_start) min_source_start = std::max(min_source_start, m_end);
                if (m_start >= resolved->source_end()) max_source_end = std::min(max_source_end, m_start);
            }
        }
        for (const auto& segment : timeline.segments()) {
            if (segment.id == resolved->id) continue;
            if (segment.source_end() <= resolved->source_start) min_source_start = std::max(min_source_start, segment.source_end());
            if (segment.source_start >= resolved->source_end()) max_source_end = std::min(max_source_end, segment.source_start);
        }
        const auto min_source_duration = std::min(resolved->source_duration, std::max<int64_t>(100'000, multiply_ratio(10'000, resolved->rate.numerator, resolved->rate.denominator)));
        const auto max_output_delta = multiply_ratio(full_duration, resolved->rate.denominator, resolved->rate.numerator) + time_scale;
        const auto clamped_output_delta = std::clamp(cmd.at("outputTicks").get<int64_t>() - resolved->output_start, -max_output_delta, max_output_delta);
        const auto raw_source = resolved->source_start + multiply_ratio(clamped_output_delta, resolved->rate.numerator, resolved->rate.denominator);
        if (cmd.at("edge").get<std::string>() == "start") {
            const auto source = std::clamp(raw_source, min_source_start, resolved->source_end() - min_source_duration);
            it->source_start = canonical_time(source);
            it->source_duration = canonical_time(resolved->source_end() - source);
        } else {
            const auto source = std::clamp(raw_source, resolved->source_start + min_source_duration, max_source_end);
            it->source_duration = canonical_time(source - resolved->source_start);
        }
    } else if (op == "trim") {
        auto it = find(); const auto start = cmd.at("sourceStart").get<int64_t>(), end = cmd.at("sourceEnd").get<int64_t>();
        if (start < normalized_ticks(it->source_start) || end > normalized_ticks(it->source_start) + normalized_ticks(it->source_duration) || end <= start)
            throw ValidationError("trim", "trim must retain a nonempty subset of clip");
        it->source_start = canonical_time(start); it->source_duration = canonical_time(end - start);
    } else if (op == "delete") {
        if (p.segments.size() == 1) throw ValidationError("delete", "keep at least one clip");
        p.segments.erase(find());
    } else if (op == "move") {
        auto it = find(); auto value = *it; auto index = cmd.at("index").get<int64_t>();
        if (index < 0 || index >= static_cast<int64_t>(p.segments.size())) throw ValidationError("move", "invalid target index");
        p.segments.erase(it); p.segments.insert(p.segments.begin() + index, std::move(value));
    } else if (op == "rate") {
        auto it = find(); it->rate = {cmd.at("numerator").get<int32_t>(), cmd.at("denominator").get<int32_t>()};
    } else if (op == "restore") {
        p.segments.clear(); auto prefix = cmd.at("idPrefix").get<std::string>();
        std::vector<Media> screens; for (auto& m : p.media) if (m.kind == MediaKind::screen) screens.push_back(m);
        std::sort(screens.begin(), screens.end(), [](auto& a, auto& b) { return normalized_ticks(a.start_offset) < normalized_ticks(b.start_offset); });
        for (size_t i = 0; i < screens.size(); ++i) p.segments.push_back({prefix + std::to_string(i), screens[i].start_offset, screens[i].duration, {1, 1}});
    } else if (op == "effectAtOutput") {
        auto id=cmd.at("effectId").get<std::string>();auto found=std::find_if(p.effects.begin(),p.effects.end(),[&](auto&e){return e.id==id;});
        if(found==p.effects.end())throw ValidationError("effectId","effect no longer exists");
        TimelineMap map(p.source_duration,p.segments);auto mapped=map.source_time(std::clamp(cmd.at("outputTicks").get<int64_t>(),int64_t(0),map.output_duration()-1));
        auto start=normalized_ticks(found->source_start),end=start+normalized_ticks(found->source_duration),old=start,total=normalized_ticks(p.source_duration);auto edge=cmd.value("edge",std::string("move"));
        if(edge!="start" && edge!="end" && edge!="move")throw ValidationError("edge","expected start, end or move");
        if(edge=="start")start=std::clamp(mapped->ticks,int64_t(0),end-1);
        else if(edge=="end")end=std::clamp(mapped->ticks,start+1,total);
        else {auto length=end-start;start=std::clamp(mapped->ticks,int64_t(0),total-length);end=start+length;}
        auto parameters=Json::parse(found->parameters_json);parameters["origin"]="edited";parameters.erase("plannerState");
        if(parameters.contains("focusPoints")){
            Json keys=Json::array();
            if(edge=="start" && !parameters["focusPoints"].empty()) {
                auto visual=parse_visual_effect(*found);
                auto next=std::upper_bound(visual.focus_points.begin(),visual.focus_points.end(),start,[](auto t,auto& point){return t<point.ticks;});
                if(next!=visual.focus_points.begin()) {
                    auto key=std::prev(next);
                    keys.push_back({{"ticks",start},{"x",key->x},{"y",key->y},{"fromX",key->from_x},{"fromY",key->from_y},{"phaseTicks",std::min<int64_t>(5*time_scale,start-key->transition_start)}});
                } else {
                    auto sampled=evaluate_visuals({visual},start).screen;
                    keys.push_back({{"ticks",start},{"x",sampled.focus_x},{"y",sampled.focus_y}});
                }
            }
            for(auto key:parameters["focusPoints"]){auto delta=edge=="move"?start-old:0;auto tick=key["ticks"].get<int64_t>()+delta;if(tick>=start && tick<end && (keys.empty() || tick>keys.back()["ticks"].get<int64_t>())){key["ticks"]=tick;keys.push_back(key);}}
            parameters["focusPoints"]=keys;
        }
        found->source_start=canonical_time(start);found->source_duration=canonical_time(end-start);found->parameters_json=parameters.dump();
    } else if (op == "autoZoom") {
        generate_auto_zoom(p,cmd.dump());
    } else if (op == "autoZoomAppend") {
        append_auto_zoom(p,cmd.dump());
    } else if (op == "effectUpsert" || op == "effectDelete") {
        auto json=Json::parse(serialize_project(p)); auto id=cmd.at("effectId").get<std::string>(); auto& effects=json["effects"];
        auto found=std::find_if(effects.begin(),effects.end(),[&](auto&e){return e["id"]==id;});
        if(op=="effectDelete") { if(found==effects.end())throw ValidationError("effectId","effect no longer exists"); effects.erase(found); }
        else { auto value=cmd.at("effect"); value["id"]=id; if(found!=effects.end()) { value["parameters"]["origin"]="edited"; value["parameters"].erase("plannerState"); *found=value; } else effects.push_back(value); }
        p=parse_project(json.dump());
    } else if (op == "settings") {
        auto json = Json::parse(serialize_project(p));
        for (const auto* key : {"canvas", "export", "editor", "effects"}) if (cmd.contains(key)) json[key] = cmd[key];
        p = parse_project(json.dump());
    } else if (op == "appendMedia") {
        const auto old_duration = normalized_ticks(p.source_duration);
        const auto& screen_json = cmd.at("screenMedia");
        if (!screen_json.is_object()) throw ValidationError("appendMedia.screenMedia", "expected object");
        const auto clip_duration = normalized_ticks(parse_time_field(screen_json.at("duration"), "appendMedia.screenMedia.duration"));
        if (clip_duration <= 0) throw ValidationError("appendMedia.screenMedia.duration", "duration must be positive");
        if (clip_duration > maximum_source_ticks - old_duration) throw ValidationError("appendMedia", "source duration exceeds maximum");

        Media screen;
        screen.id = screen_json.at("id").get<std::string>();
        screen.kind = MediaKind::screen;
        screen.path = screen_json.at("path").get<std::string>();
        screen.duration = canonical_time(clip_duration);
        screen.start_offset = canonical_time(old_duration);
        screen.width = static_cast<uint32_t>(parse_int_field(screen_json, "width", 1, 32768, "appendMedia.screenMedia.width"));
        screen.height = static_cast<uint32_t>(parse_int_field(screen_json, "height", 1, 32768, "appendMedia.screenMedia.height"));
        p.media.push_back(std::move(screen));

        if (cmd.contains("audioMedia") && !cmd["audioMedia"].is_null()) {
            const auto& audio_json = cmd["audioMedia"];
            if (!audio_json.is_object()) throw ValidationError("appendMedia.audioMedia", "expected object");
            const auto audio_duration = normalized_ticks(parse_time_field(audio_json.at("duration"), "appendMedia.audioMedia.duration"));
            if (audio_duration <= 0) throw ValidationError("appendMedia.audioMedia.duration", "duration must be positive");
            if (audio_duration > maximum_source_ticks - old_duration) throw ValidationError("appendMedia", "audio duration exceeds maximum");

            Media audio;
            audio.id = audio_json.at("id").get<std::string>();
            audio.kind = MediaKind::system_audio;
            audio.path = audio_json.at("path").get<std::string>();
            audio.duration = canonical_time(audio_duration);
            audio.start_offset = canonical_time(old_duration);
            audio.sample_rate = static_cast<uint32_t>(parse_int_field(audio_json, "sampleRate", 8000, 384000, "appendMedia.audioMedia.sampleRate"));
            audio.channels = static_cast<uint32_t>(parse_int_field(audio_json, "channels", 1, 32, "appendMedia.audioMedia.channels"));
            p.media.push_back(std::move(audio));
        }

        p.source_duration = canonical_time(old_duration + clip_duration);
        Segment segment{cmd.at("segmentId").get<std::string>(), canonical_time(old_duration), canonical_time(clip_duration), {1, 1}};
        if (cmd.contains("insertIndex") && !cmd["insertIndex"].is_null()) {
            const auto insert_index = parse_int_field(cmd, "insertIndex", 0, static_cast<int64_t>(p.segments.size()), "appendMedia.insertIndex");
            p.segments.insert(p.segments.begin() + insert_index, std::move(segment));
        } else {
            p.segments.push_back(std::move(segment));
        }

        if (p.schema_version == 2 && !p.primary_source_json.empty()) {
            auto primary = Json::parse(p.primary_source_json);
            if (primary.value("kind", std::string{}) == "androidUsb") {
                primary["kind"] = "desktop";
                p.primary_source_json = primary.dump();
                for (auto& m : p.media) {
                    if (m.kind == MediaKind::android_audio) m.kind = MediaKind::system_audio;
                }
            }
        }
    } else if (op == "batch") {
        if (!cmd.at("commands").is_array() || cmd["commands"].size() > 100) throw ValidationError("batch", "invalid command count");
        for (const auto& c : cmd["commands"]) { if (c.value("op", "") == "batch") throw ValidationError("batch", "nested batches are disallowed"); apply_command(p, c); }
    } else throw ValidationError("command.op", "unsupported edit command");
}
}
Project edit_project(const Project& project, std::string_view text) {
    if (text.empty() || text.size() > 16 * 1024 * 1024) throw ValidationError("command", "invalid command size");
    if (project.revision == (std::numeric_limits<int64_t>::max)()) throw ValidationError("revision", "revision exhausted");
    try {
        auto result = project; apply_command(result, Json::parse(text)); result.revision = project.revision + 1; validate_project(result); return result;
    } catch (const Json::exception& e) { throw ValidationError("command", e.what()); }
}
std::string timeline_json(const Project& project) {
    TimelineMap map(project.source_duration, project.segments); Json clips = Json::array(), markers = Json::array(), effects = Json::array();
    for (auto& s : map.segments()) clips.push_back({{"id", s.id}, {"sourceStart", s.source_start}, {"sourceDuration", s.source_duration},
        {"outputStart", s.output_start}, {"outputDuration", s.output_duration}, {"numerator", s.rate.numerator}, {"denominator", s.rate.denominator}});
    for (auto& m : project.markers) if (auto mapped = map.output_time(normalized_ticks(m.time))) markers.push_back({{"id", m.id}, {"ticks", mapped->ticks}, {"label", m.label}});
    std::vector<const ResolvedSegment*> ordered;
    for(auto&s:map.segments())ordered.push_back(&s);
    std::sort(ordered.begin(),ordered.end(),[](auto a,auto b){return a->source_start<b->source_start;});
    std::vector<int64_t> maximum(ordered.size()*4);
    auto build=[&](auto&& self,size_t node,size_t lo,size_t hi)->int64_t {
        if(hi-lo==1)return maximum[node]=ordered[lo]->source_end();
        auto mid=lo+(hi-lo)/2;return maximum[node]=std::max(self(self,node*2,lo,mid),self(self,node*2+1,mid,hi));
    };
    if(!ordered.empty())build(build,1,0,ordered.size());
    for(auto&e:project.effects) {
      auto first=normalized_ticks(e.source_start),last=first+normalized_ticks(e.source_duration);
      auto visit=[&](auto&& self,size_t node,size_t lo,size_t hi)->void {
        if(maximum[node]<=first || ordered[lo]->source_start>=last)return;
        if(hi-lo>1){auto mid=lo+(hi-lo)/2;self(self,node*2,lo,mid);self(self,node*2+1,mid,hi);return;}
        auto&s=*ordered[lo];
        auto start=std::max(normalized_ticks(e.source_start),s.source_start), end=std::min(normalized_ticks(e.source_start)+normalized_ticks(e.source_duration),s.source_end());
        if(end<=start)return;
        auto out=s.output_start+multiply_ratio(start-s.source_start,s.rate.denominator,s.rate.numerator);
        auto finish=s.output_start+multiply_ratio(end-s.source_start,s.rate.denominator,s.rate.numerator);
        effects.push_back({{"id",e.id},{"kind",e.kind},{"outputStart",out},{"outputDuration",std::max(int64_t(1),finish-out)}});
        if(effects.size()>1000000)throw ValidationError("effects","too many resolved effect ranges");
      };
      if(!ordered.empty())visit(visit,1,0,ordered.size());
    }
    return Json{{"duration", map.output_duration()}, {"clips", clips}, {"markers", markers}, {"effects",effects}}.dump();
}
}
