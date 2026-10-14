#include "frametrail/core/project.hpp"
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
        test_timeline(project); test_pointer(project); test_scene(project);
        std::cout << "PASS: " << checks << " checks across protocol, timeline, pointer, scene\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL after " << checks << " checks: " << error.what() << '\n'; return 1;
    }
}
