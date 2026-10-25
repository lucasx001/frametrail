#include "pointer_sampling.hpp"
#include <iostream>
#include <stdexcept>

using namespace frametrail::core;
using namespace frametrail::media;
namespace {
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
int64_t seconds(double value) { return static_cast<int64_t>(std::llround(value * time_scale)); }
Media pointer(std::string id, int64_t start, int64_t duration) {
    return {id, MediaKind::pointer_events, "events/" + id + "/pointer.jsonl", canonical_time(duration), canonical_time(start)};
}
Segment segment(std::string id, int64_t start, int64_t duration) {
    return {id, canonical_time(start), canonical_time(duration), {1, 1}};
}
PointerSample point(double at, float x, uint32_t kind = 0) { return {seconds(at), x, 0.5f, 1, true, kind}; }
template<class F> void rejects(F&& run, const char* message) {
    try { run(); } catch (const ValidationError&) { return; }
    throw std::runtime_error(message);
}
}
int main() {
    try {
        Project project;
        project.media = {pointer("first", 0, seconds(60)), pointer("second", seconds(60), seconds(60))};
        project.segments = {segment("take", 0, seconds(120))};
        std::unordered_map<std::string, PointerTrack> tracks;
        tracks["first"].points = {point(0, 0.2f), point(59.7, 0.2f, 1), point(59.9, 0.2f), point(60.1, 0.99f, 1)};
        tracks["second"].points = {point(0, 0.8f), point(0.1, 0.8f), point(59.9, 0.8f)};
        // Same shape ID, different file and hotspot: checkpoint IDs are not global.
        tracks["first"].shapes[1] = {"first.png", 1, 2};
        tracks["second"].shapes[1] = {"second.png", 3, 4};
        auto load = [&](const Media& media) -> const PointerTrack& { return tracks.at(media.id); };
        auto positive = pointer_window(project, seconds(59.9), seconds(0.2));
        require(positive && positive->ticks == seconds(60.1), "positive calibration must select following file");
        auto after = sample_pointer_window(*positive, load, 0, false, 1.2);
        require(after && after->latest.media->id == "second" && std::abs(after->x - 0.8) < 0.00001,
            "positive calibration must retain the cursor across a physical boundary");
        require(after->latest.track->shapes.at(after->latest.point.shape).file == "second.png",
            "shape lookup must use the sampled file, not the current screen file");
        require(after->clicks.size() == 1 && after->clicks[0].ticks == seconds(59.7),
            "prior click must survive checkpoint; unreferenced physical overlap tail must not render");
        auto negative = pointer_window(project, seconds(60.1), seconds(-0.2));
        auto before = sample_pointer_window(*negative, load, 0, false, 1.2);
        require(before && before->latest.media->id == "first" && std::abs(before->x - 0.2) < 0.00001,
            "negative calibration must select preceding file");
        auto smoothed = sample_pointer_window(*positive, load, 0.5, false, 1.2);
        require(smoothed && smoothed->x > 0.2 && smoothed->x < 0.8,
            "causal smoothing must include both physical files");

        tracks["first"].points = {point(0, 0.5f), point(59, 0.5f)};
        tracks["second"].points = {point(0, 0.5f), point(0.1, 0.5f)};
        auto idle = sample_pointer_window(*positive, load, 0.08, true, 8);
        require(idle && idle->opacity < 1e-12, "checkpoint first sample must not revive an idle cursor");
        require(positive->ticks - positive->begin == pointer_history_ticks,
            "retained history must be bounded by the maximum supported effect interval");

        // Real pause/resume, manual split, deletion and reordering all retain
        // logical clip boundaries. Calibration is confined to the current clip.
        project.segments = {segment("paused-take", 0, seconds(60)), segment("resumed-take", seconds(60), seconds(60))};
        auto pause = pointer_window(project, seconds(59.9), seconds(0.2));
        require(pause && pause->ticks == seconds(60) - 1 && pause->media.size() == 1,
            "positive offset must not cross an authored or pause boundary");
        auto resume = pointer_window(project, seconds(60.1), seconds(-0.2));
        require(resume && resume->ticks == seconds(60) && resume->begin == seconds(60) && resume->media.size() == 1,
            "negative offset must not borrow a prior take or deleted content");
        auto resumed = sample_pointer_window(*resume, load, 0.5, true, 8);
        require(resumed && resumed->clicks.empty() && resumed->opacity == 1,
            "new clip must reset activity without inheriting a click");
        project.segments = {segment("later", seconds(70), seconds(10)), segment("earlier", seconds(10), seconds(10))};
        auto trim = pointer_window(project, seconds(70.1), seconds(-2));
        require(trim && trim->ticks == seconds(70) && trim->begin == seconds(70),
            "reordered trimmed clips must retain source-local sample bounds");
        tracks["second"].points = {point(9, 0.4f, 1), point(10.2, 0.7f)};
        auto trimmed = sample_pointer_window(*trim, load, 0.5, false, 1.2);
        require(trimmed && trimmed->clicks.empty() && std::abs(trimmed->x - 0.4) < 0.00001,
            "trim start may seed position but never a click or smoothing history from removed content");

        project.segments = {segment("take", 0, seconds(120))};
        project.media = {pointer("first", 0, seconds(59)), pointer("second", seconds(60), seconds(60))};
        auto gap = pointer_window(project, seconds(60.1), 0);
        require(gap && gap->begin == seconds(60) && gap->media.size() == 1,
            "a gap in pointer coverage must break history");
        require(!pointer_window(project, seconds(59.5), 0), "missing pointer coverage must stay missing");

        project.media.clear();
        for (size_t index = 0; index < maximum_pointer_history_tracks + 1; ++index)
            project.media.push_back(pointer(std::to_string(index), static_cast<int64_t>(index), 1));
        rejects([&] { (void)pointer_window(project, maximum_pointer_history_tracks, 0); },
            "pathological file density must retain a bounded history cache");
        project.media = {pointer("dense", 0, seconds(60))};
        auto& dense = tracks["dense"].points;
        dense.resize(maximum_pointer_history_samples + 1);
        for (size_t index = 0; index < dense.size(); ++index) dense[index] = {static_cast<int64_t>(index), 0.5f, 0.5f, 1, true};
        auto dense_window = pointer_window(project, maximum_pointer_history_samples, 0);
        rejects([&] { (void)sample_pointer_window(*dense_window, load, 0.5, false, 1.2); },
            "pathological sample density must retain a bounded aggregate buffer");
        std::cout << "Pointer calibration, history, shape origins and cut boundaries passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n'; return 1;
    }
}
