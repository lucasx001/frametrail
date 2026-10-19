#pragma once
#include "media.h"
#include "frametrail/core/scene.hpp"
#include <windows.h>
#include <d3d11_4.h>
#include <d3d10.h>
#include <dxgi1_3.h>
#include <d2d1_1.h>
#include <dwrite.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <winrt/base.h>
#include <nlohmann/json.hpp>
#include <atomic>
#include <filesystem>
#include <fstream>
#include <functional>
#include <mutex>
#include <thread>
#include <unordered_map>
#include <vector>

namespace frametrail::media {
using Microsoft::WRL::ComPtr;
using winrt::check_hresult;
using namespace frametrail::core;
using Json = nlohmann::json;
inline constexpr DWORD video_stream_id = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), audio_stream_id = static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), all_streams_id = static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), media_source_id = static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE);
inline constexpr int sample_rate = 48000, channels = 2;
inline constexpr HRESULT cancelled_hr = HRESULT_FROM_WIN32(ERROR_CANCELLED);
struct Apartment {
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    Apartment() { if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) check_hresult(hr); }
    ~Apartment() { if (SUCCEEDED(hr)) CoUninitialize(); }
};
struct MfScope { MfScope() { check_hresult(MFStartup(MF_VERSION)); } ~MfScope() { MFShutdown(); } };
inline int64_t sample_ticks(int64_t s) { return s / sample_rate * time_scale + s % sample_rate * time_scale / sample_rate; }
inline int64_t ticks_samples(int64_t t) { return t / time_scale * sample_rate + t % time_scale * sample_rate / time_scale; }
std::filesystem::path safe_path(const std::filesystem::path& root, const std::string& path);
void write_png(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* texture, const std::filesystem::path& path);
struct VideoReader {
    ComPtr<IMFSourceReader> reader;
    ComPtr<IMFSample> current, next;
    int64_t current_time{-1}, next_time{-1}, request{-1}, origin{}, duration{};
    uint32_t width{}, height{}; bool eof{}, cpu{};
    bool matrix709{}, full_range{};
    ComPtr<ID3D11Texture2D> upload, converted;
    ComPtr<ID3D11VideoProcessorEnumerator> video_enum;
    ComPtr<ID3D11VideoProcessor> processor;
    ComPtr<ID3D11VideoProcessorOutputView> converted_view;
    VideoReader(const std::filesystem::path& path, IMFDXGIDeviceManager* manager);
    ComPtr<ID3D11Texture2D> frame(int64_t ticks, ID3D11Device* device, ID3D11DeviceContext* context);
    ComPtr<ID3D11Texture2D> to_rgb(ID3D11Device* device, ID3D11DeviceContext* context);
};
struct PointerSample { int64_t ticks; float x, y; uint32_t shape; bool visible; uint32_t kind{}; int64_t activity{}; };
struct PointerTrack {
    std::vector<PointerSample> points;
    std::vector<FocusPoint> smooth_points;
    struct Shape { std::string file; float x{}, y{}; };
    std::unordered_map<uint32_t, Shape> shapes;
};
class VisualGpu;
struct Renderer {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IMFDXGIDeviceManager> manager;
    ComPtr<IDXGISwapChain1> swapchain;
    ComPtr<ID2D1Factory1> factory;
    ComPtr<ID2D1Device> d2device;
    ComPtr<ID2D1DeviceContext> d2;
    ComPtr<ID3D11Texture2D> output;
    ComPtr<ID3D11Texture2D> composition, screen_plane, camera_plane;
    std::unique_ptr<VisualGpu> visuals;
    std::unique_ptr<VideoReader> backdrop_reader;
    std::string backdrop_path;
    ComPtr<IDWriteFactory> text_factory;
    std::unordered_map<std::string, std::unique_ptr<VideoReader>> readers;
    std::unordered_map<std::string, PointerTrack> pointers;
    std::unordered_map<std::wstring, ComPtr<ID2D1Bitmap1>> images;
    std::filesystem::path root;
    uint32_t width{}, height{}, software{}, fallbacks{};
    uint32_t viewport_width{}, viewport_height{};
    bool preview_4k{};
    Json cached_settings = Json::object(); const Project* settings_source{};
    Renderer(const std::filesystem::path& path);
    ~Renderer();
    void resize(uint32_t w, uint32_t h, double sx, double sy);
    ID3D11Texture2D* render(const Project& project, const FrameScene& scene, uint32_t w, uint32_t h);
    void present();
    void clear_decoders() { readers.clear(); }
private:
    ComPtr<ID2D1Bitmap1> bitmap(ID3D11Texture2D* texture, bool target = false, bool alpha = false);
    ComPtr<ID2D1Bitmap1> image(const std::filesystem::path& path);
    void layer(const Project& project, const MediaFrame& frame, float radius, bool shadow, const Json& settings, bool screen, const VisualScene& visual);
    void frame_chrome(D2D1_RECT_F content, std::string_view style, double canvas_short, const Json& settings);
    void ensure_surface(ComPtr<ID3D11Texture2D>& texture, uint32_t w, uint32_t h);
    void background(const Project& project, const FrameScene& scene);
    void stickers(const FrameScene& scene, bool attached);
};
struct AudioReader {
    ComPtr<IMFSourceReader> reader; std::vector<float> data;
    int64_t start{}, count{};
    explicit AudioReader(const std::filesystem::path& path);
    void read(int64_t first, int64_t frames);
    float at(int64_t sample, int channel) const;
};
// Bounded streaming WSOLA, stereo-coherent, no third-party audio dependency.
std::vector<float> stretch_wsola(const std::vector<float>& input, double rate, size_t output_frames);
struct Session {
    MfScope mf;
    Project project; FrameSceneEvaluator evaluator;
    std::atomic_int64_t output_duration;
    std::filesystem::path root;
    std::unique_ptr<Renderer> renderer;
    std::mutex mutex, audio_mutex;
    std::atomic_bool cancelled{};
    std::atomic_uint64_t generation{}, completed{}, total{}, last_progress{GetTickCount64()};
    std::atomic_uint32_t stage{};
    std::atomic_int64_t rendered_ticks{-1};
    std::atomic_int64_t audio_ticks{-1};
    std::atomic_uint32_t audio_running{}, software{}, cpu_fallback{};
    bool export_force_cpu{};
    std::atomic_uint32_t export_gpu_rejection{};
    std::atomic_uint64_t export_gpu_frames{}, export_cpu_frames{}, export_convert_us{}, export_readback_us{}, export_write_us{}, export_wait_us{};
    std::thread audio_thread; std::atomic_bool audio_stop{};
    explicit Session(Project p, std::filesystem::path path) : project(std::move(p)), evaluator(project), output_duration(evaluator.timeline().output_duration()), root(std::move(path)) { }
    ~Session();
    Renderer& gpu() { if (!renderer) renderer = std::make_unique<Renderer>(root); return *renderer; }
    void check_cancel() { if (cancelled) winrt::throw_hresult(cancelled_hr); }
    void progress(uint64_t n, uint64_t all) { completed = n; total = all; last_progress = GetTickCount64(); }
    void stop_audio();
    void stream_audio(int64_t ticks);
    void mix_pcm(int64_t first, int64_t frames, const std::function<void(const float*, size_t)>& emit);
    void prepare_peaks(const std::filesystem::path& output);
    void mix_audio(const std::filesystem::path& output);
    void export_file(const std::filesystem::path& target, bool audio_only);
};
}

// Preserve the failing media operation in native diagnostics instead of exposing
// an unhelpful E_INVALIDARG alone. All exceptions are caught at the C ABI.
#define FT_MEDIA_CHECK(expression) do { const HRESULT ft_hr_value = (expression); if (FAILED(ft_hr_value)) throw winrt::hresult_error(ft_hr_value, winrt::to_hstring(#expression)); } while (false)
