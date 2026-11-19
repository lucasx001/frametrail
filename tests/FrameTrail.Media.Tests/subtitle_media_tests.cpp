#include "media.h"
#include "subtitle_renderer.hpp"
#include "pcm_wave.hpp"
#include <windows.h>
#include <d3d11.h>
#include <d2d1_1.h>
#include <d2d1_1helper.h>
#include <dwrite.h>
#include <wrl/client.h>
#include <nlohmann/json.hpp>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
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

void write_test_wav_48k_stereo(const std::filesystem::path& path, uint64_t frames, double freq_hz, double amplitude) {
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    expect(static_cast<bool>(out), "open temp 48k WAV for writing");
    write_pcm_header(out, frames);
    constexpr double pi = 3.14159265358979323846;
    std::vector<int16_t> samples(static_cast<size_t>(frames) * 2);
    for (uint64_t i = 0; i < frames; ++i) {
        const double v = amplitude * std::sin(2.0 * pi * freq_hz * static_cast<double>(i) / 48000.0);
        const int16_t s = static_cast<int16_t>(std::lround(std::clamp(v, -1.0, 1.0) * 32767.0));
        samples[static_cast<size_t>(i) * 2] = s;
        samples[static_cast<size_t>(i) * 2 + 1] = s;
    }
    out.write(reinterpret_cast<const char*>(samples.data()), static_cast<std::streamsize>(samples.size() * sizeof(int16_t)));
    out.flush();
    expect(static_cast<bool>(out), "flush temp 48k WAV");
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

} // namespace

int main() {
    try {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        const bool com_init = SUCCEEDED(hr);
        test_audio_extract_16k();
        test_subtitle_renderer();
        if (com_init) CoUninitialize();
        std::cout << "PASS: subtitle_media_tests checks=" << checks << '\n';
        return 0;
    } catch (const std::exception& e) {
        std::cerr << "FAIL: " << e.what() << '\n';
        return 1;
    }
}
