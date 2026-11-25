#include "media.h"
#include "subtitle_renderer.hpp"
#include "pcm_wave.hpp"
#include <windows.h>
#include <d3d11.h>
#include <d2d1_1.h>
#include <d2d1_1helper.h>
#include <dwrite.h>
#include <wincodec.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>
#pragma comment(lib, "mfuuid.lib")
#include <nlohmann/json.hpp>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
using namespace frametrail::core;
using namespace frametrail::media;
using Json = nlohmann::json;

namespace {

int checks = 0;

void expect(bool condition, const char* message) {
    ++checks;
    if (!condition) throw std::runtime_error(message);
}

struct ScopedFile {
    std::filesystem::path path;
    ~ScopedFile() {
        std::error_code ec;
        std::filesystem::remove(path, ec);
    }
};

struct ScopedDir {
    std::filesystem::path path;
    ~ScopedDir() {
        std::error_code ec;
        std::filesystem::remove_all(path, ec);
    }
};

void write_test_wav_48k_stereo_fn(const std::filesystem::path& path, uint64_t frames, const std::function<double(uint64_t)>& fn) {
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    expect(static_cast<bool>(out), "open temp 48k WAV for writing");
    write_pcm_header(out, frames);
    std::vector<int16_t> samples(static_cast<size_t>(frames) * 2);
    for (uint64_t i = 0; i < frames; ++i) {
        const double v = fn(i);
        const int16_t s = static_cast<int16_t>(std::lround(std::clamp(v, -1.0, 1.0) * 32767.0));
        samples[static_cast<size_t>(i) * 2] = s;
        samples[static_cast<size_t>(i) * 2 + 1] = s;
    }
    out.write(reinterpret_cast<const char*>(samples.data()), static_cast<std::streamsize>(samples.size() * sizeof(int16_t)));
    out.flush();
    expect(static_cast<bool>(out), "flush temp 48k WAV");
}

void write_test_wav_48k_stereo(const std::filesystem::path& path, uint64_t frames, double freq_hz, double amplitude) {
    constexpr double pi = 3.14159265358979323846;
    write_test_wav_48k_stereo_fn(path, frames, [=](uint64_t i) {
        return amplitude * std::sin(2.0 * pi * freq_hz * static_cast<double>(i) / 48000.0);
    });
}

struct WavInfo {
    uint16_t format{};
    uint16_t channels{};
    uint32_t sample_rate{};
    uint32_t byte_rate{};
    uint16_t block_align{};
    uint16_t bits_per_sample{};
    std::vector<int16_t> samples;
};

WavInfo read_wav_16bit(const std::filesystem::path& path) {
    std::ifstream in(path, std::ios::binary);
    expect(static_cast<bool>(in), "open extracted 16k WAV");
    char riff[12]{};
    in.read(riff, 12);
    expect(std::memcmp(riff, "RIFF", 4) == 0 && std::memcmp(riff + 8, "WAVE", 4) == 0, "valid RIFF WAVE header");
    WavInfo info;
    bool got_fmt = false, got_data = false;
    while (in && (!got_fmt || !got_data)) {
        char id[4]{};
        uint32_t size{};
        in.read(id, 4);
        in.read(reinterpret_cast<char*>(&size), 4);
        if (!in) break;
        if (std::memcmp(id, "fmt ", 4) == 0) {
            expect(size >= 16, "fmt chunk >= 16 bytes");
            in.read(reinterpret_cast<char*>(&info.format), 2);
            in.read(reinterpret_cast<char*>(&info.channels), 2);
            in.read(reinterpret_cast<char*>(&info.sample_rate), 4);
            in.read(reinterpret_cast<char*>(&info.byte_rate), 4);
            in.read(reinterpret_cast<char*>(&info.block_align), 2);
            in.read(reinterpret_cast<char*>(&info.bits_per_sample), 2);
            if (size > 16) in.seekg(static_cast<std::streamoff>(size - 16), std::ios::cur);
            got_fmt = true;
        } else if (std::memcmp(id, "data", 4) == 0) {
            expect(size % 2 == 0, "data chunk aligned to 16-bit samples");
            info.samples.resize(size / 2);
            if (size > 0) in.read(reinterpret_cast<char*>(info.samples.data()), static_cast<std::streamsize>(size));
            got_data = true;
        } else {
            in.seekg(static_cast<std::streamoff>(size) + (size & 1), std::ios::cur);
        }
    }
    expect(got_fmt && got_data, "WAV contains fmt and data chunks");
    return info;
}

void test_audio_extract_16k() {
    const auto temp_dir = std::filesystem::temp_directory_path();
    const auto suffix = std::to_wstring(GetCurrentProcessId());
    ScopedFile src{temp_dir / (L"ft-subtest-src-" + suffix + L".wav")};
    ScopedFile out_full{temp_dir / (L"ft-subtest-16k-full-" + suffix + L".wav")};
    ScopedFile out_slice{temp_dir / (L"ft-subtest-16k-slice-" + suffix + L".wav")};

    write_test_wav_48k_stereo(src.path, 48000, 440.0, 0.6);

    // Invalid arguments rejected cleanly
    expect(ft_media_extract_audio_pcm16k(nullptr, out_full.path.c_str(), 0, -1) < 0, "null source rejected");
    expect(ft_media_extract_audio_pcm16k(src.path.c_str(), nullptr, 0, -1) < 0, "null output rejected");
    expect(ft_media_extract_audio_pcm16k(src.path.c_str(), out_full.path.c_str(), -1, -1) < 0, "negative start rejected");

    // Full 1.0s extraction
    const int32_t hr_full = ft_media_extract_audio_pcm16k(src.path.c_str(), out_full.path.c_str(), 0, -1);
    expect(hr_full == S_OK, "ft_media_extract_audio_pcm16k full extraction S_OK");
    const auto full = read_wav_16bit(out_full.path);
    expect(full.format == 1, "PCM format == 1");
    expect(full.channels == 1, "mono channels == 1");
    expect(full.sample_rate == 16000, "sample_rate == 16000");
    expect(full.byte_rate == 32000, "byte_rate == 32000");
    expect(full.block_align == 2, "block_align == 2");
    expect(full.bits_per_sample == 16, "bits_per_sample == 16");
    expect(full.samples.size() >= 15900 && full.samples.size() <= 16100, "1.0s audio yields ~16000 samples");

    int16_t peak = 0;
    int zero_crossings = 0;
    for (size_t i = 1; i < full.samples.size(); ++i) {
        const int16_t a = full.samples[i - 1], b = full.samples[i];
        peak = std::max<int16_t>(peak, static_cast<int16_t>(std::abs(b)));
        if ((a < 0 && b >= 0) || (a >= 0 && b < 0)) ++zero_crossings;
    }
    expect(peak > 15000 && peak < 23000, "440Hz tone amplitude preserved after 3:1 anti-aliased decimation");
    expect(zero_crossings > 840 && zero_crossings < 920, "440Hz tone frequency preserved at 16kHz");

    // Ranged 0.5s extraction [0.25s, 0.75s)
    const int32_t hr_slice = ft_media_extract_audio_pcm16k(src.path.c_str(), out_slice.path.c_str(), 2'500'000, 5'000'000);
    expect(hr_slice == S_OK, "ft_media_extract_audio_pcm16k ranged extraction S_OK");
    const auto slice = read_wav_16bit(out_slice.path);
    expect(slice.sample_rate == 16000 && slice.channels == 1, "sliced WAV is 16kHz mono");
    expect(slice.samples.size() >= 7900 && slice.samples.size() <= 8100, "0.5s slice yields ~8000 samples");
}

void test_subtitle_renderer() {
    ComPtr<IDWriteFactory> write_factory;
    expect(SUCCEEDED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(write_factory.GetAddressOf()))), "create DWrite factory");

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context);
    expect(SUCCEEDED(hr), "create WARP D3D11 device for subtitle render smoke test");

    ComPtr<ID2D1Factory1> d2_factory;
    expect(SUCCEEDED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, IID_PPV_ARGS(&d2_factory))), "create D2D1 factory");
    ComPtr<IDXGIDevice> dxgi;
    expect(SUCCEEDED(device.As(&dxgi)), "QI IDXGIDevice");
    ComPtr<ID2D1Device> d2_device;
    expect(SUCCEEDED(d2_factory->CreateDevice(dxgi.Get(), &d2_device)), "create D2D1 device");
    ComPtr<ID2D1DeviceContext> d2;
    expect(SUCCEEDED(d2_device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &d2)), "create D2D1 device context");

    constexpr uint32_t W = 640, H = 360;
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = W;
    desc.Height = H;
    desc.MipLevels = desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> rt;
    expect(SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &rt)), "create BGRA render target");

    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;
    expect(SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &staging)), "create staging texture");

    ComPtr<IDXGISurface> surface;
    expect(SUCCEEDED(rt.As(&surface)), "QI IDXGISurface");
    ComPtr<ID2D1Bitmap1> target_bmp;
    auto props = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET, D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
    expect(SUCCEEDED(d2->CreateBitmapFromDxgiSurface(surface.Get(), &props, &target_bmp)), "create D2D target bitmap");

    VisualEffect cue;
    cue.id = "sub-render-1";
    cue.kind = "subtitle";
    cue.start = 10'000'000;
    cue.duration = 30'000'000;
    cue.opacity = 1.0;
    cue.text = "Hello FrameTrail 字幕";
    cue.secondary_text = "Bilingual subtitle check";
    cue.words = {
        {"Hello", 0.0, 0.8},
        {"FrameTrail", 0.8, 1.8},
        {"字幕", 1.8, 3.0}
    };

    FrameScene scene;
    scene.width = 1920;
    scene.height = 1080;
    scene.source_ticks = 22'000'000; // 1.2s into cue -> "FrameTrail" active!
    scene.visual.subtitles = {cue};

    auto render_and_count = [&](const Json& style_obj, size_t& non_black, size_t& yellow_pixels) {
        Json editor{{"subtitleStyle", style_obj}};
        const auto style = parse_subtitle_style(editor);
        const auto rect = measure_subtitle_rect(write_factory.Get(), cue, style, scene.width, scene.height);
        expect(rect.width > 0.05 && rect.height > 0.03 && rect.x >= 0.0 && rect.y >= 0.0 && rect.x + rect.width <= 1.0001 && rect.y + rect.height <= 1.0001,
            "measure_subtitle_rect returns valid normalized canvas rect");

        d2->SetTarget(target_bmp.Get());
        d2->SetTransform(D2D1::Matrix3x2F::Scale(static_cast<float>(W) / scene.width, static_cast<float>(H) / scene.height));
        d2->BeginDraw();
        d2->Clear(D2D1::ColorF(0.0f, 0.0f, 0.0f, 1.0f));
        draw_subtitles(d2_factory.Get(), d2.Get(), write_factory.Get(), editor, scene);
        const HRESULT end_hr = d2->EndDraw();
        d2->SetTarget(nullptr);
        expect(SUCCEEDED(end_hr), "D2D EndDraw succeeded for subtitle render");

        context->CopyResource(staging.Get(), rt.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        expect(SUCCEEDED(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)), "map staging readback");
        non_black = 0;
        yellow_pixels = 0;
        const auto* base = static_cast<const uint8_t*>(mapped.pData);
        for (uint32_t y = 0; y < H; ++y) {
            const uint8_t* row = base + static_cast<size_t>(y) * mapped.RowPitch;
            for (uint32_t x = 0; x < W; ++x) {
                const uint8_t b = row[x * 4 + 0];
                const uint8_t g = row[x * 4 + 1];
                const uint8_t r = row[x * 4 + 2];
                if (r > 15 || g > 15 || b > 15) ++non_black;
                if (r > 180 && g > 140 && b < 90) ++yellow_pixels;
            }
        }
        context->Unmap(staging.Get(), 0);
    };

    size_t non_black = 0, yellow = 0;
    // 1. Classic outline
    render_and_count(Json{{"preset", "classicOutline"}, {"outlineWidth", 3.5}, {"shadowEnabled", true}}, non_black, yellow);
    expect(non_black > 500, "classicOutline renders non-empty subtitle pixels");

    // 2. Dark pill background
    render_and_count(Json{{"preset", "darkPill"}, {"backgroundColor", "#1E293B"}, {"backgroundOpacity", 0.85}, {"backgroundRadius", 16.0}}, non_black, yellow);
    expect(non_black > 2000, "darkPill renders background pill + text pixels");

    // 3. Karaoke wordColor highlight
    render_and_count(Json{{"preset", "karaokePop"}, {"highlightColor", "#FACC15"}, {"highlightMode", "wordColor"}}, non_black, yellow);
    expect(non_black > 500 && yellow > 40, "wordColor karaoke renders yellow highlighted word pixels");

    // 4. Karaoke wordPill + bilingual secondary line
    render_and_count(Json{{"preset", "bilingualClean"}, {"highlightColor", "#FACC15"}, {"highlightMode", "wordPill"}, {"bilingualEnabled", true}, {"secondaryScale", 0.72}}, non_black, yellow);
    expect(non_black > 1000 && yellow > 100, "wordPill + bilingual renders highlight pill and dual lines");
}

