#include "audio.h"
#include "audio_clock.h"
#include <Windows.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#include <wrl.h>
#include <avrt.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <deque>
#include <fstream>
#include <iomanip>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
using Microsoft::WRL::ComPtr;
using namespace frametrail::audio;
namespace {
constexpr uint32_t Rate = 48000, Channels = 2, BytesPerFrame = 8, HeaderBytes = 56;
int64_t now_qpc() { LARGE_INTEGER v; QueryPerformanceCounter(&v); return v.QuadPart; }
int64_t frequency_qpc() { LARGE_INTEGER v; QueryPerformanceFrequency(&v); return v.QuadPart; }
void check(HRESULT hr) { if (FAILED(hr)) throw hr; }
template<size_t N> void copy_text(wchar_t (&dst)[N], const std::wstring& s) { wcsncpy_s(dst, s.c_str(), _TRUNCATE); }
struct Handle {
    HANDLE value = INVALID_HANDLE_VALUE;
    ~Handle() { if (value != INVALID_HANDLE_VALUE && value) CloseHandle(value); }
};
struct ComApartment { HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED); ~ComApartment() { if (SUCCEEDED(result)) CoUninitialize(); } };
class WaveFile {
    Handle handle_;
    uint64_t frames_ = 0;
    void write_bytes(const void* data, uint32_t bytes) {
        DWORD written = 0; if (!WriteFile(handle_.value, data, bytes, &written, nullptr) || written != bytes) throw HRESULT_FROM_WIN32(GetLastError());
    }
    void seek(uint64_t position) { LARGE_INTEGER p; p.QuadPart = position; if (!SetFilePointerEx(handle_.value, p, nullptr, FILE_BEGIN)) throw HRESULT_FROM_WIN32(GetLastError()); }
    void header() {
        const uint32_t data_bytes = static_cast<uint32_t>(frames_ * BytesPerFrame), riff = data_bytes + HeaderBytes - 8;
        const uint32_t fmt_size = 16, fact_size = 4, rate = Rate, bytes_per_second = Rate * BytesPerFrame;
        const uint16_t tag = WAVE_FORMAT_IEEE_FLOAT, channels = Channels, align = BytesPerFrame, bits = 32;
        seek(0); write_bytes("RIFF", 4); write_bytes(&riff, 4); write_bytes("WAVEfmt ", 8); write_bytes(&fmt_size, 4);
        write_bytes(&tag, 2); write_bytes(&channels, 2); write_bytes(&rate, 4); write_bytes(&bytes_per_second, 4); write_bytes(&align, 2); write_bytes(&bits, 2);
        write_bytes("fact", 4); write_bytes(&fact_size, 4); const uint32_t fact = static_cast<uint32_t>(frames_); write_bytes(&fact, 4);
        write_bytes("data", 4); write_bytes(&data_bytes, 4);
    }
public:
    void open(const std::wstring& path) {
        handle_.value = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (handle_.value == INVALID_HANDLE_VALUE) throw HRESULT_FROM_WIN32(GetLastError()); header();
    }
    uint64_t frames() const { return frames_; }
    void append(const float* data, uint64_t frames) {
        if (frames_ + frames > (UINT32_MAX - HeaderBytes) / BytesPerFrame) throw HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
        write_bytes(data, static_cast<uint32_t>(frames * BytesPerFrame)); frames_ += frames;
    }
    uint64_t silence_to(uint64_t frames) {
        static const float silence[4096 * Channels]{}; uint64_t added = 0;
        while (frames_ < frames) { auto n = std::min<uint64_t>(4096, frames - frames_); append(silence, n); added += n; }
        return added;
    }
    void truncate(uint64_t frames) {
        if (frames >= frames_) return; frames_ = frames; seek(HeaderBytes + frames_ * BytesPerFrame);
        if (!SetEndOfFile(handle_.value)) throw HRESULT_FROM_WIN32(GetLastError());
    }
    void finalize() {
        if (handle_.value == INVALID_HANDLE_VALUE) return;
        header(); seek(HeaderBytes + frames_ * BytesPerFrame); if (!FlushFileBuffers(handle_.value)) throw HRESULT_FROM_WIN32(GetLastError());
    }
    void close() noexcept { if (handle_.value != INVALID_HANDLE_VALUE) { CloseHandle(handle_.value); handle_.value = INVALID_HANDLE_VALUE; } }
};
class Activation final : public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IActivateAudioInterfaceCompletionHandler, Microsoft::WRL::FtmBase> {
public:
    Handle completed; HRESULT result = E_PENDING; ComPtr<IAudioClient> client;
    Activation() { completed.value = CreateEventW(nullptr, TRUE, FALSE, nullptr); }
    HRESULT STDMETHODCALLTYPE ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) override {
        ComPtr<IUnknown> object; HRESULT activation = E_FAIL;
        result = operation->GetActivateResult(&activation, &object); if (SUCCEEDED(result)) result = activation;
        if (SUCCEEDED(result)) result = object.As(&client); SetEvent(completed.value); return S_OK;
    }
};
struct Packet { uint64_t device_position = 0; int64_t qpc = 0, duration_qpc = 0; uint32_t flags = 0; std::vector<float> samples; };
class AudioSession {
public:
    std::mutex mutex; std::condition_variable initialized;
    ft_audio_status status{}; SessionClock clock;
    uint32_t kind = 0, process = 0; std::wstring device_id, output, diagnostic;
    std::thread worker; std::atomic<bool> stopping = false; int64_t stop_qpc = 0;
    bool ready = false, used_default = false; HRESULT init_result = E_PENDING;
    WaveFile wave; std::ofstream log; std::deque<Packet> preroll;
    uint64_t first_position = 0; int64_t first_audio_qpc = 0, last_level_qpc = 0;
    AudioSession() { status.struct_size = sizeof(status); status.state = FT_AUDIO_PREPARING; status.sample_rate = Rate; status.channels = Channels; status.qpc_frequency = frequency_qpc(); }
    ~AudioSession() { stopping = true; if (worker.joinable()) worker.join(); }
    int64_t qpc_from_100ns(uint64_t qpc100) const { return static_cast<int64_t>(std::llround(static_cast<long double>(qpc100) * status.qpc_frequency / 10000000)); }
    void message(const std::wstring& text) { copy_text(status.message, text); }
    void process_packet(const Packet& packet) {
        if (!clock.frequency) {
            preroll.push_back(packet);
            while (!preroll.empty() && packet.qpc - preroll.front().qpc > status.qpc_frequency * 2) { status.trimmed_frames += preroll.front().samples.size() / Channels; preroll.pop_front(); }
            return;
        }
        const auto frames = packet.samples.size() / Channels;
        size_t first = 0;
        // Crop buffers sample by sample at epoch and every pause boundary. Packet sizes are <= a few ms.
        while (first < frames) {
            auto sample_qpc = [&](size_t i) { return packet.qpc + static_cast<int64_t>(std::llround(packet.duration_qpc ? static_cast<long double>(i) * packet.duration_qpc / frames : static_cast<long double>(i) * clock.frequency / Rate)); };
            while (first < frames && (!clock.active(sample_qpc(first)) || (stop_qpc && sample_qpc(first) >= stop_qpc))) { ++first; ++status.trimmed_frames; }
            if (first == frames) break;
            size_t last = first + 1;
            while (last < frames && clock.active(sample_qpc(last)) && (!stop_qpc || sample_qpc(last) < stop_qpc)) ++last;
            uint64_t target = static_cast<uint64_t>(clock.frames_at(sample_qpc(first)));
            if (target > wave.frames() + 2) { auto gap = wave.silence_to(target); status.silent_frames += gap; status.correction_frames += gap; }
            else if (target + 2 < wave.frames()) { const auto overlap = std::min<uint64_t>(last - first, wave.frames() - target); first += static_cast<size_t>(overlap); status.trimmed_frames += overlap; status.correction_frames += overlap; }
            if (last > first) {
                const auto source_frames = last - first;
                const auto output_frames = static_cast<size_t>(std::max<int64_t>(0, clock.frames_at(sample_qpc(last)) - clock.frames_at(sample_qpc(first))));
                if (source_frames == output_frames) wave.append(packet.samples.data() + first * Channels, source_frames);
                else if (output_frames) {
                    // Bounded ppm-level clock correction. Large timestamp jumps are represented by explicit silence/trim above.
                    // Format-rate conversion itself is performed by Windows SRC_DEFAULT_QUALITY at client initialization.
                    std::vector<float> corrected(output_frames * Channels);
                    for (size_t out = 0; out < output_frames; ++out) {
                        double position = static_cast<double>(out) * source_frames / output_frames;
                        size_t left = std::min(source_frames - 1, static_cast<size_t>(position)), right = std::min(source_frames - 1, left + 1);
                        float fraction = static_cast<float>(position - left);
                        for (size_t channel = 0; channel < Channels; ++channel) {
                            float a = packet.samples[(first + left) * Channels + channel], b = packet.samples[(first + right) * Channels + channel];
                            corrected[out * Channels + channel] = a + (b - a) * fraction;
                        }
                    }
                    wave.append(corrected.data(), output_frames);
                    status.correction_frames += static_cast<uint64_t>(std::abs(static_cast<int64_t>(output_frames) - static_cast<int64_t>(source_frames)));
                }
            }
            first = last;
        }
        status.frames_written = wave.frames(); status.duration_100ns = static_cast<int64_t>(wave.frames() * 10000000 / Rate);
    }
    void finish_file(bool pad) {
        if (clock.frequency && pad && stop_qpc) { auto frames = static_cast<uint64_t>(clock.frames_at(stop_qpc)); wave.truncate(frames); status.silent_frames += wave.silence_to(frames); }
        wave.finalize(); status.frames_written = wave.frames(); status.duration_100ns = static_cast<int64_t>(wave.frames() * 10000000 / Rate);
    }
    void run() noexcept {
        ComApartment apartment; DWORD task_index = 0; HANDLE mmcss = nullptr;
        ComPtr<IAudioClient> client; ComPtr<IAudioCaptureClient> capture; ComPtr<IMMDeviceEnumerator> enumerator;
        HRESULT failure = S_OK; Packet pending; bool has_pending = false;
        try {
            check(apartment.result); check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)));
            if (kind == FT_AUDIO_PROCESS_TREE) {
                AUDIOCLIENT_ACTIVATION_PARAMS params{}; params.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
                params.ProcessLoopbackParams.TargetProcessId = process; params.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
                PROPVARIANT variant{}; variant.vt = VT_BLOB; variant.blob.cbSize = sizeof(params); variant.blob.pBlobData = reinterpret_cast<BYTE*>(&params);
                auto activation = Microsoft::WRL::Make<Activation>(); ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
                check(ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient), &variant, activation.Get(), &operation));
                if (WaitForSingleObject(activation->completed.value, 10000) != WAIT_OBJECT_0) throw HRESULT_FROM_WIN32(ERROR_TIMEOUT);
                check(activation->result); client = activation->client;
            } else {
                ComPtr<IMMDevice> device;
                if (device_id.empty()) { used_default = true; check(enumerator->GetDefaultAudioEndpoint(kind == FT_AUDIO_MICROPHONE ? eCapture : eRender, eConsole, &device)); LPWSTR id = nullptr; check(device->GetId(&id)); device_id = id; CoTaskMemFree(id); }
                else check(enumerator->GetDevice(device_id.c_str(), &device));
                check(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &client));
                WAVEFORMATEX* native = nullptr; check(client->GetMixFormat(&native));
                { std::lock_guard lock(mutex); status.native_sample_rate = native->nSamplesPerSec; status.native_channels = native->nChannels; } CoTaskMemFree(native);
            }
            WAVEFORMATEX format{}; format.wFormatTag = WAVE_FORMAT_IEEE_FLOAT; format.nChannels = Channels; format.nSamplesPerSec = Rate;
            format.wBitsPerSample = 32; format.nBlockAlign = BytesPerFrame; format.nAvgBytesPerSec = Rate * BytesPerFrame;
            DWORD flags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY | AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
            if (kind != FT_AUDIO_MICROPHONE) flags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
            check(client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 1000000, 0, &format, nullptr));
            Handle event; event.value = CreateEventW(nullptr, FALSE, FALSE, nullptr); if (!event.value) throw HRESULT_FROM_WIN32(GetLastError());
            check(client->SetEventHandle(event.value)); check(client->GetService(IID_PPV_ARGS(&capture)));
            wave.open(output);
            if (!diagnostic.empty()) { Handle probe; probe.value = CreateFileW(diagnostic.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr); if (probe.value == INVALID_HANDLE_VALUE) throw HRESULT_FROM_WIN32(GetLastError()); CloseHandle(probe.value); probe.value = INVALID_HANDLE_VALUE; log.open(diagnostic); if (!log) throw E_FAIL; }
            mmcss = AvSetMmThreadCharacteristicsW(L"Audio", &task_index); check(client->Start());
            { std::lock_guard lock(mutex); ready = true; init_result = S_OK; message(L"Audio ready; waiting for first screen frame."); initialized.notify_all(); }
            int64_t last_default_poll = now_qpc(), last_checkpoint = now_qpc();
            for (;;) {
                WaitForSingleObject(event.value, 10);
                UINT32 count = 0; check(capture->GetNextPacketSize(&count));
                while (count) {
                    BYTE* bytes = nullptr; UINT32 frames = 0; DWORD packet_flags = 0; UINT64 position = 0, qpc100 = 0;
                    check(capture->GetBuffer(&bytes, &frames, &packet_flags, &position, &qpc100));
                    Packet p; p.device_position = position; p.flags = packet_flags;
                    p.qpc = (packet_flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) ? now_qpc() - static_cast<int64_t>(static_cast<long double>(frames) * status.qpc_frequency / Rate) : qpc_from_100ns(qpc100);
                    try { p.samples.resize(static_cast<size_t>(frames) * Channels); if (!(packet_flags & AUDCLNT_BUFFERFLAGS_SILENT)) { std::copy_n(reinterpret_cast<float*>(bytes), p.samples.size(), p.samples.data()); for (float& sample : p.samples) if (!std::isfinite(sample)) sample = 0; } }
                    catch (...) { capture->ReleaseBuffer(frames); throw; }
                    check(capture->ReleaseBuffer(frames)); // copied data is owned before file IO
                    {
                        std::lock_guard lock(mutex); ++status.packets;
                        if (packet_flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) ++status.discontinuities;
                        if (packet_flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) ++status.timestamp_errors;
                        if (!first_audio_qpc) { first_audio_qpc = p.qpc; first_position = position; }
                        // Windows process-loopback currently reports devicePosition=0 on every packet.
                        // Preserve raw values in diagnostics, but never advertise a bogus -1,000,000 ppm drift.
                        if (p.qpc > first_audio_qpc && position > first_position) {
                            const double device_seconds = static_cast<double>(position - first_position) / Rate;
                            const double qpc_seconds = static_cast<double>(p.qpc - first_audio_qpc) / status.qpc_frequency;
                            const double error = (device_seconds - qpc_seconds) * 1000;
                            status.max_timestamp_error_ms = std::max(status.max_timestamp_error_ms, std::abs(error));
                            if (qpc_seconds >= 1) { status.drift_available = 1; status.drift_ppm = (device_seconds / qpc_seconds - 1) * 1000000; }
                        }
                        if (p.qpc - last_level_qpc >= status.qpc_frequency / 20) {
                            double sum = 0, peak = 0; for (float sample : p.samples) { if (!std::isfinite(sample)) continue; sum += static_cast<double>(sample) * sample; peak = std::max(peak, std::abs(static_cast<double>(sample))); }
                            status.peak = peak; status.rms = p.samples.empty() ? 0 : std::sqrt(sum / p.samples.size()); last_level_qpc = p.qpc;
                        }
                        if (log) log << "{\"devicePosition\":" << position << ",\"qpc100ns\":" << qpc100 << ",\"qpc\":" << p.qpc << ",\"frames\":" << frames << ",\"flags\":" << packet_flags << "}\n";
                        if (has_pending) {
                            auto delta = p.qpc - pending.qpc;
                            const auto nominal = static_cast<long double>(pending.samples.size() / Channels) * status.qpc_frequency / Rate;
                            if (!(p.flags & (AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY | AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR)) && delta >= nominal * .99L && delta <= nominal * 1.01L) pending.duration_qpc = delta;
                            process_packet(pending);
                        }
                        pending = std::move(p); has_pending = true;
                    }
                    check(capture->GetNextPacketSize(&count));
                }
                if (stopping.load()) break; // drain the available packet queue once before finalizing
                if (now_qpc() - last_checkpoint > status.qpc_frequency) {
                    std::lock_guard lock(mutex); wave.finalize(); last_checkpoint = now_qpc();
                }
                if (used_default && now_qpc() - last_default_poll > status.qpc_frequency) {
                    last_default_poll = now_qpc(); ComPtr<IMMDevice> current; LPWSTR id = nullptr;
                    if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(kind == FT_AUDIO_MICROPHONE ? eCapture : eRender, eConsole, &current)) && SUCCEEDED(current->GetId(&id))) {
                        if (device_id != id) { std::lock_guard lock(mutex); status.default_device_changed = 1; message(L"Default audio device changed; recording remains on the originally selected device."); } CoTaskMemFree(id);
                    }
                }
            }
        } catch (HRESULT hr) { failure = hr; } catch (const std::bad_alloc&) { failure = E_OUTOFMEMORY; } catch (...) { failure = E_FAIL; }
        if (client) client->Stop(); if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
        std::lock_guard lock(mutex);
        try { if (has_pending) process_packet(pending); finish_file(SUCCEEDED(failure)); } catch (HRESULT hr) { if (SUCCEEDED(failure)) failure = hr; } catch (...) { failure = E_FAIL; }
        wave.close();
        if (!ready) { ready = true; init_result = failure; initialized.notify_all(); }
        status.error_code = failure; status.state = FAILED(failure) ? FT_AUDIO_FAILED : FT_AUDIO_STOPPED;
        if (FAILED(failure)) message(failure == AUDCLNT_E_DEVICE_INVALIDATED ? L"Audio device disconnected. Completed audio was preserved; this track is unavailable." : L"Audio capture failed. Inspect HRESULT and per-packet diagnostics; completed samples were preserved.");
        else message(L"Audio track finalized.");
        if (log) { log << "{\"event\":\"finalized\",\"frames\":" << status.frames_written << ",\"hresult\":" << failure << "}\n"; log.close(); }
    }
};
template<class F> HRESULT boundary(F&& f) noexcept { try { f(); return S_OK; } catch (HRESULT hr) { return hr; } catch (const std::invalid_argument&) { return E_INVALIDARG; } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; } }
}
extern "C" {
int32_t ft_audio_enumerate_devices(uint32_t kind, ft_audio_device* devices, uint32_t capacity, uint32_t* count) {
    if (!count || (kind != FT_AUDIO_SYSTEM && kind != FT_AUDIO_MICROPHONE) || (capacity && !devices)) return E_INVALIDARG;
    return boundary([&] {
        ComApartment apartment; if (FAILED(apartment.result) && apartment.result != RPC_E_CHANGED_MODE) check(apartment.result);
        ComPtr<IMMDeviceEnumerator> enumerator; check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)));
        auto flow = kind == FT_AUDIO_MICROPHONE ? eCapture : eRender; ComPtr<IMMDeviceCollection> collection; check(enumerator->EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, &collection));
        UINT length = 0; check(collection->GetCount(&length)); *count = length; if (!capacity) return;
        if (capacity < length) throw HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        ComPtr<IMMDevice> default_device; std::wstring default_id; if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(flow, eConsole, &default_device))) { LPWSTR id = nullptr; if (SUCCEEDED(default_device->GetId(&id))) { default_id = id; CoTaskMemFree(id); } }
        for (UINT i = 0; i < length; ++i) {
            ComPtr<IMMDevice> device; check(collection->Item(i, &device)); LPWSTR id = nullptr; check(device->GetId(&id));
            ft_audio_device value{}; value.struct_size = sizeof(value); value.kind = kind; value.is_default = default_id == id; copy_text(value.id, id); CoTaskMemFree(id);
            ComPtr<IPropertyStore> properties; check(device->OpenPropertyStore(STGM_READ, &properties)); PROPVARIANT name; PropVariantInit(&name);
            if (SUCCEEDED(properties->GetValue(PKEY_Device_FriendlyName, &name)) && name.vt == VT_LPWSTR) copy_text(value.name, name.pwszVal); PropVariantClear(&name); devices[i] = value;
        }
    });
}
int32_t ft_audio_process_from_window(uintptr_t window, uint32_t* process_id) {
    if (!process_id || !IsWindow(reinterpret_cast<HWND>(window))) return E_INVALIDARG;
    DWORD pid = 0; GetWindowThreadProcessId(reinterpret_cast<HWND>(window), &pid); *process_id = pid; return pid ? S_OK : HRESULT_FROM_WIN32(GetLastError());
}
int32_t ft_audio_process_loopback_supported(uint32_t* supported) {
    if (!supported) return E_POINTER; *supported = 0;
    using RtlGetVersionFn = LONG(WINAPI*)(OSVERSIONINFOW*); auto module = GetModuleHandleW(L"ntdll.dll");
    auto fn = reinterpret_cast<RtlGetVersionFn>(GetProcAddress(module, "RtlGetVersion")); OSVERSIONINFOW info{}; info.dwOSVersionInfoSize = sizeof(info);
    if (!fn || fn(&info)) return E_FAIL; *supported = info.dwBuildNumber >= 20348; return S_OK;
}
int32_t ft_audio_start(const ft_audio_options* options, ft_audio_handle* handle) {
    if (!handle) return E_POINTER; *handle = nullptr;
    if (!options || options->struct_size != sizeof(*options) || options->kind < 1 || options->kind > 3 || !options->output_path || !*options->output_path || (options->kind == 3 && !options->process_id)) return E_INVALIDARG;
    return boundary([&] {
        if (options->kind == FT_AUDIO_PROCESS_TREE) { uint32_t supported = 0; check(ft_audio_process_loopback_supported(&supported)); if (!supported) throw HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED); }
        auto session = std::make_unique<AudioSession>(); session->kind = options->kind; session->process = options->process_id; session->output = options->output_path;
        if (options->device_id) session->device_id = options->device_id; if (options->diagnostics_path) session->diagnostic = options->diagnostics_path;
        session->worker = std::thread([raw = session.get()] { raw->run(); });
        std::unique_lock lock(session->mutex); session->initialized.wait(lock, [&] { return session->ready; }); auto hr = session->init_result; lock.unlock(); check(hr); *handle = session.release();
    });
}
int32_t ft_audio_set_epoch(ft_audio_handle handle, int64_t qpc, int64_t frequency) {
    if (!handle) return E_POINTER; return boundary([&] { auto& s = *static_cast<AudioSession*>(handle); std::lock_guard lock(s.mutex);
        if (s.status.state != FT_AUDIO_PREPARING || frequency != s.status.qpc_frequency || qpc > now_qpc()) throw E_INVALIDARG;
        s.clock.set_epoch(qpc, frequency); s.status.epoch_qpc = qpc; s.status.state = FT_AUDIO_RECORDING; s.message(L"Recording 48 kHz stereo float PCM; sample zero aligned to first screen frame.");
        if (s.log) s.log << "{\"event\":\"epoch\",\"qpc\":" << qpc << ",\"frequency\":" << frequency << "}\n";
        for (auto& packet : s.preroll) s.process_packet(packet); s.preroll.clear();
    });
}
int32_t ft_audio_pause_at(ft_audio_handle handle, int64_t qpc) {
    if (!handle) return E_POINTER; return boundary([&] { auto& s = *static_cast<AudioSession*>(handle); std::lock_guard lock(s.mutex);
        if (s.status.state != FT_AUDIO_RECORDING || qpc > now_qpc()) throw E_INVALIDARG; s.clock.pause(qpc);
        s.wave.truncate(static_cast<uint64_t>(s.clock.frames_at(qpc))); s.status.frames_written = s.wave.frames(); s.status.duration_100ns = static_cast<int64_t>(s.wave.frames() * 10000000 / Rate); s.status.state = FT_AUDIO_PAUSED; s.status.peak = s.status.rms = 0;
        if (s.log) s.log << "{\"event\":\"pause\",\"qpc\":" << qpc << "}\n";
    });
}
int32_t ft_audio_resume_at(ft_audio_handle handle, int64_t qpc) {
    if (!handle) return E_POINTER; return boundary([&] { auto& s = *static_cast<AudioSession*>(handle); std::lock_guard lock(s.mutex);
        if (s.status.state != FT_AUDIO_PAUSED || qpc > now_qpc()) throw E_INVALIDARG; s.clock.resume(qpc); s.status.state = FT_AUDIO_RECORDING;
        if (s.log) s.log << "{\"event\":\"resume\",\"qpc\":" << qpc << "}\n";
    });
}
int32_t ft_audio_stop_at(ft_audio_handle handle, int64_t qpc) {
    if (!handle) return E_POINTER; return boundary([&] { auto& s = *static_cast<AudioSession*>(handle); { std::lock_guard lock(s.mutex); if (qpc <= 0 || qpc > now_qpc() || (s.clock.frequency && qpc < s.clock.epoch)) throw E_INVALIDARG; s.stop_qpc = qpc; } s.stopping = true; if (s.worker.joinable()) s.worker.join(); std::lock_guard lock(s.mutex); check(s.status.error_code); });
}
int32_t ft_audio_get_status(ft_audio_handle handle, ft_audio_status* status) {
    if (!handle || !status || status->struct_size != sizeof(*status)) return E_INVALIDARG; auto& s = *static_cast<AudioSession*>(handle); std::lock_guard lock(s.mutex); *status = s.status; return S_OK;
}
void ft_audio_destroy(ft_audio_handle handle) { try { delete static_cast<AudioSession*>(handle); } catch (...) {} }
}
