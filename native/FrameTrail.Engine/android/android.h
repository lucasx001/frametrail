#pragma once
#include <stdint.h>
#ifdef FT_ANDROID_EXPORTS
#define FT_ANDROID_API __declspec(dllexport)
#else
#define FT_ANDROID_API __declspec(dllimport)
#endif
extern "C" {
struct FtAndroidStatus {
    uint32_t size, state, width, height, audio_state, fps_limit;
    int64_t first_qpc, qpc_frequency, first_pts_us, last_pts_us;
    uint64_t frames, bytes;
    int64_t minimum_offset_qpc, latest_offset_qpc;
    int32_t error;
    wchar_t message[256];
};
struct FtAndroidSegmentStatus {
    uint32_t size, ready;
    int64_t epoch_qpc, origin_pts_us, duration_ticks;
    uint64_t frames, audio_frames;
    int32_t error;
};
// Loopback only. The caller owns the ADB transport; this module owns media sockets.
FT_ANDROID_API int32_t ft_android_create(uint32_t port, uint32_t audio, uint32_t fps, uint64_t* handle);
FT_ANDROID_API void ft_android_destroy(uint64_t handle);
FT_ANDROID_API int32_t ft_android_status(uint64_t handle, FtAndroidStatus* status);
FT_ANDROID_API int32_t ft_android_begin(uint64_t handle, const wchar_t* video, const wchar_t* audio, uint64_t* segment);
FT_ANDROID_API int32_t ft_android_segment_status(uint64_t handle, uint64_t segment, FtAndroidSegmentStatus* status);
FT_ANDROID_API int32_t ft_android_finish(uint64_t handle, uint64_t segment, int64_t boundary_qpc, FtAndroidSegmentStatus* status);
FT_ANDROID_API int32_t ft_android_swapchain(uint64_t handle, void** swapchain);
FT_ANDROID_API int32_t ft_android_snapshot(uint64_t handle, const wchar_t* png);
}
