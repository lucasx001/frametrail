#include "frametrail/engine.h"
#include "frametrail/core/scene.hpp"
#include "frametrail/core/editor.hpp"
#include <atomic>
#include <cstring>
#include <exception>
#include <limits>
#include <memory>
#include <mutex>
#include <unordered_map>

namespace {
struct Session {
    std::atomic_bool cancelled{false};
    std::mutex snapshot_mutex;
    std::shared_ptr<const frametrail::core::FrameSceneEvaluator> snapshot;
    std::string project_id;
    int64_t revision{-1};
};
std::mutex sessions_mutex;
std::unordered_map<ft_session_handle, std::shared_ptr<Session>> sessions;
std::atomic_uint64_t next_handle{1};
thread_local char last_error[1024]{};

ft_result fail(ft_result code, const char* message) noexcept {
    const auto length = (std::strlen(message) < sizeof(last_error) - 1) ? std::strlen(message) : sizeof(last_error) - 1;
    std::memcpy(last_error, message, length);
    last_error[length] = '\0';
    last_error[sizeof(last_error) - 1] = '\0';
    return code;
}
template<class Function> ft_result protect(Function&& function) noexcept {
    try { return function(); }
    catch (const frametrail::core::ValidationError& exception) { return fail(FT_E_INVALID_PROJECT, exception.what()); }
    catch (const std::exception& exception) { return fail(FT_E_INTERNAL, exception.what()); }
    catch (...) { return fail(FT_E_INTERNAL, "Unknown native exception"); }
}
ft_result copy_string(const char* value, char* buffer, uint32_t capacity, uint32_t* required) noexcept {
    if (!required) return FT_E_INVALID_ARGUMENT;
    *required = static_cast<uint32_t>(std::strlen(value) + 1);
    if (!buffer && capacity == 0) return FT_OK;
    if (!buffer) return FT_E_INVALID_ARGUMENT;
    if (capacity < *required) return FT_E_BUFFER_TOO_SMALL;
    std::memcpy(buffer, value, *required);
    return FT_OK;
}
std::shared_ptr<Session> find_session(ft_session_handle handle) {
    std::scoped_lock lock(sessions_mutex);
    const auto found = sessions.find(handle);
    return found == sessions.end() ? nullptr : found->second;
}
std::shared_ptr<const frametrail::core::FrameSceneEvaluator> find_snapshot(const std::shared_ptr<Session>& session) {
    std::scoped_lock lock(session->snapshot_mutex);
    return session->snapshot;
}
void copy_id(char (&output)[129], const std::string& input) noexcept {
    const auto length = (input.size() < 128) ? input.size() : 128;
    std::memcpy(output, input.data(), length); output[length] = '\0';
}
void copy_frame(ft_media_frame& output, const frametrail::core::MediaFrame& frame) {
    output = {};
    output.local_ticks = frame.local_ticks;
    output.output_rect = {frame.output_rect.x, frame.output_rect.y, frame.output_rect.width, frame.output_rect.height};
    output.mirror = frame.mirror ? 1u : 0u;
    copy_id(output.media_id, frame.media_id);
}
}