double wav_rms_range(const WavInfo& wav, size_t start_frame, size_t end_frame) {
    double sum_sq = 0.0;
    size_t count = 0;
    for (size_t f = start_frame; f < end_frame && f * wav.channels < wav.samples.size(); ++f) {
        const double s = static_cast<double>(wav.samples[f * wav.channels]);
        sum_sq += s * s;
        ++count;
    }
    return count > 0 ? std::sqrt(sum_sq / static_cast<double>(count)) : 0.0;
}

Json make_base_project(int64_t source_duration_ticks, Json media_array, Json segments_array, Json effects_array) {
    return Json{
        {"schemaVersion", 1},
        {"projectId", "transition-test"},
        {"revision", 1},
        {"sourceDuration", {{"ticks", source_duration_ticks}, {"timescale", 10000000}}},
        {"media", std::move(media_array)},
        {"segments", std::move(segments_array)},
        {"canvas", {
            {"width", 640}, {"height", 360}, {"padding", 0.05},
            {"background", {{"r", 0.0}, {"g", 0.0}, {"b", 0.0}, {"a", 1.0}}},
            {"screen", {{"x", 0.0}, {"y", 0.0}, {"width", 1.0}, {"height", 1.0}}},
            {"camera", {{"enabled", false}, {"x", 0.72}, {"y", 0.68}, {"width", 0.25}, {"height", 0.28}, {"mirror", false}}}
        }},
        {"effects", std::move(effects_array)},
        {"markers", Json::array()},
        {"export", {
            {"width", 640}, {"height", 360},
            {"fps", {{"numerator", 30}, {"denominator", 1}}},
            {"videoCodec", "h264"}, {"audioCodec", "aac"}, {"container", "mp4"}
        }}
    };
}

