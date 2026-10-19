#pragma once
#include <stdint.h>
#ifdef FT_MEDIA_EXPORTS
#define FTM_API __declspec(dllexport)
#else
#define FTM_API __declspec(dllimport)
#endif
extern "C" {
typedef uint64_t ft_media_handle;
typedef struct ft_media_status {
    uint32_t size, stage; // 0 idle, 1 preparing, 2 video/audio output, 3 validating, 4 complete, 5 cancelled, 6 failed
    uint64_t completed, total, last_progress_ms;
    int64_t duration, rendered_ticks, audio_ticks;
    uint32_t software_device, cpu_decode_fallbacks, audio_running;
} ft_media_status;
FTM_API int32_t __cdecl ft_media_create(const char* json, uint32_t length, const wchar_t* root, ft_media_handle* handle);
FTM_API void __cdecl ft_media_destroy(ft_media_handle handle);
// Returns an AddRef'd IDXGISwapChain1. Caller releases the COM reference.
FTM_API int32_t __cdecl ft_media_swapchain(ft_media_handle handle, void** swapchain);
FTM_API int32_t __cdecl ft_media_resize(ft_media_handle handle, uint32_t width, uint32_t height, double scale_x, double scale_y);
// Preview-only quality: 0 matches physical viewport pixels, 1 renders within 4K UHD.
FTM_API int32_t __cdecl ft_media_preview_quality(ft_media_handle handle, uint32_t quality);
FTM_API int32_t __cdecl ft_media_generation(ft_media_handle handle, uint64_t generation);
// Low-frequency authored effect edits only; preserves decoder/device/swapchain.
FTM_API int32_t __cdecl ft_media_update_effects(ft_media_handle handle, const char* json, uint32_t length, uint64_t generation);
// Same-project edits; keeps existing decoders, GPU device and attached swapchain.
FTM_API int32_t __cdecl ft_media_update_project(ft_media_handle handle, const char* json, uint32_t length, uint64_t generation);
FTM_API int32_t __cdecl ft_media_render(ft_media_handle handle, int64_t ticks, uint64_t generation, const wchar_t* png);
FTM_API int32_t __cdecl ft_media_status_get(ft_media_handle handle, ft_media_status* status);
FTM_API int32_t __cdecl ft_media_cancel(ft_media_handle handle);
// Blocking, call on a worker. Atomically replaces target only after finalize and validation.
FTM_API int32_t __cdecl ft_media_export(ft_media_handle handle, const wchar_t* target, uint32_t audio_only);
FTM_API int32_t __cdecl ft_media_export_cpu(ft_media_handle handle, uint32_t force_cpu);
FTM_API int32_t __cdecl ft_media_export_metrics(ft_media_handle handle, char* output, uint32_t capacity, uint32_t* required);
FTM_API int32_t __cdecl ft_media_audio_prepare(ft_media_handle handle, const wchar_t* wav);
FTM_API int32_t __cdecl ft_media_audio_play(ft_media_handle handle, const wchar_t* wav, int64_t ticks);
FTM_API int32_t __cdecl ft_media_audio_stop(ft_media_handle handle);
FTM_API int32_t __cdecl ft_media_audio_stream(ft_media_handle handle, int64_t ticks);
FTM_API int32_t __cdecl ft_media_audio_peaks(ft_media_handle handle, const wchar_t* json);
FTM_API int32_t __cdecl ft_media_edit_geometry(ft_media_handle handle, int64_t ticks, uint32_t crop_mode, char* output, uint32_t capacity, uint32_t* required);
FTM_API int32_t __cdecl ft_media_error(wchar_t* output, uint32_t capacity);
FTM_API int32_t __cdecl ft_media_probe_visual(const wchar_t* path, uint32_t video, uint32_t* width, uint32_t* height, int64_t* duration);
}
