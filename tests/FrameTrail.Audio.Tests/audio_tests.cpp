#include "audio.h"
#include "audio_clock.h"
#include <Windows.h>
#include <mmsystem.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <vector>
using namespace frametrail::audio;
void expect(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
void ok(int32_t hr, const char* operation) { if (hr < 0) { std::cerr << operation << " HRESULT=0x" << std::hex << static_cast<uint32_t>(hr) << std::dec << '\n'; throw std::runtime_error(operation); } }
int64_t now() { LARGE_INTEGER n; QueryPerformanceCounter(&n); return n.QuadPart; }
int64_t frequency() { LARGE_INTEGER n; QueryPerformanceFrequency(&n); return n.QuadPart; }
void clocks() {
    SessionClock clock; clock.set_epoch(10000000, 10000000);
    expect(!clock.active(9999999), "preroll rejected"); expect(clock.frames_at(20000000) == 48000, "one second");
    clock.pause(20000000); expect(!clock.active(20000000), "left pause boundary excluded"); expect(clock.frames_at(90000000) == 48000, "open pause freezes clock");
    clock.resume(40000000); expect(clock.active(40000000), "resume boundary included"); expect(clock.frames_at(50000000) == 96000, "pause removed");
    clock.pause(60000000); clock.resume(61000000); expect(clock.frames_at(71000000) == 192000, "multiple pauses");
    bool rejected = false; try { clock.resume(72000000); } catch (const std::invalid_argument&) { rejected = true; } expect(rejected, "invalid resume rejected");
    SessionClock rational; rational.set_epoch(1, 3579545); expect(rational.frames_at(1 + 3579545LL * 1800) == 86400000, "30 minute rational QPC no accumulation error");
    expect(ft_audio_set_epoch(nullptr, 1, 1) < 0, "null ABI handle rejected");
    ft_audio_handle handle = reinterpret_cast<void*>(1); ft_audio_options invalid{}; expect(ft_audio_start(&invalid, &handle) < 0 && !handle, "invalid start clears handle");
    uint32_t pid = 0; expect(ft_audio_process_from_window(0, &pid) < 0, "invalid window rejected");
    std::cout << "PASS: QPC epoch, half-open pause intervals, repeated pause/resume, rational 30-minute map, invalid ABI inputs.\n";
}
struct Tone {
    HWAVEOUT device = nullptr; WAVEHDR header{}; std::vector<int16_t> samples;
    void play() {
        WAVEFORMATEX format{}; format.wFormatTag = WAVE_FORMAT_PCM; format.nChannels = 2; format.nSamplesPerSec = 48000; format.wBitsPerSample = 16; format.nBlockAlign = 4; format.nAvgBytesPerSec = 192000;
        if (waveOutOpen(&device, WAVE_MAPPER, &format, 0, 0, CALLBACK_NULL) != MMSYSERR_NOERROR) throw std::runtime_error("waveOutOpen");
        samples.resize(48000 * 2); for (int i = 0; i < 48000; ++i) samples[i * 2] = samples[i * 2 + 1] = static_cast<int16_t>(std::sin(i * 2.0 * 3.141592653589793 * 1000 / 48000) * 1600);
        header.lpData = reinterpret_cast<LPSTR>(samples.data()); header.dwBufferLength = static_cast<DWORD>(samples.size() * 2);
        expect(waveOutPrepareHeader(device, &header, sizeof(header)) == MMSYSERR_NOERROR, "tone prepare"); expect(waveOutWrite(device, &header, sizeof(header)) == MMSYSERR_NOERROR, "tone write");
    }
    ~Tone() { if (device) { waveOutReset(device); waveOutUnprepareHeader(device, &header, sizeof(header)); waveOutClose(device); } }
};
void smoke(const std::filesystem::path& directory) {
    std::filesystem::create_directories(directory);
    uint32_t app_supported = 0; ok(ft_audio_process_loopback_supported(&app_supported), "support");
    std::cout << "Process loopback supported=" << app_supported << " QPC frequency=" << frequency() << '\n';
    for (uint32_t kind : {1u, 2u}) {
        uint32_t count = 0; ok(ft_audio_enumerate_devices(kind, nullptr, 0, &count), "enumerate count"); std::vector<ft_audio_device> devices(count);
        ok(ft_audio_enumerate_devices(kind, devices.data(), count, &count), "enumerate fill");
        for (const auto& d : devices) std::wcout << L"Device kind=" << kind << L" default=" << d.is_default << L" name=" << d.name << L'\n';
    }
    for (uint32_t kind : {1u, 3u, 2u}) {
        if (kind == 3 && !app_supported) continue;
        auto path = directory / (L"audio-" + std::to_wstring(kind) + L".wav"); auto log = directory / (L"audio-" + std::to_wstring(kind) + L".jsonl");
        ft_audio_options options{}; options.struct_size = sizeof(options); options.kind = kind; options.process_id = GetCurrentProcessId(); options.output_path = path.c_str(); options.diagnostics_path = log.c_str();
        ft_audio_handle handle = nullptr; auto hr = ft_audio_start(&options, &handle);
        if (hr < 0) { std::cout << "UNAVAILABLE kind=" << kind << " HRESULT=0x" << std::hex << static_cast<uint32_t>(hr) << std::dec << '\n'; continue; }
        try {
            std::this_thread::sleep_for(std::chrono::milliseconds(250)); auto epoch = now(); ok(ft_audio_set_epoch(handle, epoch, frequency()), "epoch");
            Tone tone; if (kind != FT_AUDIO_MICROPHONE) tone.play();
            std::this_thread::sleep_for(std::chrono::milliseconds(300)); auto pause = now(); ok(ft_audio_pause_at(handle, pause), "pause");
            std::this_thread::sleep_for(std::chrono::milliseconds(200)); auto resume = now(); ok(ft_audio_resume_at(handle, resume), "resume");
            std::this_thread::sleep_for(std::chrono::milliseconds(700)); auto stop = now(); ok(ft_audio_stop_at(handle, stop), "stop");
            ft_audio_status status{}; status.struct_size = sizeof(status); ok(ft_audio_get_status(handle, &status), "status");
            auto expected = static_cast<int64_t>(std::llround(static_cast<long double>(stop - epoch - (resume - pause)) * 48000 / frequency()));
            expect(static_cast<int64_t>(status.frames_written) == expected, "WAV frame duration excludes exact pause interval");
            std::ifstream input(path, std::ios::binary); input.seekg(56); std::vector<float> samples(static_cast<size_t>(status.frames_written) * 2); input.read(reinterpret_cast<char*>(samples.data()), static_cast<std::streamsize>(samples.size() * sizeof(float)));
            double energy = 0, max = 0; for (auto sample : samples) { expect(std::isfinite(sample), "finite PCM"); max = std::max(max, std::abs(static_cast<double>(sample))); energy += sample * sample; }
            std::cout << "PASS kind=" << kind << " frames=" << status.frames_written << " expected=" << expected << " packets=" << status.packets << " native=" << status.native_sample_rate << '/' << status.native_channels << " peak=" << max << " rms=" << std::sqrt(energy / samples.size()) << " discontinuities=" << status.discontinuities << " drift_ppm=" << status.drift_ppm << " correction_frames=" << status.correction_frames << '\n';
            if (kind != FT_AUDIO_MICROPHONE) expect(max > .001, "rendered test tone captured");
        } catch (...) { ft_audio_destroy(handle); throw; }
        ft_audio_destroy(handle);
    }
    // Silent process loopback must still finalize a duration-correct zero track without waiting for packets.
    auto path = directory / L"silence.wav"; ft_audio_options options{}; options.struct_size = sizeof(options); options.kind = 3; options.process_id = GetCurrentProcessId(); options.output_path = path.c_str();
    ft_audio_handle handle = nullptr; ok(ft_audio_start(&options, &handle), "silent start"); auto epoch = now(); ok(ft_audio_set_epoch(handle, epoch, frequency()), "silent epoch");
    std::this_thread::sleep_for(std::chrono::milliseconds(350)); ok(ft_audio_stop_at(handle, now()), "silent stop"); ft_audio_status status{}; status.struct_size = sizeof(status); ok(ft_audio_get_status(handle, &status), "silent status");
    expect(status.frames_written > 15000, "silence has duration"); std::cout << "PASS silent process track frames=" << status.frames_written << " packets=" << status.packets << '\n'; ft_audio_destroy(handle);
}
void dsp_math() {
    constexpr double fs = 48000.0;
    constexpr double pi = 3.14159265358979323846;

    // 1. 2nd-order Butterworth 85 Hz high-pass biquad filter
    const double w0 = 2.0 * pi * 85.0 / fs;
    const double cos_w0 = std::cos(w0), sin_w0 = std::sin(w0);
    const double alpha = sin_w0 / (2.0 * 0.7071067811865475);
    const double a0 = 1.0 + alpha;
    const double b0 = ((1.0 + cos_w0) * 0.5) / a0;
    const double b1 = -(1.0 + cos_w0) / a0;
    const double b2 = ((1.0 + cos_w0) * 0.5) / a0;
    const double a1 = (-2.0 * cos_w0) / a0;
    const double a2 = (1.0 - alpha) / a0;

    auto run_hpf_rms = [&](double freq_hz) {
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0, sum_sq = 0;
        const int total = 48000;
        const int settle = 24000;
        for (int i = 0; i < total; ++i) {
            const double x = std::sin(2.0 * pi * freq_hz * i / fs);
            const double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            if (i >= settle) sum_sq += y * y;
        }
        return std::sqrt(sum_sq / (total - settle));
    };
    const double rms_30hz = run_hpf_rms(30.0);
    const double rms_1khz = run_hpf_rms(1000.0);
    expect(rms_30hz < 0.15, "85 Hz HPF attenuates 30 Hz rumble");
    expect(rms_1khz > 0.68 && rms_1khz < 0.72, "85 Hz HPF preserves 1 kHz speech tone");

    // 2. Soft-knee RMS noise gate (-42 dBFS threshold, 3:1 below threshold, 0.18 floor)
    const double gate_atk = 1.0 - std::exp(-1.0 / (0.005 * fs));
    const double gate_rel = 1.0 - std::exp(-1.0 / (0.060 * fs));
    auto eval_gate = [&](double amp, int samples, double& env_sq) {
        double gain = 1.0;
        for (int i = 0; i < samples; ++i) {
            const double sq = amp * amp;
            env_sq += (sq > env_sq ? gate_atk : gate_rel) * (sq - env_sq);
            const double rms = std::sqrt(std::max(0.0, env_sq));
            const double level_db = 20.0 * std::log10(std::max(rms, 1e-9));
            const double diff = -42.0 - level_db;
            double gain_db = 0.0;
            if (diff >= 3.0) gain_db = -2.0 * diff;
            else if (diff > -3.0) { const double x = diff + 3.0; gain_db = -2.0 * (x * x) / 12.0; }
            gain = std::max(0.18, std::pow(10.0, gain_db / 20.0));
        }
        return gain;
    };
    double gate_env_sq = 0.0;
    const double noise_gain = eval_gate(0.001, 4800, gate_env_sq); // -60 dBFS
    expect(std::abs(noise_gain - 0.18) < 1e-4, "noise gate attenuates -60 dBFS room tone to 0.18 floor");
    const double speech_gain = eval_gate(0.1, 4800, gate_env_sq); // -20 dBFS
    expect(std::abs(speech_gain - 1.0) < 1e-4, "noise gate opens to unity for -20 dBFS speech");

    // 3. Auto voice ducking (15ms/280ms, -35 dBFS -> 0.28x)
    const double duck_atk = 1.0 - std::exp(-1.0 / (0.015 * fs));
    const double duck_rel = 1.0 - std::exp(-1.0 / (0.280 * fs));
    const double smooth_down = 1.0 - std::exp(-1.0 / (0.010 * fs));
    const double smooth_up = 1.0 - std::exp(-1.0 / (0.040 * fs));
    double duck_env_sq = 0.0, duck_gain = 1.0;
    for (int i = 0; i < 9600; ++i) {
        const double sq = 0.1 * 0.1;
        duck_env_sq += (sq > duck_env_sq ? duck_atk : duck_rel) * (sq - duck_env_sq);
        const double target = std::sqrt(duck_env_sq) > 0.017782794 ? 0.28 : 1.0;
        duck_gain += (target < duck_gain ? smooth_down : smooth_up) * (target - duck_gain);
    }
    expect(std::abs(duck_gain - 0.28) < 0.01, "voice ducker ducks background to 0.28x when speech active");

    // 4. Loudness normalization (-16 dBFS target, max 2.5x) & -1.0 dBFS soft-knee limiter
    const double norm_rms = 1.0 - std::exp(-1.0 / (0.100 * fs));
    const double norm_atk = 1.0 - std::exp(-1.0 / (0.010 * fs));
    const double norm_rel = 1.0 - std::exp(-1.0 / (0.150 * fs));
    constexpr double target_rms = 0.158489319;
    constexpr float limit_peak = 0.891250938f;
    constexpr float knee_start = 0.70f * limit_peak;
    constexpr float headroom = limit_peak - knee_start;
    auto soft_limit = [&](float x) {
        const float ax = std::abs(x);
        if (ax <= knee_start) return x;
        const float y = knee_start + headroom * std::tanh((ax - knee_start) / headroom);
        return x < 0.0f ? -y : y;
    };
    double norm_env_sq = 0.0, norm_gain = 1.0;
    for (int i = 0; i < 48000; ++i) {
        const double s = 0.08 * std::sin(2.0 * pi * 440.0 * i / fs); // RMS = 0.05657 -> target/rms = 2.80 -> capped at 2.5x
        const double sq = s * s;
        norm_env_sq += norm_rms * (sq - norm_env_sq);
        const double rms = std::sqrt(norm_env_sq);
        double desired = 1.0;
        if (rms > 1e-4) {
            const double raw = std::clamp(target_rms / rms, 0.1, 2.5);
            const double blend = std::clamp((rms - 1e-4) / 9e-4, 0.0, 1.0);
            desired = 1.0 + blend * (raw - 1.0);
        }
        norm_gain += (desired < norm_gain ? norm_atk : norm_rel) * (desired - norm_gain);
    }
    expect(std::abs(norm_gain - 2.5) < 0.05, "loudness normalizer caps makeup gain at 2.5x for quiet signal");
    expect(soft_limit(2.0f) <= limit_peak && soft_limit(-2.0f) >= -limit_peak, "soft limiter enforces -1.0 dBFS ceiling");
    std::cout << "PASS: audio DSP high-pass filter, RMS noise gate, voice ducking, loudness normalizer, and soft limiter.\n";
}
int wmain(int argc, wchar_t** argv) {
    try { clocks(); dsp_math(); if (argc == 3 && std::wstring(argv[1]) == L"--smoke") smoke(argv[2]); return 0; }
    catch (const std::exception& e) { std::cerr << "FAIL: " << e.what() << '\n'; return 1; }
}