void test_transition_audio_crossfade() {
    const auto temp_dir = std::filesystem::temp_directory_path() / (L"ft-trans-audio-" + std::to_wstring(GetCurrentProcessId()));
    std::filesystem::create_directories(temp_dir);
    ScopedDir cleanup{temp_dir};
    const auto root_w = temp_dir.wstring();

    // Dummy screen file (audio_prepare only opens audio streams, ft_media_create checks file existence).
    {
        std::ofstream dummy(temp_dir / L"screen.mp4", std::ios::binary);
        dummy << "placeholder";
    }

    auto render_audio = [&](const Json& proj, const std::wstring& out_name) -> WavInfo {
        const std::string json = proj.dump();
        ft_media_handle handle = 0;
        expect(ft_media_create(json.data(), static_cast<uint32_t>(json.size()), root_w.c_str(), &handle) == S_OK && handle != 0,
            "ft_media_create succeeded for transition audio test");
        const auto out_wav = temp_dir / out_name;
        const int32_t hr = ft_media_audio_prepare(handle, out_wav.c_str());
        ft_media_destroy(handle);
        expect(hr == S_OK, "ft_media_audio_prepare S_OK");
        return read_wav_16bit(out_wav);
    };

    constexpr double pi = 3.14159265358979323846;

    // 1. Complementary cosine unity-sum crossfade when both sides have handles.
    write_test_wav_48k_stereo(temp_dir / L"sys_cont.wav", 6ULL * 48000, 250.0, 0.6);
    {
        auto proj = make_base_project(
            60'000'000,
            Json::array({
                {{"id", "scr"}, {"kind", "screen"}, {"path", "screen.mp4"}, {"duration", {{"ticks", 60'000'000}, {"timescale", 10000000}}}, {"startOffset", {{"ticks", 0}, {"timescale", 10000000}}}, {"width", 640}, {"height", 360}},
                {{"id", "aud"}, {"kind", "systemAudio"}, {"path", "sys_cont.wav"}, {"duration", {{"ticks", 60'000'000}, {"timescale", 10000000}}}, {"startOffset", {{"ticks", 0}, {"timescale", 10000000}}}, {"sampleRate", 48000}, {"channels", 2}}
            }),
            Json::array({
                {{"id", "seg-1"}, {"sourceStart", {{"ticks", 0}, {"timescale", 10000000}}}, {"sourceDuration", {{"ticks", 20'000'000}, {"timescale", 10000000}}}, {"rate", {{"numerator", 1}, {"denominator", 1}}}},
                {{"id", "seg-2"}, {"sourceStart", {{"ticks", 40'000'000}, {"timescale", 10000000}}}, {"sourceDuration", {{"ticks", 20'000'000}, {"timescale", 10000000}}}, {"rate", {{"numerator", 1}, {"denominator", 1}}}}
            }),
            Json::array({
                {{"id", "tr-1"}, {"kind", "transition"}, {"sourceStart", {{"ticks", 19'000'000}, {"timescale", 10000000}}}, {"sourceDuration", {{"ticks", 2'000'000}, {"timescale", 10000000}}}, {"parameters", {{"style", "crossfade"}, {"duration", 0.6}, {"fromSegmentId", "seg-1"}, {"toSegmentId", "seg-2"}}}}
            })
        );
        const WavInfo wav = render_audio(proj, L"out_cont.wav");
        const double steady_rms = wav_rms_range(wav, 24000, 48000);
        const double seam_rms = wav_rms_range(wav, 96000 - 4800, 96000 + 4800);
        expect(seam_rms > steady_rms * 0.92 && seam_rms < steady_rms * 1.08,
            "complementary cosine crossfade preserves unity level across in-phase cut");
    }

    // 2. Step signal: first 3s silent, second 3s 440Hz -> pre-roll handle from seg-2 fades in BEFORE the 2.0s cut.
    write_test_wav_48k_stereo_fn(temp_dir / L"sys_step.wav", 6ULL * 48000, [=](uint64_t i) {
        return i < 3ULL * 48000 ? 0.0 : 0.7 * std::sin(2.0 * pi * 440.0 * static_cast<double>(i) / 48000.0);
    });
    {
        auto proj = make_base_project(
            60'000'000,
            Json::array({
                {{"id", "scr"}, {"kind", "screen"}, {"path", "screen.mp4"}, {"duration", {{"ticks", 60'000'000}, {"timescale", 10000000}}}, {"startOffset", {{"ticks", 0}, {"timescale", 10000000}}}, {"width", 640}, {"height", 360}},
                {{"id", "aud"}, {"kind", "systemAudio"}, {"path", "sys_step.wav"}, {"duration", {{"ticks", 60'000'000}, {"timescale", 10000000}}}, {"startOffset", {{"ticks", 0}, {"timescale", 10000000}}}, {"sampleRate", 48000}, {"channels", 2}}
            }),
            Json::array({
                {{"id", "seg-1"}, {"sourceStart", {{"ticks", 0}, {"timescale", 10000000}}}, {"sourceDuration", {{"ticks", 20'000'000}, {"timescale", 10000000}}}, {"rate", {{"numerator", 1}, {"denominator", 1}}}},
                {{"id", "seg-2"}, {"sourceStart", {{"ticks", 40'000'000}, {"timescale", 10000000}}}, {"sourceDuration", {{"ticks", 20'000'000}, {"timescale", 10000000}}}, {"rate", {{"numerator", 1}, {"denominator", 1}}}}
            }),
            Json::array({
                {{"id", "tr-1"}, {"kind", "transition"}, {"sourceStart", {{"ticks", 19'000'000}, {"timescale", 10000000}}}, {"sourceDuration", {{"ticks", 2'000'000}, {"timescale", 10000000}}}, {"parameters", {{"style", "crossfade"}, {"duration", 0.6}, {"fromSegmentId", "seg-1"}, {"toSegmentId", "seg-2"}}}}
            })
        );
        const WavInfo wav = render_audio(proj, L"out_step.wav");
        const double pre_window_silence = wav_rms_range(wav, 24000, 72000);
        const double preroll_fade_rms = wav_rms_range(wav, 86400, 95500); // [1.80s, 1.99s) before cut at 2.0s
        const double post_cut_steady = wav_rms_range(wav, 120000, 168000);
        expect(pre_window_silence < 10.0, "before transition window remains silent");
        expect(preroll_fade_rms > 1500.0 && preroll_fade_rms < post_cut_steady * 0.65,
            "incoming pre-roll handle fades in smoothly before cut");
        expect(post_cut_steady > 12000.0, "post-cut steady state has expected tone");
    }

    // 3. No source handles: seg-1 ends at media A end, seg-2 starts at media B start -> 300ms fade-out / fade-in.
    write_test_wav_48k_stereo(temp_dir / L"sys_a.wav", 2ULL * 48000, 440.0, 0.7);
    write_test_wav_48k_stereo(temp_dir / L"mic_b.wav", 2ULL * 48000, 880.0, 0.7);
    {
        auto proj = make_base_project(
            40'000'000,
            Json::array({
                {{"id", "scr"}, {"kind", "screen"}, {"path", "screen.mp4"}, {"duration", {{"ticks", 40'000'000}, {"timescale", 10000000}}}, {"startOffset", {{"ticks", 0}, {"timescale", 10000000}}}, {"width", 640}, {"height", 360}},
                {{"id", "aud-a"}, {"kind", "systemAudio"}, {"path", "sys_a.wav"}, {"duration", {{"ticks", 20'000'000}, {"timescale", 10000000}}}, {"startOffset", {{"ticks", 0}, {"timescale", 10000000}}}, {"sampleRate", 48000}, {"channels", 2}},
                {{"id", "aud-b"}, {"kind", "microphone"}, {"path", "mic_b.wav"}, {"duration", {{"ticks", 20'000'000}, {"timescale", 10000000}}}, {"startOffset", {{"ticks", 20'000'000}, {"timescale", 10000000}}}, {"sampleRate", 48000}, {"channels", 2}}
            }),
            Json::array({
                {{"id", "seg-1"}, {"sourceStart", {{"ticks", 0}, {"timescale", 10000000}}}, {"sourceDuration", {{"ticks", 20'000'000}, {"timescale", 10000000}}}, {"rate", {{"numerator", 1}, {"denominator", 1}}}},
                {{"id", "seg-2"}, {"sourceStart", {{"ticks", 20'000'000}, {"timescale", 10000000}}}, {"sourceDuration", {{"ticks", 20'000'000}, {"timescale", 10000000}}}, {"rate", {{"numerator", 1}, {"denominator", 1}}}}
            }),
            Json::array({
                {{"id", "tr-1"}, {"kind", "transition"}, {"sourceStart", {{"ticks", 19'000'000}, {"timescale", 10000000}}}, {"sourceDuration", {{"ticks", 2'000'000}, {"timescale", 10000000}}}, {"parameters", {{"style", "crossfade"}, {"duration", 0.6}, {"fromSegmentId", "seg-1"}, {"toSegmentId", "seg-2"}}}}
            })
        );
        const WavInfo wav = render_audio(proj, L"out_nohandle.wav");
        const double pre_fade = wav_rms_range(wav, 24000, 60000);
        const double tail_out = wav_rms_range(wav, 93000, 95900); // final ~60ms before cut
        const double head_in = wav_rms_range(wav, 96100, 99000);  // initial ~60ms after cut
        const double post_fade = wav_rms_range(wav, 120000, 168000);
        expect(pre_fade > 12000.0 && post_fade > 12000.0, "steady segments have full tone");
        expect(tail_out < 3000.0 && head_in < 3000.0, "transition without source handles applies 300ms cosine fade-out/in");
    }
}

