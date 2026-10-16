#pragma once
#include <stdint.h>
#include <stddef.h>
#ifdef _WIN32
#ifdef FT_CAPTURE_EXPORTS
#define FT_CAPTURE_API __declspec(dllexport)
#else
#define FT_CAPTURE_API __declspec(dllimport)
#endif
#define FT_CAPTURE_CALL __cdecl
#else
#define FT_CAPTURE_API
#define FT_CAPTURE_CALL
#endif
#ifdef __cplusplus
extern "C" {
#endif
// ABI v1. All structs use natural x64 alignment and caller-provided struct_size.
// Every function returns HRESULT (0 success). No exception crosses the boundary.
// Callers serialize start/pause/resume/stop/destroy; status is thread safe.
typedef void* ft_capture_handle;
enum ft_capture_kind { FT_CAPTURE_MONITOR = 1, FT_CAPTURE_WINDOW = 2, FT_CAPTURE_REGION = 3 };
enum ft_capture_state { FT_CAPTURE_STARTING = 0, FT_CAPTURE_RECORDING = 1, FT_CAPTURE_PAUSED = 2, FT_CAPTURE_FINALIZING = 3, FT_CAPTURE_STOPPED = 4, FT_CAPTURE_FAILED = 5 };
typedef struct ft_capture_source {
    uint32_t struct_size;
    uint32_t kind;
    uintptr_t handle;
    int32_t x, y, width, height; // desktop physical pixels; windows include capture border.
    uint32_t dpi;
    uint32_t hdr; // 0 SDR, 1 HDR, 2 unable to determine (capture fails closed).
    wchar_t name[256];
} ft_capture_source;
typedef struct ft_capture_options {
    uint32_t struct_size;
    uint32_t kind;
    uintptr_t source_handle;
    int32_t crop_x, crop_y, crop_width, crop_height; // monitor-local PHYSICAL pixels for region only.
    uint32_t fps; // 30 or 60; timestamps remain variable rate, no duplicate-frame padding.
    uint32_t bitrate;
    uint32_t queue_capacity; // 2..8; fixed number of owned textures.
    uint32_t allow_software; // limited to <=1920x1080 at <=30 fps.
    const wchar_t* output_path; // new .mp4 file; existing files never overwritten.
} ft_capture_options;
typedef struct ft_capture_status {
    uint32_t struct_size;
    uint32_t state;
    int32_t error_code;
    uint32_t width, height;
    uint32_t hardware_available, hardware_active; // actual writer transform inspected, not inferred from preference.
    uint32_t queue_depth, queue_high_water;
    uint64_t frames_received, frames_written, frames_dropped, frames_rate_limited;
    int64_t qpc_frequency, first_frame_qpc, paused_qpc_ticks, pause_started_qpc;
    int64_t duration_100ns;
    wchar_t message[512];
} ft_capture_status;
typedef struct ft_capture_capabilities {
    uint32_t struct_size;
    uint32_t wgc_supported, hardware_h264_count, software_h264_count;
    int64_t qpc_frequency;
} ft_capture_capabilities;
enum ft_video_codec { FT_VIDEO_H264 = 0, FT_VIDEO_HEVC = 1, FT_VIDEO_AV1 = 2 };
typedef struct ft_video_codec_capabilities {
    uint32_t struct_size, codec, hardware_count, software_count, decoder_count;
} ft_video_codec_capabilities;
// Additive diagnostics API; capture-status ABI remains unchanged.
typedef struct ft_capture_performance {
    uint32_t struct_size, gpu_samples;
    uint64_t converted, conversion_qpc, conversion_max_qpc, writer_qpc, writer_max_qpc;
} ft_capture_performance;
// Enumeration is two-pass; count returns required number; insufficient buffer => HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER).
FT_CAPTURE_API int32_t FT_CAPTURE_CALL ft_capture_enumerate_sources(ft_capture_source* sources, uint32_t capacity, uint32_t* count);
FT_CAPTURE_API int32_t FT_CAPTURE_CALL ft_capture_get_capabilities(ft_capture_capabilities* capabilities);
FT_CAPTURE_API int32_t FT_CAPTURE_CALL ft_capture_get_codec_capabilities(ft_video_codec_capabilities* capabilities);
FT_CAPTURE_API int32_t FT_CAPTURE_CALL ft_capture_start(const ft_capture_options* options, ft_capture_handle* handle);
// Same options/status ABI. No fallback to a different codec when unavailable.
FT_CAPTURE_API int32_t FT_CAPTURE_CALL ft_capture_start_with_codec(const ft_capture_options* options, uint32_t codec, ft_capture_handle* handle);
FT_CAPTURE_API int32_t FT_CAPTURE_CALL ft_capture_pause(ft_capture_handle handle);
FT_CAPTURE_API int32_t FT_CAPTURE_CALL ft_capture_resume(ft_capture_handle handle);
FT_CAPTURE_API int32_t FT_CAPTURE_CALL ft_capture_pause_at(ft_capture_handle handle, int64_t boundary_qpc);
FT_CAPTURE_API int32_t FT_CAPTURE_CALL ft_capture_resume_at(ft_capture_handle handle, int64_t boundary_qpc);
// Diagnostic from the last failed call on this thread; copy before another native call.
FT_CAPTURE_API const wchar_t* FT_CAPTURE_CALL ft_capture_last_error(void);
FT_CAPTURE_API int32_t FT_CAPTURE_CALL ft_capture_stop(ft_capture_handle handle);
FT_CAPTURE_API int32_t FT_CAPTURE_CALL ft_capture_get_status(ft_capture_handle handle, ft_capture_status* status);
FT_CAPTURE_API int32_t FT_CAPTURE_CALL ft_capture_get_performance(ft_capture_handle handle, ft_capture_performance* performance);
FT_CAPTURE_API void FT_CAPTURE_CALL ft_capture_destroy(ft_capture_handle handle);
// Convert selected monitor-local DIP rectangle to inward physical crop. Rejects empty/out-of-bounds/cross-monitor rectangles.
FT_CAPTURE_API int32_t FT_CAPTURE_CALL ft_capture_region_from_dip(uint32_t dpi, double x, double y, double width, double height, int32_t monitor_width, int32_t monitor_height, int32_t* physical_xywh);
#ifdef __cplusplus
}
#endif
