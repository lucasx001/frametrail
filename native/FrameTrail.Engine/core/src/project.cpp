#include "frametrail/core/project.hpp"
#include "frametrail/core/timeline.hpp"
#include <nlohmann/json.hpp>
#include <algorithm>
#include <array>
#include <cctype>
#include <cmath>
#include <limits>
#include <unordered_set>

namespace frametrail::core {
namespace {
using Json = nlohmann::json;
[[noreturn]] void invalid(const std::string& path, const std::string& message) { throw ValidationError(path, message); }
void object(const Json& value, const std::string& path, std::initializer_list<const char*> allowed) {
    if (!value.is_object()) invalid(path, "expected object");
    for (auto it = value.begin(); it != value.end(); ++it) {
        if (std::none_of(allowed.begin(), allowed.end(), [&](const auto* key) { return it.key() == key; }))
            invalid(path + "." + it.key(), "unknown field; refusing lossy rewrite");
    }
}
const Json& required(const Json& value, const char* key, const std::string& path) {
    const auto found = value.find(key);
    if (found == value.end()) invalid(path + "." + key, "required field missing");
    return *found;
}
std::int64_t integer(const Json& value, const std::string& path, std::int64_t minimum, std::int64_t maximum) {
    if (!value.is_number_integer() || (value.is_number_unsigned() && value.get<std::uint64_t>() > static_cast<std::uint64_t>((std::numeric_limits<std::int64_t>::max)())))
        invalid(path, "expected signed 64-bit integer");
    const auto result = value.get<std::int64_t>();
    if (result < minimum || result > maximum) invalid(path, "integer outside allowed range");
    return result;
}
std::string string_value(const Json& value, const std::string& path, std::size_t max_length = 1024) {
    if (!value.is_string()) invalid(path, "expected string");
    auto result = value.get<std::string>();
    if (result.empty() || result.size() > max_length) invalid(path, "empty or oversized string");
    return result;
}
double number(const Json& value, const std::string& path) {
    if (!value.is_number()) invalid(path, "expected finite number");
    const auto result = value.get<double>();
    if (!std::isfinite(result)) invalid(path, "expected finite number");
    return result;
}
bool boolean(const Json& value, const std::string& path) {
    if (!value.is_boolean()) invalid(path, "expected boolean");
    return value.get<bool>();
}
Time read_time(const Json& value, const std::string& path) {
    object(value, path, {"ticks", "timescale"});
    Time result{integer(required(value, "ticks", path), path + ".ticks", -(std::numeric_limits<std::int64_t>::max)(), (std::numeric_limits<std::int64_t>::max)()),
        static_cast<std::int32_t>(integer(required(value, "timescale", path), path + ".timescale", 1, 1'000'000'000))};
    try { (void)normalized_ticks(result); } catch (const ValidationError& error) { invalid(path, error.what()); }
    return result;
}
Rational read_rational(const Json& value, const std::string& path) {
    object(value, path, {"numerator", "denominator"});
    return {static_cast<std::int32_t>(integer(required(value, "numerator", path), path, 1, 1'000'000)),
        static_cast<std::int32_t>(integer(required(value, "denominator", path), path, 1, 1'000'000))};
}
Rect read_rect(const Json& value, const std::string& path) {
    object(value, path, {"x", "y", "width", "height"});
    return {number(required(value, "x", path), path), number(required(value, "y", path), path),
        number(required(value, "width", path), path), number(required(value, "height", path), path)};
}
Color read_color(const Json& value, const std::string& path) {
    object(value, path, {"r", "g", "b", "a"});
    return {number(required(value, "r", path), path), number(required(value, "g", path), path),
        number(required(value, "b", path), path), number(required(value, "a", path), path)};
}
MediaKind read_kind(const std::string& value) {
    if (value == "screen") return MediaKind::screen;
    if (value == "camera") return MediaKind::camera;
    if (value == "systemAudio") return MediaKind::system_audio;
    if (value == "microphone") return MediaKind::microphone;
    if (value == "pointerEvents") return MediaKind::pointer_events;
    invalid("media.kind", "unsupported media kind");
}
std::string kind_string(MediaKind value) {
    switch (value) {
    case MediaKind::screen: return "screen";
    case MediaKind::camera: return "camera";
    case MediaKind::system_audio: return "systemAudio";
    case MediaKind::microphone: return "microphone";
    case MediaKind::pointer_events: return "pointerEvents";
    }
    invalid("media.kind", "unsupported media kind");
}
bool valid_id(std::string_view value) {
    return !value.empty() && value.size() <= 128 && std::all_of(value.begin(), value.end(), [](unsigned char c) {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
    });
}
void validate_rect(Rect value, const std::string& path) {
    if (!std::isfinite(value.x) || !std::isfinite(value.y) || !std::isfinite(value.width) || !std::isfinite(value.height) ||
        value.x < 0 || value.y < 0 || value.width <= 0 || value.height <= 0 || value.x + value.width > 1.000000001 || value.y + value.height > 1.000000001)
        invalid(path, "rectangle must be finite and inside normalized canvas");
}
Json time_json(Time value) { return {{"ticks", value.ticks}, {"timescale", value.timescale}}; }
Json ratio_json(Rational value) { return {{"numerator", value.numerator}, {"denominator", value.denominator}}; }
Json rect_json(Rect value) { return {{"x", value.x}, {"y", value.y}, {"width", value.width}, {"height", value.height}}; }
Json color_json(Color value) { return {{"r", value.r}, {"g", value.g}, {"b", value.b}, {"a", value.a}}; }
} // namespace

bool is_project_relative_path(std::string_view path) {
    if (path.empty() || path.size() > 1024 || path.front() == '/' || path.back() == '/') return false;
    for (const unsigned char c : path)
        if (c < 32 || c == 127 || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return false;
    std::size_t offset = 0;
    while (offset < path.size()) {
        const auto slash = path.find('/', offset);
        const auto part = path.substr(offset, slash == std::string_view::npos ? path.size() - offset : slash - offset);
        if (part.empty() || part == "." || part == ".." || part.back() == '.' || part.back() == ' ') return false;
        const auto dot = part.find('.');
        auto stem = part.substr(0, dot);
        std::string upper;
        for (const auto c : stem) upper += static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
        if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL" ||
            (upper.size() == 4 && (upper.starts_with("COM") || upper.starts_with("LPT")) && upper[3] >= '0' && upper[3] <= '9')) return false;
        if (slash == std::string_view::npos) break;
        offset = slash + 1;
    }
    return true;
}

Project parse_project(std::string_view text) {
    if (text.empty() || text.size() > 16 * 1024 * 1024) invalid("$", "JSON must be 1 byte..16 MiB");
    try {
        std::vector<std::unordered_set<std::string>> keys;
        auto callback = [&](int depth, Json::parse_event_t event, Json& parsed) {
            if (depth > 32) invalid("$", "JSON nesting limit exceeded");
            if (event == Json::parse_event_t::object_start) keys.emplace_back();
            if (event == Json::parse_event_t::key && !keys.back().insert(parsed.get<std::string>()).second)
                invalid("$", "duplicate JSON key");
            if (event == Json::parse_event_t::object_end) keys.pop_back();
            return true;
        };
        const auto root = Json::parse(text, callback);
        object(root, "$", {"schemaVersion", "projectId", "revision", "sourceDuration", "media", "segments", "canvas", "effects", "markers", "export", "editor"});
        Project result;
        if (root.contains("editor")) result.editor_json = root["editor"].dump();
        result.schema_version = static_cast<std::uint32_t>(integer(required(root, "schemaVersion", "$"), "schemaVersion", 1, 1));
        result.project_id = string_value(required(root, "projectId", "$"), "projectId", 128);
        result.revision = integer(required(root, "revision", "$"), "revision", 0, (std::numeric_limits<std::int64_t>::max)());
        result.source_duration = read_time(required(root, "sourceDuration", "$"), "sourceDuration");
        const auto& media = required(root, "media", "$");
        if (!media.is_array() || media.empty() || media.size() > 100'000) invalid("media", "expected 1..100000 items");
        for (const auto& entry : media) {
            object(entry, "media[]", {"id", "kind", "path", "duration", "startOffset", "width", "height", "sampleRate", "channels"});
            Media item;
            item.id = string_value(required(entry, "id", "media[]"), "media.id", 128);
            item.kind = read_kind(string_value(required(entry, "kind", "media[]"), "media.kind"));
            item.path = string_value(required(entry, "path", "media[]"), "media.path");
            item.duration = read_time(required(entry, "duration", "media[]"), "media.duration");
            item.start_offset = read_time(required(entry, "startOffset", "media[]"), "media.startOffset");
            auto field = [&](const char* key, std::int64_t max) {
                const auto found = entry.find(key);
                return found == entry.end() ? 0u : static_cast<std::uint32_t>(integer(*found, std::string("media.") + key, 0, max));
            };
            item.width = field("width", 32768); item.height = field("height", 32768);
            item.sample_rate = field("sampleRate", 384000); item.channels = field("channels", 32);
            result.media.push_back(std::move(item));
        }
        const auto& segments = required(root, "segments", "$");
        if (!segments.is_array() || segments.empty() || segments.size() > 100'000) invalid("segments", "expected 1..100000 items");
        for (const auto& entry : segments) {
            object(entry, "segments[]", {"id", "sourceStart", "sourceDuration", "rate"});
            result.segments.push_back({string_value(required(entry, "id", "segments[]"), "segments.id", 128),
                read_time(required(entry, "sourceStart", "segments[]"), "segments.sourceStart"),
                read_time(required(entry, "sourceDuration", "segments[]"), "segments.sourceDuration"),
                read_rational(required(entry, "rate", "segments[]"), "segments.rate")});
        }
        const auto& canvas = required(root, "canvas", "$");
        object(canvas, "canvas", {"width", "height", "padding", "background", "screen", "camera"});
        result.canvas.width = static_cast<std::uint32_t>(integer(required(canvas, "width", "canvas"), "canvas.width", 1, 16384));
        result.canvas.height = static_cast<std::uint32_t>(integer(required(canvas, "height", "canvas"), "canvas.height", 1, 16384));
        result.canvas.padding = number(required(canvas, "padding", "canvas"), "canvas.padding");
        result.canvas.background = read_color(required(canvas, "background", "canvas"), "canvas.background");
        result.canvas.screen = read_rect(required(canvas, "screen", "canvas"), "canvas.screen");
        const auto& camera = required(canvas, "camera", "canvas");
        object(camera, "canvas.camera", {"enabled", "x", "y", "width", "height", "mirror"});
        result.canvas.camera.enabled = boolean(required(camera, "enabled", "canvas.camera"), "canvas.camera.enabled");
        result.canvas.camera.rect = {number(required(camera, "x", "camera"), "camera.x"), number(required(camera, "y", "camera"), "camera.y"),
            number(required(camera, "width", "camera"), "camera.width"), number(required(camera, "height", "camera"), "camera.height")};
        result.canvas.camera.mirror = boolean(required(camera, "mirror", "camera"), "camera.mirror");
        const auto& effects = required(root, "effects", "$");
        if (!effects.is_array() || effects.size() > 100'000) invalid("effects", "expected array, at most 100000 items");
        for (const auto& entry : effects) {
            object(entry, "effects[]", {"id", "kind", "sourceStart", "sourceDuration", "parameters"});
            const auto& parameters = required(entry, "parameters", "effects[]");
            if (!parameters.is_object()) invalid("effects.parameters", "expected extensible object");
            result.effects.push_back({string_value(required(entry, "id", "effects[]"), "effects.id", 128),
                string_value(required(entry, "kind", "effects[]"), "effects.kind", 32),
                read_time(required(entry, "sourceStart", "effects[]"), "effects.sourceStart"),
                read_time(required(entry, "sourceDuration", "effects[]"), "effects.sourceDuration"), parameters.dump()});
        }
        if (const auto markers = root.find("markers"); markers != root.end()) {
            if (!markers->is_array() || markers->size() > 100'000) invalid("markers", "expected array, at most 100000 items");
            for (const auto& entry : *markers) {
                object(entry, "markers[]", {"id", "time", "label"});
                const auto& label = required(entry, "label", "markers[]");
                if (!label.is_string() || label.get_ref<const std::string&>().size() > 512) invalid("markers.label", "expected UTF-8 label at most 512 bytes");
                result.markers.push_back({string_value(required(entry, "id", "markers[]"), "markers.id", 128),
                    read_time(required(entry, "time", "markers[]"), "markers.time"), label.get<std::string>()});
            }
        }
        const auto& output = required(root, "export", "$");
        object(output, "export", {"width", "height", "fps", "videoCodec", "audioCodec", "container"});
        result.export_settings.width = static_cast<std::uint32_t>(integer(required(output, "width", "export"), "export.width", 2, 7680));
        result.export_settings.height = static_cast<std::uint32_t>(integer(required(output, "height", "export"), "export.height", 2, 7680));
        result.export_settings.fps = read_rational(required(output, "fps", "export"), "export.fps");
        result.export_settings.video_codec = string_value(required(output, "videoCodec", "export"), "export.videoCodec", 16);
        result.export_settings.audio_codec = string_value(required(output, "audioCodec", "export"), "export.audioCodec", 16);
        result.export_settings.container = string_value(required(output, "container", "export"), "export.container", 16);
        validate_project(result);
        return result;
    } catch (const ValidationError&) { throw; }
    catch (const Json::exception& error) { invalid("$", std::string("invalid JSON: ") + error.what()); }
}

void validate_project(const Project& project) {
    const auto editor = Json::parse(project.editor_json);
    object(editor, "editor", {"systemGain", "microphoneGain", "systemMuted", "microphoneMuted", "clipAudio", "cornerRadius", "shadow", "cursorScale", "crop"});
    for (const auto* key : {"systemGain", "microphoneGain", "cornerRadius", "shadow", "cursorScale"}) if (editor.contains(key)) {
        const auto v = number(editor[key], std::string("editor.") + key);
        const double max = std::string_view(key) == "cornerRadius" ? 200 : (std::string_view(key) == "shadow" ? 1 : 4);
        if (v < 0 || v > max) invalid("editor", "setting outside allowed range");
    }
    for (const auto* key : {"systemMuted", "microphoneMuted"}) if (editor.contains(key)) (void)boolean(editor[key], key);
    if (editor.contains("crop")) validate_rect(read_rect(editor["crop"], "editor.crop"), "editor.crop");
    if (editor.contains("clipAudio")) {
        if (!editor["clipAudio"].is_object() || editor["clipAudio"].size() > 100000) invalid("editor.clipAudio", "invalid clip audio map");
        for (auto it = editor["clipAudio"].begin(); it != editor["clipAudio"].end(); ++it) {
            if (!valid_id(it.key())) invalid("editor.clipAudio", "invalid clip ID");
            object(it.value(), "editor.clipAudio[]", {"systemGain", "microphoneGain", "systemMuted", "microphoneMuted"});
            for (const auto* key : {"systemGain", "microphoneGain"}) if (it.value().contains(key)) { auto v = number(it.value()[key], key); if (v < 0 || v > 4) invalid(key, "gain must be 0..4"); }
            for (const auto* key : {"systemMuted", "microphoneMuted"}) if (it.value().contains(key)) (void)boolean(it.value()[key], key);
        }
    }
    if (project.schema_version != 1) invalid("schemaVersion", "only schema 1 is supported");
    if (!valid_id(project.project_id)) invalid("projectId", "invalid ID");
    if (project.revision < 0) invalid("revision", "must be non-negative");
    const auto duration = normalized_ticks(project.source_duration);
    if (duration <= 0) invalid("sourceDuration", "must be positive");
    if (project.media.empty() || project.media.size() > 100'000 || project.effects.size() > 100'000) invalid("media", "collection limit exceeded");
    std::unordered_set<std::string> ids;
    auto unique_id = [&](const std::string& id) {
        if (!valid_id(id) || !ids.insert(id).second) invalid("id", "invalid or duplicate global item ID");
    };
    std::array<std::vector<std::pair<std::int64_t, std::int64_t>>, 5> track_ranges;
    for (const auto& media : project.media) {
        unique_id(media.id);
        if (!is_project_relative_path(media.path)) invalid("media.path", "expected safe project-relative Windows path with forward slashes");
        const auto length = normalized_ticks(media.duration);
        const auto start = normalized_ticks(media.start_offset);
        if (length <= 0 || start >= duration || start + length <= 0 || start + length > maximum_source_ticks)
            invalid("media.duration", "invalid or non-intersecting track interval");
        const auto kind = static_cast<std::size_t>(media.kind);
        if (kind >= track_ranges.size()) invalid("media.kind", "unsupported media kind");
        track_ranges[kind].emplace_back(start, start + length);
        if (media.kind == MediaKind::screen || media.kind == MediaKind::camera) {
            if (media.width == 0 || media.height == 0 || media.width > 32768 || media.height > 32768)
                invalid("media", "video needs dimensions in 1..32768");
        } else if (media.kind == MediaKind::system_audio || media.kind == MediaKind::microphone) {
            if (media.sample_rate < 8000 || media.sample_rate > 384000 || media.channels == 0 || media.channels > 32)
                invalid("media", "audio needs valid sample rate and channel count");
        }
    }
    for (auto& ranges : track_ranges) {
        std::sort(ranges.begin(), ranges.end());
        for (std::size_t i = 1; i < ranges.size(); ++i)
            if (ranges[i].first < ranges[i - 1].second) invalid("media", "same-kind media segments overlap");
    }
    const auto& screens = track_ranges[static_cast<std::size_t>(MediaKind::screen)];
    if (screens.empty() || screens.front().first != 0 || screens.back().second != duration)
        invalid("media", "screen segments must cover full source duration");
    for (std::size_t i = 1; i < screens.size(); ++i)
        if (screens[i].first != screens[i - 1].second) invalid("media", "screen media contains a gap");
    for (const auto& segment : project.segments) unique_id(segment.id);
    (void)TimelineMap(project.source_duration, project.segments);
    if (project.canvas.width == 0 || project.canvas.height == 0 || project.canvas.width > 16384 || project.canvas.height > 16384)
        invalid("canvas", "dimensions must be 1..16384");
    if (!std::isfinite(project.canvas.padding) || project.canvas.padding < 0 || project.canvas.padding >= 0.5)
        invalid("canvas.padding", "must be finite and in 0..0.5 (exclusive)");
    const auto color = project.canvas.background;
    for (const auto component : {color.r, color.g, color.b, color.a})
        if (!std::isfinite(component) || component < 0 || component > 1) invalid("canvas.background", "SDR color channels must be 0..1");
    validate_rect(project.canvas.screen, "canvas.screen");
    validate_rect(project.canvas.camera.rect, "canvas.camera");
    const auto& screen_rect = project.canvas.screen;
    const auto padding = project.canvas.padding * (std::min)(project.canvas.width, project.canvas.height);
    if (screen_rect.width * project.canvas.width <= padding * 2 || screen_rect.height * project.canvas.height <= padding * 2)
        invalid("canvas.padding", "padding leaves no visible screen area");
    for (const auto& effect : project.effects) {
        unique_id(effect.id);
        const auto start = normalized_ticks(effect.source_start);
        const auto length = normalized_ticks(effect.source_duration);
        if (start < 0 || length <= 0 || start >= duration || length > duration - start) invalid("effects", "effect outside source duration");
        if (effect.kind != "zoom" && effect.kind != "mask" && effect.kind != "sticker" && effect.kind != "focus" && effect.kind != "tilt" && effect.kind != "intro")
            invalid("effects.kind", "unsupported effect kind");
        try {
            const auto parameters = Json::parse(effect.parameters_json);
            if (!parameters.is_object()) invalid("effects.parameters", "expected object");
        } catch (const Json::exception&) { invalid("effects.parameters", "invalid JSON object"); }
    }
    if (project.markers.size() > 100'000) invalid("markers", "marker count limit exceeded");
    for (const auto& marker : project.markers) {
        unique_id(marker.id);
        const auto time = normalized_ticks(marker.time);
        if (time < 0 || time > duration || marker.label.size() > 512) invalid("markers", "invalid source marker time or label length");
    }
    const auto& output = project.export_settings;
    if (output.width < 2 || output.height < 2 || output.width > 7680 || output.height > 7680 || output.width % 2 || output.height % 2)
        invalid("export", "H.264 output dimensions must be even and in 2..7680");
    if (output.fps.denominator != 1 || (output.fps.numerator != 30 && output.fps.numerator != 60)) invalid("export.fps", "schema 1 supports 30/1 or 60/1");
    if (output.video_codec != "h264" || output.audio_codec != "aac" || output.container != "mp4") invalid("export", "schema 1 defaults require h264/aac/mp4");
}

std::string serialize_project(const Project& project) {
    validate_project(project);
    Json result{{"schemaVersion", project.schema_version}, {"projectId", project.project_id}, {"revision", project.revision},
        {"sourceDuration", time_json(project.source_duration)}, {"media", Json::array()}, {"segments", Json::array()}, {"effects", Json::array()}, {"markers", Json::array()}};
    for (const auto& media : project.media)
        result["media"].push_back({{"id", media.id}, {"kind", kind_string(media.kind)}, {"path", media.path}, {"duration", time_json(media.duration)},
            {"startOffset", time_json(media.start_offset)}, {"width", media.width}, {"height", media.height}, {"sampleRate", media.sample_rate}, {"channels", media.channels}});
    for (const auto& segment : project.segments)
        result["segments"].push_back({{"id", segment.id}, {"sourceStart", time_json(segment.source_start)},
            {"sourceDuration", time_json(segment.source_duration)}, {"rate", ratio_json(segment.rate)}});
    const auto& canvas = project.canvas;
    auto camera = rect_json(canvas.camera.rect);
    camera["enabled"] = canvas.camera.enabled; camera["mirror"] = canvas.camera.mirror;
    result["canvas"] = {{"width", canvas.width}, {"height", canvas.height}, {"padding", canvas.padding},
        {"background", color_json(canvas.background)}, {"screen", rect_json(canvas.screen)}, {"camera", camera}};
    for (const auto& effect : project.effects)
        result["effects"].push_back({{"id", effect.id}, {"kind", effect.kind}, {"sourceStart", time_json(effect.source_start)},
            {"sourceDuration", time_json(effect.source_duration)}, {"parameters", Json::parse(effect.parameters_json)}});
    for (const auto& marker : project.markers)
        result["markers"].push_back({{"id", marker.id}, {"time", time_json(marker.time)}, {"label", marker.label}});
    const auto& output = project.export_settings;
    result["export"] = {{"width", output.width}, {"height", output.height}, {"fps", ratio_json(output.fps)},
        {"videoCodec", output.video_codec}, {"audioCodec", output.audio_codec}, {"container", output.container}};
    if (project.editor_json != "{}") result["editor"] = Json::parse(project.editor_json);
    auto text = result.dump(2);
    if (text.size() > 16 * 1024 * 1024) invalid("$", "serialized JSON exceeds 16 MiB");
    return text;
}
} // namespace frametrail::core
