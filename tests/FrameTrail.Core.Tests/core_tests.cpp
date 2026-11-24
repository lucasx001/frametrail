#include "frametrail/core/project.hpp"
#include "frametrail/core/editor.hpp"
#include "frametrail/core/scene.hpp"
#include <nlohmann/json.hpp>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <limits>
#include <sstream>

using namespace frametrail::core;
using Json = nlohmann::json;
namespace {
int checks = 0;
void check(bool condition, const char* label) {
    ++checks;
    if (!condition) throw std::runtime_error(label);
}
void close(double actual, double expected, const char* label) { check(std::abs(actual - expected) < 0.000001, label); }
template<typename F> void rejects(F&& function, const char* label) {
    ++checks;
    try { function(); } catch (const ValidationError&) { return; }
    throw std::runtime_error(std::string("Expected rejection: ") + label);
}
std::string read(const std::filesystem::path& path) {
    std::ifstream file(path, std::ios::binary);
    if (!file) throw std::runtime_error("Cannot read " + path.string());
    std::ostringstream out; out << file.rdbuf(); return out.str();
}
Time seconds(std::int64_t value) { return {value, 1}; }
void mutate(Json& root, const Json& test) {
    auto* node = &root;
    const auto& path = test["path"];
    for (std::size_t index = 0; index + 1 < path.size(); ++index)
        node = path[index].is_string() ? &(*node)[path[index].get<std::string>()] : &(*node)[path[index].get<std::size_t>()];
    const auto& last = path.back();
    if (test.value("remove", false)) node->erase(last.get<std::string>());
    else if (last.is_string()) (*node)[last.get<std::string>()] = test["value"];
    else (*node)[last.get<std::size_t>()] = test["value"];
}
void test_protocol(const std::string& text) {
    const auto project = parse_project(text);
    check(project.schema_version == 1 && project.revision == 7, "schema and revision");
    const auto serialized = serialize_project(project);
    check(parse_project(serialized).segments.size() == 3, "roundtrip project");
    check(Json::parse(serialized)["effects"][0]["parameters"]["futureEditableData"]["preserve"] == true, "retain parameter extensions");
    const auto baseline = Json::parse(text);
    const auto cases = Json::parse(read(std::filesystem::path(FRAMETRAIL_FIXTURES) / "protocol-invalid-cases.json"));
    for (const auto& test : cases) {
        auto invalid = baseline;
        mutate(invalid, test);
        rejects([&] { (void)parse_project(invalid.dump()); }, test["name"].get<std::string>().c_str());
    }
    rejects([&] { (void)parse_project("{\"schemaVersion\":1,\"schemaVersion\":1}"); }, "duplicate keys");
    rejects([&] { (void)parse_project("{\"schemaVersion\":NaN}"); }, "NaN JSON");
    rejects([&] { (void)parse_project("[]"); }, "array root");
    auto invalid = baseline;
    invalid["segments"][1]["sourceStart"] = {{"ticks", 7}, {"timescale", 1}};
    rejects([&] { (void)parse_project(invalid.dump()); }, "overlap after reorder");
    invalid = baseline; invalid["media"][1]["startOffset"]["ticks"] = 7;
    rejects([&] { (void)parse_project(invalid.dump()); }, "media gap");
    invalid = baseline; invalid["media"][2]["startOffset"]["ticks"] = 10;
    rejects([&] { (void)parse_project(invalid.dump()); }, "camera outside source");
    invalid = baseline; invalid["segments"][0]["rate"]["numerator"] = 5;
    rejects([&] { (void)parse_project(invalid.dump()); }, "rate outside 0.25..4");
    auto direct = project; direct.canvas.padding = std::numeric_limits<double>::infinity();
    rejects([&] { validate_project(direct); }, "nonfinite in-memory value");
    direct = project; direct.canvas.background.a = std::numeric_limits<double>::quiet_NaN();
    rejects([&] { (void)serialize_project(direct); }, "NaN cannot serialize to null");
    direct = project; direct.media[0].width = 0;
    rejects([&] { validate_project(direct); }, "video zero dimension");
    direct = project; direct.effects[0].source_duration = seconds(20);
    rejects([&] { validate_project(direct); }, "effect outside range");
    for (const auto* path : {"/absolute", "../outside", "media/../outside", "media\\video.mp4", "media/NUL", "media/LPT1.txt", "media/stream:ads", "media/trailing.", "media/./file"})
        check(!is_project_relative_path(path), "unsafe Windows path");
    check(is_project_relative_path("media/screen-1.mp4"), "safe relative path");
    check(is_project_relative_path("assets/背景.png"), "Unicode relative path");
}

void test_timeline(const Project& project) {
    const TimelineMap map(project.source_duration, project.segments);
    const auto vectors = Json::parse(read(std::filesystem::path(FRAMETRAIL_FIXTURES) / "mapping-vectors.json"));
    check(map.output_duration() == vectors["outputDurationTicks"].get<std::int64_t>(), "output duration 11 seconds");
    for (const auto& sample : vectors["outputToSource"]) {
        const auto actual = map.source_time(sample["outputTicks"]);
        if (sample["sourceTicks"].is_null()) check(!actual, "unmapped output time");
        else check(actual && actual->ticks == sample["sourceTicks"].get<std::int64_t>() && actual->segment_id == sample["segmentId"].get<std::string>(), "output to source vector");
    }
    for (const auto& sample : vectors["sourceToOutput"]) {
        const auto actual = map.output_time(sample["sourceTicks"]);
        if (sample["outputTicks"].is_null()) check(!actual, "deleted or outside source time");
        else check(actual && actual->ticks == sample["outputTicks"].get<std::int64_t>() && actual->segment_id == sample["segmentId"].get<std::string>(), "source to output vector");
    }
    check(map.source_time(map.output_duration(), EndpointPolicy::include_final_endpoint)->ticks == 6 * time_scale, "explicit final endpoint follows last output clip");
    check(map.output_time(6 * time_scale, EndpointPolicy::include_final_endpoint)->ticks == 0, "cut ownership wins over optional final endpoint");
    check(map.source_time(time_scale - 1)->ticks < 10 * time_scale, "fast final tick stays half open");
    check(map.source_time(9 * time_scale - 1)->ticks < 2 * time_scale, "slow final tick stays half open");
    check(map.segment_at(time_scale)->id == "earlier-slow", "cut instant belongs next output clip");
    check(normalized_ticks({1, 3}) == 3'333'333, "nearest positive time conversion");
    check(normalized_ticks({1, 20'000'000}) == 1, "half tick away from zero");
    check(normalized_ticks({-1, 20'000'000}) == -1, "negative half tick away from zero");
    check(multiply_ratio(3, 1, 2) == 2, "positive rational tie rounding");
    check(multiply_ratio(-3, 1, 2) == -2, "negative rational tie rounding");
    check(normalized_ticks({604800000000000, 1'000'000'000}) == maximum_source_ticks, "seven day boundary without overflow");
    rejects([] { (void)normalized_ticks({604800000000001, 1'000'000'000}); }, "raw time beyond seven days");
    rejects([] { (void)normalized_ticks({1, 0}); }, "zero timescale");
    rejects([] { (void)multiply_ratio((std::numeric_limits<std::int64_t>::max)(), 4, 1); }, "multiplication overflow");
    rejects([] { (void)TimelineMap(seconds(10), {}); }, "empty edit");
    rejects([] { (void)TimelineMap(seconds(10), {{"zero", seconds(0), seconds(0), {1, 1}}}); }, "zero duration");
    rejects([] { (void)TimelineMap(seconds(10), {{"subtick", seconds(0), canonical_time(1), {4, 1}}}); }, "zero quantized output duration");
    const TimelineMap fractional(seconds(10), {{"fractional", seconds(0), seconds(3), {3, 2}}});
    check(fractional.output_duration() == 2 * time_scale && fractional.source_time(time_scale)->ticks == 15'000'000, "noninteger rational rate");
    const TimelineMap longest(canonical_time(maximum_source_ticks), {{"longest", seconds(0), canonical_time(maximum_source_ticks), {1, 4}}});
    check(longest.output_duration() == maximum_source_ticks * 4, "maximum slow duration checked integer arithmetic");
    // Many deterministic instants verify inverse mapping to <= 2 canonical ticks,
    // including rounding at noninteger rates. Endpoints are separately specified.
    const TimelineMap inverse(seconds(10), {{"fractional", seconds(0), seconds(10), {3, 2}}});
    for (std::int64_t ticks = 0; ticks < 10 * time_scale - 10; ticks += 7919) {
        const auto output = inverse.output_time(ticks);
        check(output.has_value(), "inverse output exists");
        const auto source = inverse.source_time(output->ticks);
        check(source && std::abs(source->ticks - ticks) <= 1, "inverse source within rounding tolerance");
    }
}

void test_pointer(const Project& project) {
    const TimelineMap map(project.source_duration, project.segments);
    const std::vector<PointerEvent> events{
        {7 * time_scale, .9, .9, PointerKind::left_down, "arrow"},
        {0, .1, .1, PointerKind::move, "arrow"},
        {time_scale, .2, .2, PointerKind::left_down, "hand"},
        {3 * time_scale, .8, .8, PointerKind::left_down, "arrow"}, // deleted interval; only position may seed next clip
        {5 * time_scale, .85, .85, PointerKind::move, "arrow"},
        {6 * time_scale, .88, .88, PointerKind::right_down, "arrow"},
        {6 * time_scale, .89, .89, PointerKind::right_up, "arrow"}
    };
    const auto groups = map.map_pointer_events(events);
    check(groups.size() == 3, "one pointer group per edit segment");
    check(groups[0].events.front().kind == PointerKind::right_down && groups[0].events[1].kind == PointerKind::right_up, "equal-time stable ordering");
    check(groups[1].events.back().ticks == 5 * time_scale, "slow pointer time mapping");
    check(groups[2].events.front().ticks == 9 * time_scale && groups[2].events.front().kind == PointerKind::move, "position seed never duplicates click");
    close(groups[2].events.front().x, .8, "seed latest source position before clip");
    const auto before_cut = sample_pointer(groups[1], 9 * time_scale - 1);
    close(before_cut->x, .2, "no interpolation across deleted interval");
    check(!sample_pointer(groups[1], 9 * time_scale), "pointer sample cannot cross clip end");
    close(sample_pointer(groups[2], 9 * time_scale)->x, .8, "next clip starts independently");
    check(map.map_pointer_events({})[0].events.empty(), "empty pointer input retains boundaries");
    rejects([&] { (void)map.map_pointer_events({{0, std::numeric_limits<double>::quiet_NaN(), 0}}); }, "nonfinite pointer");
    rejects([&] { (void)map.map_pointer_events({{-1, 0, 0}}); }, "negative pointer time");
}

void test_scene(const Project& project) {
    FrameSceneEvaluator evaluator(project);
    const auto scene = evaluator.evaluate(0);
    check(scene && scene->revision == 7 && scene->segment_id == "later-fast", "scene revision and clip");
    check(scene->screen.media_id == "screen-2" && scene->screen.local_ticks == 0, "screen segmented media local clock");
    close(scene->screen.output_rect.width, 1728, "screen aspect fit width");
    close(scene->screen.output_rect.height, 972, "screen aspect fit height");
    close(scene->screen.output_rect.x, 96, "screen centered X");
    close(scene->screen.output_rect.y, 54, "screen centered Y");
    check(scene->camera && scene->camera->local_ticks == 4 * time_scale && scene->camera->mirror, "camera offset and mirror");
    check(!evaluator.evaluate(time_scale)->camera, "camera not invented before its start");
    check(!evaluator.evaluate(evaluator.timeline().output_duration()), "scene final endpoint excluded");
    check(!evaluator.evaluate(-1), "scene negative excluded");
}

void test_append_media(const Project& project) {
    // 1. Single append (no audioMedia, no insertIndex)
    const Json append_one{
        {"op", "appendMedia"},
        {"screenMedia", {
            {"id", "screen-import-1"},
            {"path", "media/import-1.mp4"},
            {"duration", {{"ticks", 5 * time_scale}, {"timescale", time_scale}}},
            {"width", 1280},
            {"height", 720}
        }},
        {"segmentId", "clip-import-1"}
    };
    const auto appended = edit_project(project, append_one.dump());
    check(appended.revision == project.revision + 1, "appendMedia increments revision");
    check(normalized_ticks(appended.source_duration) == 15 * time_scale, "appendMedia extends sourceDuration");
    check(appended.media.back().id == "screen-import-1" && appended.media.back().kind == MediaKind::screen, "appendMedia adds screen media");
    check(normalized_ticks(appended.media.back().start_offset) == 10 * time_scale, "appendMedia sets screen startOffset");
    check(normalized_ticks(appended.media.back().duration) == 5 * time_scale, "appendMedia sets screen duration");
    check(appended.media.back().width == 1280 && appended.media.back().height == 720, "appendMedia sets screen dimensions");
    check(appended.segments.size() == 4 && appended.segments.back().id == "clip-import-1", "appendMedia appends segment");
    check(normalized_ticks(appended.segments.back().source_start) == 10 * time_scale &&
          normalized_ticks(appended.segments.back().source_duration) == 5 * time_scale &&
          appended.segments.back().rate.numerator == 1 && appended.segments.back().rate.denominator == 1, "appendMedia segment fields");

    // 2. Append with insertIndex and audioMedia
    const Json append_with_audio{
        {"op", "appendMedia"},
        {"screenMedia", {
            {"id", "screen-import-2"},
            {"path", "media/import-2.mp4"},
            {"duration", {{"ticks", 3 * time_scale}, {"timescale", time_scale}}},
            {"width", 1920},
            {"height", 1080}
        }},
        {"audioMedia", {
            {"id", "audio-import-2"},
            {"path", "media/import-2.mp4"},
            {"duration", {{"ticks", 3 * time_scale}, {"timescale", time_scale}}},
            {"sampleRate", 44100},
            {"channels", 2}
        }},
        {"segmentId", "clip-import-2"},
        {"insertIndex", 1}
    };
    const auto with_audio = edit_project(appended, append_with_audio.dump());
    check(normalized_ticks(with_audio.source_duration) == 18 * time_scale, "appendMedia with audio extends sourceDuration");
    check(with_audio.segments.size() == 5 && with_audio.segments[1].id == "clip-import-2", "appendMedia inserts segment at insertIndex");
    check(normalized_ticks(with_audio.segments[1].source_start) == 15 * time_scale, "inserted segment points to new source offset");
    const auto audio_it = std::find_if(with_audio.media.begin(), with_audio.media.end(), [](const Media& m) { return m.id == "audio-import-2"; });
    check(audio_it != with_audio.media.end() && audio_it->kind == MediaKind::system_audio, "audioMedia added as systemAudio");
    check(normalized_ticks(audio_it->start_offset) == 15 * time_scale && audio_it->sample_rate == 44100 && audio_it->channels == 2, "audioMedia metadata");

    rejects([&] {
        auto bad = append_one;
        bad["insertIndex"] = -1;
        (void)edit_project(project, bad.dump());
    }, "appendMedia rejects negative insertIndex");
    rejects([&] {
        auto bad = append_one;
        bad["insertIndex"] = 99;
        (void)edit_project(project, bad.dump());
    }, "appendMedia rejects out-of-range insertIndex");
    rejects([&] {
        auto bad = append_one;
        bad["screenMedia"]["duration"]["ticks"] = 0;
        (void)edit_project(project, bad.dump());
    }, "appendMedia rejects zero duration");

    // 3. Batch append
    const Json batch_cmd{
        {"op", "batch"},
        {"commands", Json::array({
            {
                {"op", "appendMedia"},
                {"screenMedia", {
                    {"id", "screen-batch-1"},
                    {"path", "media/import-batch-1.mp4"},
                    {"duration", {{"ticks", 2 * time_scale}, {"timescale", time_scale}}},
                    {"width", 1920},
                    {"height", 1080}
                }},
                {"segmentId", "clip-batch-1"}
            },
            {
                {"op", "appendMedia"},
                {"screenMedia", {
                    {"id", "screen-batch-2"},
                    {"path", "media/import-batch-2.mp4"},
                    {"duration", {{"ticks", 4 * time_scale}, {"timescale", time_scale}}},
                    {"width", 1280},
                    {"height", 720}
                }},
                {"audioMedia", {
                    {"id", "audio-batch-2"},
                    {"path", "media/import-batch-2.mp4"},
                    {"duration", {{"ticks", 4 * time_scale}, {"timescale", time_scale}}},
                    {"sampleRate", 48000},
                    {"channels", 1}
                }},
                {"segmentId", "clip-batch-2"},
                {"insertIndex", 0}
            }
        })}
    };
    const auto batched = edit_project(project, batch_cmd.dump());
    check(normalized_ticks(batched.source_duration) == 16 * time_scale, "batch appendMedia total sourceDuration");
    check(batched.segments.size() == 5 && batched.segments.front().id == "clip-batch-2" && batched.segments.back().id == "clip-batch-1", "batch appendMedia segment order");

    // 4. Cross-source trimOutput boundary protection for media/import- clips
    auto isolated_recorded = edit_project(appended, Json{{"op", "batch"}, {"commands", Json::array({
        {{"op", "delete"}, {"segmentId", "earlier-slow"}},
        {{"op", "delete"}, {"segmentId", "middle-normal"}},
        {{"op", "delete"}, {"segmentId", "clip-import-1"}}
    })}}.dump());
    check(isolated_recorded.segments.size() == 1 && isolated_recorded.segments[0].id == "later-fast", "isolated recorded segment");
    // Recorded checkpoint segments (screen-1/screen-2) can still trim across checkpoint boundary, but cannot enter media/import-1.mp4 (10..15s)
    auto across_checkpoint = edit_project(isolated_recorded, Json{{"op", "trimOutput"}, {"segmentId", "later-fast"}, {"edge", "start"}, {"outputTicks", -time_scale}}.dump());
    check(normalized_ticks(across_checkpoint.segments[0].source_start) == 2 * time_scale, "recorded segment trims across checkpoint boundary");
    auto blocked_at_import = edit_project(isolated_recorded, Json{{"op", "trimOutput"}, {"segmentId", "later-fast"}, {"edge", "end"}, {"outputTicks", 5 * time_scale}}.dump());
    check(normalized_ticks(blocked_at_import.segments[0].source_start) + normalized_ticks(blocked_at_import.segments[0].source_duration) == 10 * time_scale, "recorded segment cannot trim into imported media");

    auto isolated_import = edit_project(with_audio, Json{{"op", "batch"}, {"commands", Json::array({
        {{"op", "delete"}, {"segmentId", "later-fast"}},
        {{"op", "delete"}, {"segmentId", "earlier-slow"}},
        {{"op", "delete"}, {"segmentId", "middle-normal"}},
        {{"op", "delete"}, {"segmentId", "clip-import-2"}}
    })}}.dump());
    check(isolated_import.segments.size() == 1 && isolated_import.segments[0].id == "clip-import-1", "isolated imported segment");
    // Imported clip (10..15s) cannot expand left into recorded media (0..10s) or right into import-2 (15..18s)
    auto import_trimmed_left = edit_project(isolated_import, Json{{"op", "trimOutput"}, {"segmentId", "clip-import-1"}, {"edge", "start"}, {"outputTicks", -4 * time_scale}}.dump());
    check(normalized_ticks(import_trimmed_left.segments[0].source_start) == 10 * time_scale, "imported clip start clamped to its own start");
    auto import_trimmed_right = edit_project(isolated_import, Json{{"op", "trimOutput"}, {"segmentId", "clip-import-1"}, {"edge", "end"}, {"outputTicks", 10 * time_scale}}.dump());
    check(normalized_ticks(import_trimmed_right.segments[0].source_start) + normalized_ticks(import_trimmed_right.segments[0].source_duration) == 15 * time_scale, "imported clip end clamped to its own end");
    auto import_trimmed_in = edit_project(isolated_import, Json{{"op", "trimOutput"}, {"segmentId", "clip-import-1"}, {"edge", "start"}, {"outputTicks", time_scale}}.dump());
    check(normalized_ticks(import_trimmed_in.segments[0].source_start) == 11 * time_scale, "imported clip inward trim works");
    auto import_expanded_left = edit_project(import_trimmed_in, Json{{"op", "trimOutput"}, {"segmentId", "clip-import-1"}, {"edge", "start"}, {"outputTicks", -3 * time_scale}}.dump());
    check(normalized_ticks(import_expanded_left.segments[0].source_start) == 10 * time_scale, "imported clip expand left stops at its own boundary");

    // 5. Android project appendMedia compatibility
    const auto android_project = parse_project(read(std::filesystem::path(FRAMETRAIL_FIXTURES) / "project-v2-android.json"));
    const Json append_android{
        {"op", "appendMedia"},
        {"screenMedia", {
            {"id", "screen-android-import"},
            {"path", "media/import-desktop.mp4"},
            {"duration", {{"ticks", 5 * time_scale}, {"timescale", time_scale}}},
            {"width", 1280},
            {"height", 720}
        }},
        {"audioMedia", {
            {"id", "audio-android-import"},
            {"path", "media/import-desktop.mp4"},
            {"duration", {{"ticks", 5 * time_scale}, {"timescale", time_scale}}},
            {"sampleRate", 48000},
            {"channels", 2}
        }},
        {"segmentId", "clip-android-import"}
    };
    const auto merged_android = edit_project(android_project, append_android.dump());
    check(merged_android.schema_version == 2, "Android merged project keeps schema 2");
    check(Json::parse(merged_android.primary_source_json).at("kind") == "desktop", "Android primarySource kind switches to desktop");
    check(normalized_ticks(merged_android.source_duration) == 15 * time_scale, "Android merged sourceDuration updated");
    check(std::none_of(merged_android.media.begin(), merged_android.media.end(), [](const Media& m) { return m.kind == MediaKind::android_audio; }), "androidAudio converted to systemAudio on desktop switch");
    check(parse_project(serialize_project(merged_android)).segments.size() == 4, "Android merged project roundtrips");
}

void test_cut_ranges(const Project& base_project) {
    // Prepare a single-clip 300s project with clipAudio and a trailing clip + transition
    auto single = base_project;
    single.source_duration = canonical_time(300 * time_scale);
    single.media = {single.media[0]};
    single.media[0].start_offset = canonical_time(0);
    single.media[0].duration = canonical_time(300 * time_scale);
    single.segments = {
        {"clip-main", canonical_time(0), canonical_time(280 * time_scale), {1, 1}},
        {"clip-tail", canonical_time(280 * time_scale), canonical_time(20 * time_scale), {1, 1}}
    };
    auto editor_json = Json::parse(single.editor_json);
    editor_json["clipAudio"] = {{"clip-main", {{"microphoneGain", 1.25}}}};
    single.editor_json = editor_json.dump();
    single.effects = {
        {"tr-1", "transition", canonical_time(280 * time_scale), canonical_time(6 * time_scale / 10),
         Json{{"origin", "manual"}, {"style", "crossfade"}, {"duration", 0.6}, {"easing", "cubic"},
              {"fromSegmentId", "clip-main"}, {"toSegmentId", "clip-tail"}}.dump()}
    };
    validate_project(single);

    // 1. Atomic cutRanges with 130 cuts (>100 batch cap) inside clip-main
    Json ranges = Json::array();
    for (int i = 0; i < 130; ++i) {
        int64_t s = (i * 2 + 1) * time_scale;
        int64_t e = s + 4 * time_scale / 10; // 0.4s cut every 2s
        ranges.push_back({{"start", s}, {"end", e}, {"reason", i % 2 == 0 ? "pause" : "filler"}, {"label", "cut"}});
    }
    const auto cut = edit_project(single, Json{{"op", "cutRanges"}, {"ranges", ranges}}.dump());
    check(cut.segments.size() == 132, "cutRanges splits 130 interior ranges into 131 pieces + tail");
    check(cut.segments.front().id == "clip-main", "cutRanges keeps original id on first surviving piece");
    check(cut.segments.back().id == "clip-tail", "cutRanges preserves untouched trailing clip");
    const auto cut_editor = Json::parse(cut.editor_json);
    check(cut_editor["clipAudio"].contains(cut.segments[130].id), "cutRanges copies clipAudio to split pieces");
    const auto tr_params = Json::parse(cut.effects[0].parameters_json);
    check(tr_params.at("fromSegmentId").get<std::string>() == cut.segments[130].id, "cutRanges updates transition fromSegmentId to rightmost split piece");
    check(parse_project(serialize_project(cut)).segments.size() == 132, "cutRanges result roundtrips");

    // 2. Reject invalid ranges and full-timeline deletion
    rejects([&] {
        (void)edit_project(single, Json{{"op", "cutRanges"}, {"ranges", Json::array()}}.dump());
    }, "cutRanges rejects empty ranges");
    rejects([&] {
        (void)edit_project(single, Json{{"op", "cutRanges"}, {"ranges", Json::array({{{"start", 0}, {"end", 300 * time_scale}}})}}.dump());
    }, "cutRanges rejects deleting entire timeline");
    rejects([&] {
        (void)edit_project(single, Json{{"op", "cutRanges"}, {"ranges", Json::array({{{"start", 10 * time_scale}, {"end", 5 * time_scale}}})}}.dump());
    }, "cutRanges rejects end <= start");
}

void test_export_settings(const Project& base_project) {
    check(base_project.export_settings.quality == "standard", "default export quality is standard");
    const auto serialized_default = Json::parse(serialize_project(base_project));
    check(!serialized_default["export"].contains("quality"), "standard export quality is omitted from serialized JSON");

    for (const auto* q : {"low", "high", "ultra"}) {
        auto json = Json::parse(serialize_project(base_project));
        json["export"]["quality"] = q;
        const auto parsed = parse_project(json.dump());
        check(parsed.export_settings.quality == q, "parse export.quality");
        const auto roundtrip = Json::parse(serialize_project(parsed));
        check(roundtrip["export"].contains("quality") && roundtrip["export"]["quality"] == q, "serialize non-standard export.quality");
    }

    for (const auto* bad_q : {"medium", "best", ""}) {
        auto json = Json::parse(serialize_project(base_project));
        json["export"]["quality"] = bad_q;
        rejects([&] { (void)parse_project(json.dump()); }, "invalid export.quality");
    }

    // editor settings op: set quality, preserve when omitted, reset to standard
    auto high_export = Json::parse(serialize_project(base_project))["export"];
    high_export["quality"] = "high";
    const auto with_high = edit_project(base_project, Json{{"op", "settings"}, {"export", high_export}}.dump());
    check(with_high.export_settings.quality == "high", "settings op sets export.quality");

    auto omitted_quality = Json::parse(serialize_project(base_project))["export"];
    omitted_quality["width"] = 1280;
    omitted_quality["height"] = 720;
    const auto preserved = edit_project(with_high, Json{{"op", "settings"}, {"export", omitted_quality}}.dump());
    check(preserved.export_settings.quality == "high" && preserved.export_settings.width == 1280, "settings op preserves existing non-standard quality when omitted");

    auto reset_quality = omitted_quality;
    reset_quality["quality"] = "standard";
    const auto reset = edit_project(preserved, Json{{"op", "settings"}, {"export", reset_quality}}.dump());
    check(reset.export_settings.quality == "standard", "settings op resets export.quality to standard");

    // FPS validation: 10, 15, 24, 30, 60 allowed; 120 rejected
    for (const int fps : {10, 15, 24, 30, 60}) {
        auto json = Json::parse(serialize_project(base_project));
        json["export"]["fps"] = {{"numerator", fps}, {"denominator", 1}};
        const auto parsed = parse_project(json.dump());
        check(parsed.export_settings.fps.numerator == fps && parsed.export_settings.fps.denominator == 1, "allowed export fps");
    }
    for (const int bad_fps : {12, 45, 120}) {
        auto json = Json::parse(serialize_project(base_project));
        json["export"]["fps"] = {{"numerator", bad_fps}, {"denominator", 1}};
        rejects([&] { (void)parse_project(json.dump()); }, "unsupported export fps");
    }
}
} // namespace

int main(int argc, char** argv) {
    try {
        if (argc == 3 && std::string_view(argv[1]) == "--validate") { (void)parse_project(read(argv[2])); return 0; }
        if (argc == 4 && std::string_view(argv[1]) == "--roundtrip") {
            std::ofstream file(argv[3], std::ios::binary); file << serialize_project(parse_project(read(argv[2])));
            if (!file) throw std::runtime_error("Cannot write roundtrip output");
            return 0;
        }
        const auto text = read(std::filesystem::path(FRAMETRAIL_FIXTURES) / "project-v1.json");
        test_protocol(text);
        const auto project = parse_project(text);
        test_timeline(project); test_pointer(project); test_scene(project); test_append_media(project); test_cut_ranges(project); test_export_settings(project);
        std::cout << "PASS: " << checks << " checks across protocol, timeline, pointer, scene\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL after " << checks << " checks: " << error.what() << '\n'; return 1;
    }
}
