#include "frametrail/core/editor.hpp"
#include "frametrail/core/timeline.hpp"
#include <nlohmann/json.hpp>
#include <algorithm>
#include <limits>

namespace frametrail::core {
using Json = nlohmann::json;
namespace {
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
        const auto output = std::clamp(cmd.at("outputTicks").get<int64_t>(), resolved->output_start, resolved->output_end());
        const auto source = resolved->source_start + std::min(resolved->source_duration, multiply_ratio(output - resolved->output_start, resolved->rate.numerator, resolved->rate.denominator));
        if (cmd.at("edge").get<std::string>() == "start") { it->source_start = canonical_time(source); it->source_duration = canonical_time(resolved->source_end() - source); }
        else it->source_duration = canonical_time(source - resolved->source_start);
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
    } else if (op == "settings") {
        auto json = Json::parse(serialize_project(p));
        for (const auto* key : {"canvas", "export", "editor", "effects"}) if (cmd.contains(key)) json[key] = cmd[key];
        p = parse_project(json.dump());
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
    TimelineMap map(project.source_duration, project.segments); Json clips = Json::array(), markers = Json::array();
    for (auto& s : map.segments()) clips.push_back({{"id", s.id}, {"sourceStart", s.source_start}, {"sourceDuration", s.source_duration},
        {"outputStart", s.output_start}, {"outputDuration", s.output_duration}, {"numerator", s.rate.numerator}, {"denominator", s.rate.denominator}});
    for (auto& m : project.markers) if (auto mapped = map.output_time(normalized_ticks(m.time))) markers.push_back({{"id", m.id}, {"ticks", mapped->ticks}, {"label", m.label}});
    return Json{{"duration", map.output_duration()}, {"clips", clips}, {"markers", markers}}.dump();
}
}
