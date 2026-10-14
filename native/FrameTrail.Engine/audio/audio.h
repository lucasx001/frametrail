#pragma once
#include <stdint.h>
#ifdef FT_AUDIO_EXPORTS
#define FT_AUDIO_API __declspec(dllexport)
#else
#define FT_AUDIO_API __declspec(dllimport)
#endif
#define FT_AUDIO_CALL __cdecl
#ifdef __cplusplus
extern "C" {
#endif
typedef void* ft_audio_handle;
enum ft_audio_kind { FT_AUDIO_SYSTEM = 1, FT_AUDIO_MICROPHONE = 2, FT_AUDIO_PROCESS_TREE = 3 };
enum ft_audio_state { FT_AUDIO_PREPARING = 0, FT_AUDIO_RECORDING = 1, FT_AUDIO_PAUSED = 2, FT_AUDIO_STOPPED = 3, FT_AUDIO_FAILED = 4 };
typedef struct ft_audio_device {
    uint32_t struct_size, kind, is_default;
    wchar_t id[1024];
    wchar_t name[256];
} ft_audio_device;
typedef struct ft_audio_options {
    uint32_t struct_size, kind, process_id;
    const wchar_t* device_id; // null = pin the current default; never silently switches devices
    const wchar_t* output_path; // new IEEE float WAV; caller owns project-relative path
    const wchar_t* diagnostics_path; // optional new JSONL; no audio samples logged
} ft_audio_options;
typedef struct ft_audio_status {
    uint32_t struct_size, state;
    int32_t error_code;
    uint32_t sample_rate, channels, native_sample_rate, native_channels;
    uint32_t default_device_changed, discontinuities, timestamp_errors, drift_available;
    uint64_t packets, frames_written, silent_frames, trimmed_frames, correction_frames;
    int64_t epoch_qpc, qpc_frequency, duration_100ns;
    double peak, rms, drift_ppm, max_timestamp_error_ms;
    wchar_t message[512];
} ft_audio_status;
// HRESULT, no C++ exceptions cross this ABI. Serialize mutations per handle; get_status is thread safe.
// Start runs a dedicated MTA capture worker; before set_epoch, at most 2 seconds of preroll are retained.
FT_AUDIO_API int32_t FT_AUDIO_CALL ft_audio_enumerate_devices(uint32_t kind, ft_audio_device* devices, uint32_t capacity, uint32_t* count);
FT_AUDIO_API int32_t FT_AUDIO_CALL ft_audio_process_from_window(uintptr_t hwnd, uint32_t* process_id);
FT_AUDIO_API int32_t FT_AUDIO_CALL ft_audio_process_loopback_supported(uint32_t* supported);
FT_AUDIO_API int32_t FT_AUDIO_CALL ft_audio_start(const ft_audio_options* options, ft_audio_handle* handle);
FT_AUDIO_API int32_t FT_AUDIO_CALL ft_audio_set_epoch(ft_audio_handle handle, int64_t first_video_qpc, int64_t frequency);
// Boundaries use the same raw QPC values as screen capture. Never pass WASAPI's 100 ns QPC position here.
FT_AUDIO_API int32_t FT_AUDIO_CALL ft_audio_pause_at(ft_audio_handle handle, int64_t qpc);
FT_AUDIO_API int32_t FT_AUDIO_CALL ft_audio_resume_at(ft_audio_handle handle, int64_t qpc);
FT_AUDIO_API int32_t FT_AUDIO_CALL ft_audio_stop_at(ft_audio_handle handle, int64_t qpc);
FT_AUDIO_API int32_t FT_AUDIO_CALL ft_audio_get_status(ft_audio_handle handle, ft_audio_status* status);
FT_AUDIO_API void FT_AUDIO_CALL ft_audio_destroy(ft_audio_handle handle);
#ifdef __cplusplus
}
#endif