void write_solid_mp4(const std::filesystem::path& path, uint32_t width, uint32_t height, uint32_t fps, uint32_t frame_count, uint32_t bgra) {
    HMODULE mfplat = LoadLibraryW(L"mfplat.dll");
    HMODULE mfrw = LoadLibraryW(L"mfreadwrite.dll");
    expect(mfplat != nullptr && mfrw != nullptr, "load mfplat.dll and mfreadwrite.dll");

    using FnMFStartup = HRESULT(WINAPI*)(ULONG, DWORD);
    using FnMFShutdown = HRESULT(WINAPI*)();
    using FnMFCreateMediaType = HRESULT(WINAPI*)(IMFMediaType**);
    using FnMFCreateMemoryBuffer = HRESULT(WINAPI*)(DWORD, IMFMediaBuffer**);
    using FnMFCreateSample = HRESULT(WINAPI*)(IMFSample**);
    using FnMFCreateSinkWriterFromURL = HRESULT(WINAPI*)(LPCWSTR, IMFByteStream*, IMFAttributes*, IMFSinkWriter**);

    auto pMFStartup = reinterpret_cast<FnMFStartup>(GetProcAddress(mfplat, "MFStartup"));
    auto pMFShutdown = reinterpret_cast<FnMFShutdown>(GetProcAddress(mfplat, "MFShutdown"));
    auto pMFCreateMediaType = reinterpret_cast<FnMFCreateMediaType>(GetProcAddress(mfplat, "MFCreateMediaType"));
    auto pMFCreateMemoryBuffer = reinterpret_cast<FnMFCreateMemoryBuffer>(GetProcAddress(mfplat, "MFCreateMemoryBuffer"));
    auto pMFCreateSample = reinterpret_cast<FnMFCreateSample>(GetProcAddress(mfplat, "MFCreateSample"));
    auto pMFCreateSinkWriterFromURL = reinterpret_cast<FnMFCreateSinkWriterFromURL>(GetProcAddress(mfrw, "MFCreateSinkWriterFromURL"));
    expect(pMFStartup && pMFShutdown && pMFCreateMediaType && pMFCreateMemoryBuffer && pMFCreateSample && pMFCreateSinkWriterFromURL,
        "resolve MF procedures for test MP4 generation");

    expect(SUCCEEDED(pMFStartup(MF_VERSION, MFSTARTUP_FULL)), "MFStartup");
    struct MfGuard { FnMFShutdown fn; ~MfGuard() { fn(); } } guard{pMFShutdown};

    ComPtr<IMFSinkWriter> writer;
    expect(SUCCEEDED(pMFCreateSinkWriterFromURL(path.c_str(), nullptr, nullptr, &writer)), "MFCreateSinkWriterFromURL");

    ComPtr<IMFMediaType> out_type;
    expect(SUCCEEDED(pMFCreateMediaType(&out_type)), "MFCreateMediaType out");
    expect(SUCCEEDED(out_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video)), "out major");
    expect(SUCCEEDED(out_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264)), "out subtype H264");
    expect(SUCCEEDED(out_type->SetUINT32(MF_MT_AVG_BITRATE, 400000)), "out bitrate");
    expect(SUCCEEDED(out_type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive)), "out interlace");
    expect(SUCCEEDED(MFSetAttributeSize(out_type.Get(), MF_MT_FRAME_SIZE, width, height)), "out size");
    expect(SUCCEEDED(MFSetAttributeRatio(out_type.Get(), MF_MT_FRAME_RATE, fps, 1)), "out fps");
    expect(SUCCEEDED(MFSetAttributeRatio(out_type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1)), "out par");
    DWORD stream_idx = 0;
    expect(SUCCEEDED(writer->AddStream(out_type.Get(), &stream_idx)), "AddStream");

    ComPtr<IMFMediaType> in_type;
    expect(SUCCEEDED(pMFCreateMediaType(&in_type)), "MFCreateMediaType in");
    expect(SUCCEEDED(in_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video)), "in major");
    expect(SUCCEEDED(in_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32)), "in subtype RGB32");
    expect(SUCCEEDED(in_type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive)), "in interlace");
    expect(SUCCEEDED(MFSetAttributeSize(in_type.Get(), MF_MT_FRAME_SIZE, width, height)), "in size");
    expect(SUCCEEDED(MFSetAttributeRatio(in_type.Get(), MF_MT_FRAME_RATE, fps, 1)), "in fps");
    expect(SUCCEEDED(MFSetAttributeRatio(in_type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1)), "in par");
    expect(SUCCEEDED(writer->SetInputMediaType(stream_idx, in_type.Get(), nullptr)), "SetInputMediaType");
    expect(SUCCEEDED(writer->BeginWriting()), "BeginWriting");

    const DWORD bytes_per_frame = width * height * 4;
    const LONGLONG frame_dur = 10'000'000LL / fps;
    for (uint32_t i = 0; i < frame_count; ++i) {
        ComPtr<IMFMediaBuffer> buf;
        expect(SUCCEEDED(pMFCreateMemoryBuffer(bytes_per_frame, &buf)), "MFCreateMemoryBuffer");
        BYTE* ptr = nullptr;
        expect(SUCCEEDED(buf->Lock(&ptr, nullptr, nullptr)), "Lock");
        auto* pixels = reinterpret_cast<uint32_t*>(ptr);
        std::fill_n(pixels, static_cast<size_t>(width) * height, bgra);
        expect(SUCCEEDED(buf->Unlock()), "Unlock");
        expect(SUCCEEDED(buf->SetCurrentLength(bytes_per_frame)), "SetCurrentLength");

        ComPtr<IMFSample> sample;
        expect(SUCCEEDED(pMFCreateSample(&sample)), "MFCreateSample");
        expect(SUCCEEDED(sample->AddBuffer(buf.Get())), "AddBuffer");
        expect(SUCCEEDED(sample->SetSampleTime(static_cast<LONGLONG>(i) * frame_dur)), "SetSampleTime");
        expect(SUCCEEDED(sample->SetSampleDuration(frame_dur)), "SetSampleDuration");
        expect(SUCCEEDED(writer->WriteSample(stream_idx, sample.Get())), "WriteSample");
    }
    expect(SUCCEEDED(writer->Finalize()), "Finalize");
}

