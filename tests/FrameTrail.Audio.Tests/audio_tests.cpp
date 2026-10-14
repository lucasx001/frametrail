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
int wmain(int argc, wchar_t** argv) {
    try { clocks(); if (argc == 3 && std::wstring(argv[1]) == L"--smoke") smoke(argv[2]); return 0; }
    catch (const std::exception& e) { std::cerr << "FAIL: " << e.what() << '\n'; return 1; }
}
