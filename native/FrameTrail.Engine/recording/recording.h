#pragma once
#include <stdint.h>
#ifdef FT_RECORDING_EXPORTS
#define FT_RECORDING_API __declspec(dllexport)
#else
#define FT_RECORDING_API __declspec(dllimport)
#endif
#ifdef __cplusplus
extern "C" {
#endif
typedef void* ft_camera_handle;
typedef struct ft_camera_device { wchar_t id[1024]; wchar_t name[256]; } ft_camera_device;
typedef struct ft_camera_format { uint32_t width, height, fps_numerator, fps_denominator; } ft_camera_format;
typedef struct ft_camera_options { uint32_t size; const wchar_t* device_id; const wchar_t* output_path; uint32_t width, height, fps_numerator, fps_denominator; } ft_camera_options;
typedef struct ft_camera_status {
    uint32_t size, state; int32_t error_code; uint32_t width, height;
    uint64_t frames_written, frames_dropped; int64_t first_offset_100ns, duration_100ns;
    wchar_t message[256];
} ft_camera_status;
FT_RECORDING_API int32_t __cdecl ft_camera_enumerate(ft_camera_device*, uint32_t, uint32_t*);
FT_RECORDING_API int32_t __cdecl ft_camera_formats(const wchar_t*, ft_camera_format*, uint32_t, uint32_t*);
FT_RECORDING_API int32_t __cdecl ft_camera_start(const ft_camera_options*, ft_camera_handle*);
FT_RECORDING_API int32_t __cdecl ft_camera_set_epoch(ft_camera_handle, int64_t qpc, int64_t frequency);
FT_RECORDING_API int32_t __cdecl ft_camera_stop_at(ft_camera_handle, int64_t qpc);
FT_RECORDING_API int32_t __cdecl ft_camera_get_status(ft_camera_handle, ft_camera_status*);
// Diagnostics: overlapping segments must retain the same device-source identity.
FT_RECORDING_API int32_t __cdecl ft_camera_get_source_identity(ft_camera_handle, uint64_t*);
FT_RECORDING_API void __cdecl ft_camera_destroy(ft_camera_handle);
typedef void* ft_camera_preview_handle;
FT_RECORDING_API int32_t __cdecl ft_camera_preview_start(const wchar_t*, const ft_camera_format*, ft_camera_preview_handle*);
// Latest immutable BGRA frame, top-down. S_FALSE means no new frame yet.
FT_RECORDING_API int32_t __cdecl ft_camera_preview_copy(ft_camera_preview_handle, uint8_t*, uint32_t, int64_t*, uint64_t*);
FT_RECORDING_API void __cdecl ft_camera_preview_destroy(ft_camera_preview_handle);
// Decode at least one video/audio sample and obtain container duration; used before accepting recovery media.
FT_RECORDING_API int32_t __cdecl ft_recording_probe(const wchar_t* path, uint32_t video, int64_t* duration_100ns);
// Pointer sampling + click events on a dedicated message thread. JSONL and cursor PNG assets stay native.
typedef void* ft_pointer_handle;
typedef struct ft_pointer_options {
    uint32_t size, kind; uintptr_t source_handle;
    int32_t x, y, width, height; // display-local region for kind=3; kind=1 whole monitor; kind=2 moving window
    int64_t epoch_qpc, qpc_frequency;
    const wchar_t* output_path; const wchar_t* shapes_directory;
} ft_pointer_options;
typedef struct ft_pointer_status { uint32_t size; int32_t error_code; uint64_t events_written, events_dropped; } ft_pointer_status;
FT_RECORDING_API int32_t __cdecl ft_pointer_start(const ft_pointer_options*, ft_pointer_handle*);
FT_RECORDING_API int32_t __cdecl ft_pointer_stop_at(ft_pointer_handle, int64_t);
FT_RECORDING_API int32_t __cdecl ft_pointer_get_status(ft_pointer_handle, ft_pointer_status*);
FT_RECORDING_API void __cdecl ft_pointer_destroy(ft_pointer_handle);
FT_RECORDING_API int32_t __cdecl ft_recording_exclude_window(uintptr_t hwnd, uint32_t exclude);
// RegisterHotKey is thread-affine. UI calls register/unregister on the same dispatcher and handles WM_HOTKEY.
FT_RECORDING_API int32_t __cdecl ft_recording_hotkey(uintptr_t hwnd, int32_t id, uint32_t modifiers, uint32_t key, uint32_t enable);
#ifdef __cplusplus
}
#endif