std::vector<uint8_t> read_png_bgra(const std::filesystem::path& path, uint32_t& out_w, uint32_t& out_h) {
    ComPtr<IWICImagingFactory> wic;
    expect(SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic))), "WIC factory");
    ComPtr<IWICBitmapDecoder> decoder;
    expect(SUCCEEDED(wic->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad, &decoder)), "WIC decode PNG");
    ComPtr<IWICBitmapFrameDecode> frame;
    expect(SUCCEEDED(decoder->GetFrame(0, &frame)), "WIC frame 0");
    expect(SUCCEEDED(frame->GetSize(&out_w, &out_h)), "WIC GetSize");
    ComPtr<IWICFormatConverter> converter;
    expect(SUCCEEDED(wic->CreateFormatConverter(&converter)), "WIC CreateFormatConverter");
    expect(SUCCEEDED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom)), "WIC converter");
    std::vector<uint8_t> pixels(static_cast<size_t>(out_w) * out_h * 4);
    expect(SUCCEEDED(converter->CopyPixels(nullptr, out_w * 4, static_cast<UINT>(pixels.size()), pixels.data())), "WIC CopyPixels");
    return pixels;
}

size_t count_diff_pixels(const std::vector<uint8_t>& a, const std::vector<uint8_t>& b) {
    expect(a.size() == b.size(), "PNG buffers have identical size");
    size_t diff = 0;
    for (size_t i = 0; i + 3 < a.size(); i += 4) {
        if (std::abs(static_cast<int>(a[i]) - static_cast<int>(b[i])) > 8 ||
            std::abs(static_cast<int>(a[i + 1]) - static_cast<int>(b[i + 1])) > 8 ||
            std::abs(static_cast<int>(a[i + 2]) - static_cast<int>(b[i + 2])) > 8) {
            ++diff;
        }
    }
    return diff;
}

