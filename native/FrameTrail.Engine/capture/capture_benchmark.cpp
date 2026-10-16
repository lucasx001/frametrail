#include "capture.h"
#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <atomic>
#include <chrono>
#include <iostream>
#include <thread>

using Microsoft::WRL::ComPtr;
namespace {
void check(HRESULT hr) { if (FAILED(hr)) throw hr; }
LRESULT CALLBACK procedure(HWND window, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_CLOSE) return 0; // The bounded benchmark owns its window lifetime.
    return DefWindowProcW(window, message, w, l);
}
}
// Capture only our own animated test surface. No desktop, microphone or camera is used.
int wmain(int argc, wchar_t** argv) {
    if (argc != 3) { std::wcerr << L"Usage: FrameTrail.Capture.Benchmark SECONDS NEW_OUTPUT.mp4 | --surface SECONDS\n"; return 2; }
    bool surface_only = std::wstring_view(argv[1]) == L"--surface";
    int seconds = _wtoi(argv[surface_only ? 2 : 1]); if (seconds < 2 || seconds > 3600) return 2;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    HWND window{};
    std::thread capture;
    std::atomic<bool> done{}; ft_capture_status result{}; ft_capture_performance performance{sizeof(ft_capture_performance)}; int32_t hr{}; uint64_t rendered{};
    try {
        WNDCLASSW cls{}; cls.hInstance = GetModuleHandleW(nullptr); cls.lpfnWndProc = procedure; cls.lpszClassName = L"FrameTrailCaptureBenchmark";
        if (!RegisterClassW(&cls)) throw HRESULT_FROM_WIN32(GetLastError());
        window = CreateWindowExW(0, cls.lpszClassName, L"FrameTrail · 1080p capture benchmark", WS_POPUP | WS_VISIBLE,
            40, 40, 1920, 1080, nullptr, nullptr, cls.hInstance, nullptr);
        if (!window) throw HRESULT_FROM_WIN32(GetLastError());
        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
        check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context));
        ComPtr<IDXGIDevice> dxgi; check(device.As(&dxgi)); ComPtr<IDXGIAdapter> adapter; check(dxgi->GetAdapter(&adapter));
        ComPtr<IDXGIFactory2> factory; check(adapter->GetParent(IID_PPV_ARGS(&factory)));
        DXGI_SWAP_CHAIN_DESC1 desc{}; desc.Width = 1920; desc.Height = 1080; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1; desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2; desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        ComPtr<IDXGISwapChain1> swap; check(factory->CreateSwapChainForHwnd(device.Get(), window, &desc, nullptr, nullptr, &swap));
        ComPtr<ID3D11Texture2D> back; check(swap->GetBuffer(0, IID_PPV_ARGS(&back)));
        ComPtr<ID3D11RenderTargetView> target; check(device->CreateRenderTargetView(back.Get(), nullptr, &target));
        capture = std::thread([&] {
            if (surface_only) { std::this_thread::sleep_for(std::chrono::seconds(seconds)); done = true; return; }
            ft_capture_options options{}; options.struct_size = sizeof(options); options.kind = FT_CAPTURE_WINDOW;
            options.source_handle = reinterpret_cast<uintptr_t>(window); options.fps = 60; options.bitrate = 10'000'000;
            options.queue_capacity = 4; options.output_path = argv[2];
            ft_capture_handle handle{}; hr = ft_capture_start(&options, &handle);
            if (SUCCEEDED(hr)) {
                std::this_thread::sleep_for(std::chrono::seconds(seconds)); hr = ft_capture_stop(handle);
                result.struct_size = sizeof(result); ft_capture_get_status(handle, &result); ft_capture_get_performance(handle, &performance); ft_capture_destroy(handle);
            } else std::wcerr << ft_capture_last_error() << L"\n";
            done = true;
        });
        while (!done) {
            MSG message{}; while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
            // Distinct changing colors and a moving rectangle reveal duplicates and corruption.
            float color[]{0.07f, 0.12f + static_cast<float>(rendered % 180) / 360, 0.22f, 1}; context->ClearRenderTargetView(target.Get(), color);
            ComPtr<ID3D11DeviceContext1> context1; check(context.As(&context1));
            D3D11_RECT rect{static_cast<LONG>(rendered * 7 % 1700), 200, static_cast<LONG>(rendered * 7 % 1700 + 220), 880};
            float bright[]{0.7f, 0.5f, 0.25f, 1}; context1->ClearView(target.Get(), bright, &rect, 1);
            check(swap->Present(1, 0)); ++rendered;
        }
        capture.join(); DestroyWindow(window); window = nullptr;
        if (surface_only) return 0;
        if (FAILED(hr)) std::wcerr << result.message << L"\n";
        double duration = result.duration_100ns / 10'000'000.0;
        std::cout << "{\"hr\":" << hr << ",\"width\":" << result.width << ",\"height\":" << result.height
            << ",\"rendered\":" << rendered << ",\"received\":" << result.frames_received << ",\"written\":" << result.frames_written
            << ",\"dropped\":" << result.frames_dropped << ",\"rateLimited\":" << result.frames_rate_limited
            << ",\"queueHigh\":" << result.queue_high_water << ",\"hardware\":" << result.hardware_active
            << ",\"durationSeconds\":" << duration << ",\"fps\":" << (duration > 0 ? result.frames_written / duration : 0)
            << ",\"gpuSamples\":" << performance.gpu_samples
            << ",\"conversionMeanMs\":" << (performance.converted ? performance.conversion_qpc * 1000.0 / result.qpc_frequency / performance.converted : 0)
            << ",\"conversionMaxMs\":" << performance.conversion_max_qpc * 1000.0 / result.qpc_frequency
            << ",\"writerMeanMs\":" << (result.frames_written > 1 ? performance.writer_qpc * 1000.0 / result.qpc_frequency / (result.frames_written - 1) : 0)
            << ",\"writerMaxMs\":" << performance.writer_max_qpc * 1000.0 / result.qpc_frequency << "}\n";
        return FAILED(hr) || !result.frames_written ? 1 : 0;
    } catch (HRESULT hr) {
        if (capture.joinable()) capture.join(); if (window) DestroyWindow(window);
        std::cerr << "HRESULT=" << std::hex << static_cast<uint32_t>(hr) << '\n'; return 1;
    }
}