uint32_t FT_CALL ft_engine_abi_version(void) { return FT_ABI_VERSION; }
ft_result FT_CALL ft_engine_version(char* utf8, uint32_t capacity, uint32_t* required) {
    return protect([&] {
        const auto result = copy_string("0.1.0", utf8, capacity, required);
        return result == FT_OK ? result : fail(result, "Invalid engine version buffer");
    });
}
ft_result FT_CALL ft_session_create(uint32_t abi_version, ft_session_handle* output) {
    return protect([&]() -> ft_result {
        if (!output) return fail(FT_E_INVALID_ARGUMENT, "Session output must not be null");
        *output = 0;
        if (abi_version != FT_ABI_VERSION) return fail(FT_E_ABI_MISMATCH, "Unsupported engine ABI version");
        auto session = std::make_shared<Session>();
        const auto handle = next_handle.fetch_add(1);
        if (handle == 0 || handle > static_cast<uint64_t>(std::numeric_limits<intptr_t>::max()))
            return fail(FT_E_INTERNAL, "Session handle space exhausted");
        { std::scoped_lock lock(sessions_mutex); sessions.emplace(handle, std::move(session)); }
        *output = handle;
        return FT_OK;
    });
}
ft_result FT_CALL ft_session_destroy(ft_session_handle handle) {
    return protect([&]() -> ft_result {
        std::scoped_lock lock(sessions_mutex);
        if (sessions.erase(handle) == 0) return fail(FT_E_INVALID_HANDLE, "Unknown or already destroyed session");
        return FT_OK;
    });
}
ft_result FT_CALL ft_session_cancel(ft_session_handle handle) {
    return protect([&]() -> ft_result {
        auto session = find_session(handle);
        if (!session) return fail(FT_E_INVALID_HANDLE, "Unknown or destroyed session");
        session->cancelled.store(true);
        return FT_OK;
    });
}
ft_result FT_CALL ft_session_is_cancelled(ft_session_handle handle, uint32_t* cancelled) {
    return protect([&]() -> ft_result {
        if (!cancelled) return fail(FT_E_INVALID_ARGUMENT, "Cancelled output must not be null");
        *cancelled = 0;
        auto session = find_session(handle);
        if (!session) return fail(FT_E_INVALID_HANDLE, "Unknown or destroyed session");
        *cancelled = session->cancelled.load() ? 1u : 0u;
        return FT_OK;
    });
}
ft_result FT_CALL ft_project_validate(const char* utf8, uint32_t length) {
    return protect([&]() -> ft_result {
        if (!utf8 || length == 0 || length > 16u * 1024u * 1024u) return fail(FT_E_INVALID_ARGUMENT, "Invalid project byte buffer");
        (void)frametrail::core::parse_project(std::string_view(utf8, length));
        return FT_OK;
    });
}
ft_result FT_CALL ft_project_edit(const char* utf8, uint32_t length, const char* command, uint32_t command_length, char* output, uint32_t capacity, uint32_t* required) {
    return protect([&]() -> ft_result {
        if (!utf8 || !command || !length || length > 16u * 1024u * 1024u || !command_length || command_length > 16u * 1024u * 1024u) return fail(FT_E_INVALID_ARGUMENT, "Invalid command buffer");
        const auto p = frametrail::core::parse_project(std::string_view(utf8, length));
        const auto result = frametrail::core::serialize_project(frametrail::core::edit_project(p, std::string_view(command, command_length)));
        return copy_string(result.c_str(), output, capacity, required);
    });
}
ft_result FT_CALL ft_project_timeline(const char* utf8, uint32_t length, char* output, uint32_t capacity, uint32_t* required) {
    return protect([&]() -> ft_result {
        if (!utf8 || !length || length > 16u * 1024u * 1024u) return fail(FT_E_INVALID_ARGUMENT, "Invalid project buffer");
        const auto result = frametrail::core::timeline_json(frametrail::core::parse_project(std::string_view(utf8, length)));
        return copy_string(result.c_str(), output, capacity, required);
    });
}
ft_result FT_CALL ft_session_load_project(ft_session_handle handle, const char* utf8, uint32_t length) {
    return protect([&]() -> ft_result {
        if (!utf8 || length == 0 || length > 16u * 1024u * 1024u) return fail(FT_E_INVALID_ARGUMENT, "Invalid project byte buffer");
        auto session = find_session(handle);
        if (!session) return fail(FT_E_INVALID_HANDLE, "Unknown or destroyed session");
        if (session->cancelled.load()) return fail(FT_E_CANCELLED, "Session cancelled");
        auto project = frametrail::core::parse_project(std::string_view(utf8, length));
        const auto revision = project.revision;
        auto project_id = project.project_id;
        auto evaluator = std::make_shared<const frametrail::core::FrameSceneEvaluator>(std::move(project));
        std::scoped_lock lock(session->snapshot_mutex);
        if (session->cancelled.load()) return fail(FT_E_CANCELLED, "Session cancelled");
        if ((!session->project_id.empty() && session->project_id != project_id) || revision <= session->revision)
            return fail(FT_E_STALE_REVISION, "Project identity changed or revision is not newer");
        session->snapshot = std::move(evaluator);
        session->project_id = std::move(project_id);
        session->revision = revision;
        return FT_OK;
    });
}
ft_result FT_CALL ft_session_output_duration(ft_session_handle handle, int64_t* ticks) {
    return protect([&]() -> ft_result {
        if (!ticks) return fail(FT_E_INVALID_ARGUMENT, "Output duration must not be null");
        *ticks = 0;
        auto session = find_session(handle);
        if (!session) return fail(FT_E_INVALID_HANDLE, "Unknown or destroyed session");
        if (session->cancelled.load()) return fail(FT_E_CANCELLED, "Session cancelled");
        const auto snapshot = find_snapshot(session);
        if (!snapshot) return fail(FT_E_NO_SNAPSHOT, "No project loaded");
        *ticks = snapshot->timeline().output_duration();
        return FT_OK;
    });
}
ft_result FT_CALL ft_session_map_output(ft_session_handle handle, int64_t ticks, ft_mapped_time* mapped) {
    return protect([&]() -> ft_result {
        if (!mapped) return fail(FT_E_INVALID_ARGUMENT, "Mapped time output must not be null");
        *mapped = {};
        auto session = find_session(handle);
        if (!session) return fail(FT_E_INVALID_HANDLE, "Unknown or destroyed session");
        if (session->cancelled.load()) return fail(FT_E_CANCELLED, "Session cancelled");
        const auto snapshot = find_snapshot(session);
        if (!snapshot) return fail(FT_E_NO_SNAPSHOT, "No project loaded");
        const auto time = snapshot->timeline().source_time(ticks);
        if (!time) return fail(FT_E_OUT_OF_RANGE, "Output time is outside half-open timeline");
        mapped->ticks = time->ticks; copy_id(mapped->segment_id, time->segment_id);
        return FT_OK;
    });
}
ft_result FT_CALL ft_session_map_source(ft_session_handle handle, int64_t ticks, ft_mapped_time* mapped) {
    return protect([&]() -> ft_result {
        if (!mapped) return fail(FT_E_INVALID_ARGUMENT, "Mapped time output must not be null");
        *mapped = {};
        auto session = find_session(handle);
        if (!session) return fail(FT_E_INVALID_HANDLE, "Unknown or destroyed session");
        if (session->cancelled.load()) return fail(FT_E_CANCELLED, "Session cancelled");
        const auto snapshot = find_snapshot(session);
        if (!snapshot) return fail(FT_E_NO_SNAPSHOT, "No project loaded");
        const auto time = snapshot->timeline().output_time(ticks);
        if (!time) return fail(FT_E_OUT_OF_RANGE, "Source time is deleted or outside half-open source range");
        mapped->ticks = time->ticks; copy_id(mapped->segment_id, time->segment_id);
        return FT_OK;
    });
}
ft_result FT_CALL ft_session_evaluate_scene(ft_session_handle handle, int64_t ticks, ft_frame_scene* scene) {
    return protect([&]() -> ft_result {
        if (!scene) return fail(FT_E_INVALID_ARGUMENT, "Scene output must not be null");
        *scene = {};
        auto session = find_session(handle);
        if (!session) return fail(FT_E_INVALID_HANDLE, "Unknown or destroyed session");
        if (session->cancelled.load()) return fail(FT_E_CANCELLED, "Session cancelled");
        const auto snapshot = find_snapshot(session);
        if (!snapshot) return fail(FT_E_NO_SNAPSHOT, "No project loaded");
        const auto result = snapshot->evaluate(ticks);
        if (!result) return fail(FT_E_OUT_OF_RANGE, "Scene time outside half-open output range");
        scene->revision = result->revision; scene->output_ticks = result->output_ticks; scene->source_ticks = result->source_ticks;
        scene->width = result->width; scene->height = result->height;
        scene->background_r = result->background.r; scene->background_g = result->background.g;
        scene->background_b = result->background.b; scene->background_a = result->background.a;
        copy_id(scene->segment_id, result->segment_id);
        copy_frame(scene->screen, result->screen);
        if (result->camera) { scene->has_camera = 1; copy_frame(scene->camera, *result->camera); }
        return FT_OK;
    });
}
ft_result FT_CALL ft_error_copy(char* utf8, uint32_t capacity, uint32_t* required) {
    return copy_string(last_error, utf8, capacity, required);
}