void test_transition_render_tilt_and_stickers() {
    const auto temp_dir = std::filesystem::temp_directory_path() / (L"ft-trans-render-" + std::to_wstring(GetCurrentProcessId()));
    std::filesystem::create_directories(temp_dir);
    ScopedDir cleanup{temp_dir};
    const auto root_w = temp_dir.wstring();

    write_solid_mp4(temp_dir / L"clip.mp4", 320, 180, 30, 60, 0xFF2060E0u);

    auto render_at_cut = [&](Json effects, const std::wstring& png_name) -> std::vector<uint8_t> {
        auto proj = make_base_project(
            20'000'000,
            Json::array({
                {{"id", "scr"}, {"kind", "screen"}, {"path", "clip.mp4"}, {"duration", {{"ticks", 20'000'000}, {"timescale", 10000000}}}, {"startOffset", {{"ticks", 0}, {"timescale", 10000000}}}, {"width", 320}, {"height", 180}}
            }),
            Json::array({
                {{"id", "seg-1"}, {"sourceStart", {{"ticks", 0}, {"timescale", 10000000}}}, {"sourceDuration", {{"ticks", 10'000'000}, {"timescale", 10000000}}}, {"rate", {{"numerator", 1}, {"denominator", 1}}}},
                {{"id", "seg-2"}, {"sourceStart", {{"ticks", 10'000'000}, {"timescale", 10000000}}}, {"sourceDuration", {{"ticks", 10'000'000}, {"timescale", 10000000}}}, {"rate", {{"numerator", 1}, {"denominator", 1}}}}
            }),
            std::move(effects)
        );
        const std::string json = proj.dump();
        ft_media_handle handle = 0;
        expect(ft_media_create(json.data(), static_cast<uint32_t>(json.size()), root_w.c_str(), &handle) == S_OK && handle != 0,
            "ft_media_create succeeded for transition render test");
        const auto png_path = temp_dir / png_name;
        const int32_t hr = ft_media_render(handle, 10'000'000, 1, png_path.c_str());
        if (hr != S_OK) {
            wchar_t err[512]{};
            ft_media_error(err, 512);
            std::wcerr << L"ft_media_render failed hr=0x" << std::hex << static_cast<uint32_t>(hr) << std::dec << L" error=" << err << L'\n';
        }
        ft_media_destroy(handle);
        expect(hr == S_OK, "ft_media_render S_OK at transition midpoint");
        uint32_t w = 0, h = 0;
        auto px = read_png_bgra(png_path, w, h);
        expect(w == 640 && h == 360, "rendered transition PNG has expected 640x360 resolution");
        return px;
    };

    const Json transition_effect = {
        {"id", "tr-1"}, {"kind", "transition"},
        {"sourceStart", {{"ticks", 9'000'000}, {"timescale", 10000000}}},
        {"sourceDuration", {{"ticks", 2'000'000}, {"timescale", 10000000}}},
        {"parameters", {{"style", "crossfade"}, {"duration", 0.6}, {"fromSegmentId", "seg-1"}, {"toSegmentId", "seg-2"}}}
    };
    const Json screen_sticker = {
        {"id", "stk-1"}, {"kind", "sticker"},
        {"sourceStart", {{"ticks", 0}, {"timescale", 10000000}}},
        {"sourceDuration", {{"ticks", 20'000'000}, {"timescale", 10000000}}},
        {"parameters", {{"attachment", "screen"}, {"x", 0.15}, {"y", 0.15}, {"width", 0.7}, {"height", 0.7}, {"text", "TRANSITION STICKER"}, {"opacity", 1.0}}}
    };
    const Json screen_tilt = {
        {"id", "tilt-1"}, {"kind", "tilt"},
        {"sourceStart", {{"ticks", 0}, {"timescale", 10000000}}},
        {"sourceDuration", {{"ticks", 20'000'000}, {"timescale", 10000000}}},
        {"parameters", {{"target", "screen"}, {"angleX", 25.0}, {"angleY", 20.0}, {"enter", 0.1}, {"exit", 0.1}}}
    };

    const auto baseline = render_at_cut(Json::array({transition_effect}), L"baseline.png");
    const auto with_sticker = render_at_cut(Json::array({transition_effect, screen_sticker}), L"with_sticker.png");
    const auto with_tilt = render_at_cut(Json::array({transition_effect, screen_sticker, screen_tilt}), L"with_tilt.png");

    expect(count_diff_pixels(baseline, with_sticker) > 5000, "screen-attached sticker is rendered during transition");
    expect(count_diff_pixels(with_sticker, with_tilt) > 5000, "3D tilt projection is applied to transition output");
}

} // namespace

int main() {
    try {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        const bool com_init = SUCCEEDED(hr);
        test_audio_extract_16k();
        test_subtitle_renderer();
        test_transition_audio_crossfade();
        test_transition_render_tilt_and_stickers();
        if (com_init) CoUninitialize();
        std::cout << "PASS: subtitle_media_tests checks=" << checks << '\n';
        return 0;
    } catch (const std::exception& e) {
        std::cerr << "FAIL: " << e.what() << '\n';
        return 1;
    }
}
