#include "capture.h"
#include <windows.h>
#include <chrono>
#include <iostream>
#include <thread>
#include <vector>
#include <string>
#include <algorithm>
#include <io.h>
#include <fcntl.h>

int wmain(int argc, wchar_t** argv) {
    _setmode(_fileno(stdout), _O_U8TEXT); _setmode(_fileno(stderr), _O_U8TEXT);
    std::wcout << std::unitbuf;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    ft_capture_capabilities caps{}; caps.struct_size = sizeof(caps);
    int32_t hr = ft_capture_get_capabilities(&caps);
    std::wcout << L"Capabilities hr=" << std::hex << static_cast<uint32_t>(hr) << std::dec
        << L" WGC=" << caps.wgc_supported << L" hardwareH264=" << caps.hardware_h264_count
        << L" softwareH264=" << caps.software_h264_count << L" QPC=" << caps.qpc_frequency << L"\n";
    uint32_t count{}; hr = ft_capture_enumerate_sources(nullptr, 0, &count);
    if (hr < 0) return 1;
    std::vector<ft_capture_source> sources(count);
    hr = ft_capture_enumerate_sources(sources.data(), count, &count);
    if (hr < 0) return 1;
    for (uint32_t i = 0; i < count; ++i) {
        auto const& s = sources[i];
        std::wcout << i << L": kind=" << s.kind << L" handle=" << std::hex << s.handle << std::dec
            << L" bounds=" << s.x << L"," << s.y << L"," << s.width << L"," << s.height
            << L" dpi=" << s.dpi << L" hdr=" << s.hdr << L" " << s.name << L"\n";
    }
    if (argc < 4) {
        std::wcout << L"Usage: FrameTrail.Capture.Smoke.exe SOURCE_INDEX SECONDS OUTPUT.mp4 [--region x y w h] [--pause] [--fps60]\n"
            << L"Region coordinates are monitor-local physical pixels. --pause pauses 2 seconds at midpoint. No audio is recorded.\n";
        return 0;
    }
    const int index = _wtoi(argv[1]); const int seconds = _wtoi(argv[2]);
    if (index < 0 || static_cast<uint32_t>(index) >= count || seconds < 1 || seconds > 3600) return 2;
    ft_capture_options options{}; options.struct_size = sizeof(options); options.kind = sources[index].kind;
    options.source_handle = sources[index].handle; options.fps = 30; options.bitrate = 10000000;
    options.queue_capacity = 4; options.allow_software = 1; options.output_path = argv[3];
    bool pause = false;
    for (int i = 4; i < argc; ++i) {
        std::wstring arg = argv[i];
        if (arg == L"--pause") pause = true;
        else if (arg == L"--fps60") options.fps = 60;
        else if (arg == L"--region" && i + 4 < argc) {
            if (options.kind != FT_CAPTURE_MONITOR) return 2;
            options.kind = FT_CAPTURE_REGION; options.crop_x = _wtoi(argv[++i]); options.crop_y = _wtoi(argv[++i]);
            options.crop_width = _wtoi(argv[++i]); options.crop_height = _wtoi(argv[++i]);
        } else return 2;
    }
    ft_capture_handle handle{}; hr = ft_capture_start(&options, &handle);
    if (hr < 0) { std::wcerr << L"start failed HRESULT=0x" << std::hex << static_cast<uint32_t>(hr) << L" " << ft_capture_last_error() << L"\n"; return 3; }
    bool failed = false;
    for (int i = 0; i < seconds; ++i) {
        std::this_thread::sleep_for(std::chrono::seconds(1));
        ft_capture_status status{}; status.struct_size = sizeof(status); ft_capture_get_status(handle, &status);
        std::wcout << L"second=" << i+1 << L" state=" << status.state << L" size=" << status.width << L"x" << status.height
            << L" received=" << status.frames_received << L" written=" << status.frames_written << L" drop=" << status.frames_dropped
            << L" rateLimited=" << status.frames_rate_limited << L" queueHigh=" << status.queue_high_water
            << L" firstQpc=" << status.first_frame_qpc << L" hardware=" << status.hardware_active
            << L" error=" << std::hex << static_cast<uint32_t>(status.error_code) << std::dec << L" " << status.message << std::endl;
        if (status.state == FT_CAPTURE_FAILED) { failed = true; break; }
        if (pause && i == seconds / 2) {
            hr = ft_capture_pause(handle); if (hr < 0) { failed = true; break; }
            std::this_thread::sleep_for(std::chrono::seconds(2));
            hr = ft_capture_resume(handle); if (hr < 0) { failed = true; break; }
        }
    }
    hr = ft_capture_stop(handle);
    ft_capture_status status{}; status.struct_size = sizeof(status); ft_capture_get_status(handle, &status);
    std::wcout << L"final hr=" << std::hex << static_cast<uint32_t>(hr) << std::dec << L" state=" << status.state
        << L" frames=" << status.frames_written << L" duration100ns=" << status.duration_100ns
        << L" pausedQpc=" << status.paused_qpc_ticks << L" error=" << status.message << L"\n";
    ft_capture_destroy(handle); return failed || hr < 0 || status.frames_written == 0 ? 4 : 0;
}
