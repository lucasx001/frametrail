#include "recording.h"
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wrl/client.h>
#include <atomic>
#include <algorithm>
#include <condition_variable>
#include <chrono>
#include <deque>
#include <future>
#include <mutex>
#include <memory>
#include <thread>
#include <vector>
#include <string>
using Microsoft::WRL::ComPtr;
namespace {
constexpr DWORD video_stream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
constexpr DWORD audio_stream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM);
constexpr DWORD all_streams = static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS);
constexpr DWORD media_source = static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE);
void check(HRESULT hr) { if (FAILED(hr)) throw hr; }
struct Runtime {
    bool co{}; Runtime() { auto hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED); if (SUCCEEDED(hr)) co = true; else if (hr != RPC_E_CHANGED_MODE) check(hr); check(MFStartup(MF_VERSION)); }
    ~Runtime() { MFShutdown(); if (co) CoUninitialize(); }
};
std::vector<ComPtr<IMFActivate>> devices() {
    ComPtr<IMFAttributes> attr; check(MFCreateAttributes(&attr, 1)); check(attr->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID));
    IMFActivate** array{}; UINT32 count{}; check(MFEnumDeviceSources(attr.Get(), &array, &count));
    std::vector<ComPtr<IMFActivate>> result; for (UINT32 i = 0; i < count; ++i) { ComPtr<IMFActivate> item; item.Attach(array[i]); result.push_back(item); } CoTaskMemFree(array); return result;
}
std::wstring device_string(IMFActivate* item, REFGUID key) { wchar_t* data{}; UINT32 size{}; check(item->GetAllocatedString(key, &data, &size)); std::wstring result(data, size); CoTaskMemFree(data); return result; }
ComPtr<IMFMediaSource> open_source(const std::wstring& id) {
    for (const auto& item : devices()) if (device_string(item.Get(), MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK) == id) {
        ComPtr<IMFMediaSource> source; check(item->ActivateObject(IID_PPV_ARGS(&source))); return source;
    }
    throw HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED);
}
int64_t now() { LARGE_INTEGER value{}; QueryPerformanceCounter(&value); return value.QuadPart; }
int64_t scaled(int64_t value, int64_t frequency) { return static_cast<int64_t>((static_cast<long double>(value) * 10'000'000) / frequency); }
class Camera final : public IMFSourceReaderCallback {
    std::atomic<ULONG> refs_{1};
    struct Frame { ComPtr<IMFSample> sample; int64_t qpc; };
    std::mutex mutex_; std::condition_variable changed_; std::deque<Frame> queue_;
    std::thread worker_; std::atomic<bool> stopping_{};
    ComPtr<IMFSourceReader> reader_; ComPtr<IMFMediaSource> source_;
    std::wstring id_, path_;
    uint32_t width_, height_, fps_numerator_, fps_denominator_; int64_t epoch_{}, frequency_{}, stop_qpc_{}, anchor_qpc_{}, anchor_time_{};
    ft_camera_status status_{sizeof(ft_camera_status), 0};
public:
    Camera(const ft_camera_options& options) : id_(options.device_id), path_(options.output_path), width_(options.width), height_(options.height), fps_numerator_(options.fps_numerator), fps_denominator_(options.fps_denominator) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override { if (!value) return E_POINTER; *value = nullptr; if (iid == __uuidof(IUnknown) || iid == __uuidof(IMFSourceReaderCallback)) { *value = static_cast<IMFSourceReaderCallback*>(this); AddRef(); return S_OK; } return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override { auto count = --refs_; if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE OnEvent(DWORD, IMFMediaEvent*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnFlush(DWORD) override { changed_.notify_all(); return S_OK; }
    HRESULT STDMETHODCALLTYPE OnReadSample(HRESULT hr, DWORD, DWORD flags, LONGLONG timestamp, IMFSample* sample) override {
        ComPtr<IMFSourceReader> next_reader;
        {
            std::lock_guard lock(mutex_);
            if (FAILED(hr) || (flags & (MF_SOURCE_READERF_ERROR | MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED))) { fail(FAILED(hr) ? hr : E_FAIL, L"Camera capture ended, changed format or the device disconnected."); stopping_ = true; changed_.notify_all(); return S_OK; }
            if (stopping_) return S_OK;
            if (sample) {
                if (!anchor_qpc_) { anchor_qpc_ = now(); anchor_time_ = timestamp; }
                LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency);
                auto mapped = anchor_qpc_ + static_cast<int64_t>((static_cast<long double>(timestamp - anchor_time_) * frequency.QuadPart) / 10'000'000);
                if (queue_.size() == 4) { queue_.pop_front(); ++status_.frames_dropped; }
                queue_.push_back({sample, mapped}); changed_.notify_all();
            }
            next_reader = reader_;
        }
        if (!stopping_ && next_reader) { auto result = next_reader->ReadSample(video_stream, 0, nullptr, nullptr, nullptr, nullptr); if (FAILED(result)) { std::lock_guard lock(mutex_); fail(result, L"Camera stopped delivering samples."); stopping_ = true; changed_.notify_all(); } }
        return S_OK;
    }
    void fail(HRESULT hr, const wchar_t* message) { status_.state = 3; status_.error_code = hr; wcsncpy_s(status_.message, message, _TRUNCATE); }
    HRESULT start() {
        std::promise<HRESULT> ready; auto future = ready.get_future();
        worker_ = std::thread([this, ready = std::move(ready)]() mutable {
            bool signaled = false; std::unique_ptr<Runtime> runtime; ComPtr<IMFSinkWriter> writer; DWORD stream{};
            try {
                runtime = std::make_unique<Runtime>();
                if (GetFileAttributesW(path_.c_str()) != INVALID_FILE_ATTRIBUTES) throw HRESULT_FROM_WIN32(ERROR_FILE_EXISTS);
                source_ = open_source(id_);
                ComPtr<IMFAttributes> attr; check(MFCreateAttributes(&attr, 3)); check(attr->SetUnknown(MF_SOURCE_READER_ASYNC_CALLBACK, this)); check(attr->SetUINT32(MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE));
                check(MFCreateSourceReaderFromMediaSource(source_.Get(), attr.Get(), &reader_));
                check(reader_->SetStreamSelection(all_streams, FALSE)); check(reader_->SetStreamSelection(video_stream, TRUE));
                // Select an exact native size. Source Reader converts the device subtype to RGB32, never mirrors source media.
                ComPtr<IMFMediaType> selected;
                for (DWORD index = 0;; ++index) {
                    ComPtr<IMFMediaType> type; if (FAILED(reader_->GetNativeMediaType(video_stream, index, &type))) break;
                    UINT32 w{}, h{}, n{}, d{}; MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h); MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &n, &d);
                    if (w == width_ && h == height_ && d && static_cast<uint64_t>(n) * fps_denominator_ == static_cast<uint64_t>(fps_numerator_) * d) { selected = type; break; }
                }
                if (!selected) throw MF_E_INVALIDMEDIATYPE;
                check(reader_->SetCurrentMediaType(video_stream, nullptr, selected.Get()));
                ComPtr<IMFMediaType> input; check(MFCreateMediaType(&input)); check(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video)); check(input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32));
                check(reader_->SetCurrentMediaType(video_stream, nullptr, input.Get()));
                check(reader_->GetCurrentMediaType(video_stream, &input));
                ComPtr<IMFAttributes> writer_attr; check(MFCreateAttributes(&writer_attr, 2)); check(writer_attr->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE)); check(writer_attr->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE));
                check(MFCreateSinkWriterFromURL(path_.c_str(), nullptr, writer_attr.Get(), &writer));
                ComPtr<IMFMediaType> output; check(MFCreateMediaType(&output)); check(output->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video)); check(output->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
                check(output->SetUINT32(MF_MT_AVG_BITRATE, 4'000'000)); check(output->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
                check(MFSetAttributeSize(output.Get(), MF_MT_FRAME_SIZE, width_, height_)); check(MFSetAttributeRatio(output.Get(), MF_MT_FRAME_RATE, fps_numerator_, fps_denominator_)); check(MFSetAttributeRatio(output.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
                check(writer->AddStream(output.Get(), &stream)); check(writer->SetInputMediaType(stream, input.Get(), nullptr)); check(writer->BeginWriting());
                { std::lock_guard lock(mutex_); status_.state = 1; status_.width = width_; status_.height = height_; }
                check(reader_->ReadSample(video_stream, 0, nullptr, nullptr, nullptr, nullptr));
                {
                    std::unique_lock lock(mutex_);
                    if (!changed_.wait_for(lock, std::chrono::seconds(10), [&] { return anchor_qpc_ != 0 || stopping_.load(); }))
                        throw HRESULT_FROM_WIN32(WAIT_TIMEOUT);
                    if (FAILED(status_.error_code)) throw status_.error_code;
                    if (!anchor_qpc_ || stopping_) throw HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED);
                }
                ready.set_value(S_OK); signaled = true;
                int64_t first_time = -1, last_time = -1;
                for (;;) {
                    Frame frame;
                    int64_t epoch, frequency, end;
                    { std::unique_lock lock(mutex_); changed_.wait(lock, [&] { return stopping_ || (epoch_ && !queue_.empty()); });
                        if (queue_.empty()) { if (stopping_) break; continue; }
                        if (!epoch_) { queue_.clear(); break; }
                        frame = std::move(queue_.front()); queue_.pop_front(); epoch = epoch_; frequency = frequency_; end = stop_qpc_;
                    }
                    if (frame.qpc < epoch || (end && frame.qpc >= end)) continue;
                    auto time = scaled(frame.qpc - epoch, frequency);
                    if (first_time < 0) first_time = time;
                    auto local_time = time - first_time;
                    if (local_time <= last_time) continue;
                    auto frame_duration = static_cast<int64_t>(10'000'000ULL * fps_denominator_ / fps_numerator_);
                    check(frame.sample->SetSampleTime(local_time)); check(frame.sample->SetSampleDuration(frame_duration)); check(writer->WriteSample(stream, frame.sample.Get()));
                    last_time = local_time;
                    { std::lock_guard lock(mutex_); ++status_.frames_written; status_.first_offset_100ns = first_time; status_.duration_100ns = local_time + frame_duration; }
                }
                check(writer->Finalize());
                { std::lock_guard lock(mutex_); if (status_.state != 3) status_.state = 2; }
            } catch (HRESULT hr) { if (writer) writer->Finalize(); std::lock_guard lock(mutex_); fail(hr, L"Camera capture/encoding failed. Verify device access and selected format."); if (!signaled) { ready.set_value(hr); signaled = true; } }
            catch (...) { std::lock_guard lock(mutex_); fail(E_FAIL, L"Unexpected camera failure."); if (!signaled) ready.set_value(E_FAIL); }
            stopping_ = true;
            ComPtr<IMFSourceReader> release_reader; ComPtr<IMFMediaSource> release_source;
            { std::lock_guard lock(mutex_); release_reader = std::move(reader_); release_source = std::move(source_); queue_.clear(); }
            if (release_source) release_source->Shutdown();
            // Never invoke COM releases while holding the callback mutex: shutdown may wait for an in-flight callback.
            release_reader.Reset(); release_source.Reset();
            writer.Reset(); runtime.reset();
        });
        return future.get();
    }
    HRESULT epoch(int64_t qpc, int64_t frequency) { std::lock_guard lock(mutex_); if (epoch_ || qpc <= 0 || frequency <= 0) return E_INVALIDARG; epoch_ = qpc; frequency_ = frequency; changed_.notify_all(); return S_OK; }
    void stop(int64_t qpc) { { std::lock_guard lock(mutex_); if (!stop_qpc_) stop_qpc_ = qpc; stopping_ = true; changed_.notify_all(); } if (worker_.joinable()) worker_.join(); }
    ft_camera_status status() { std::lock_guard lock(mutex_); return status_; }
};
template<class F> HRESULT protect(F&& call) { try { return call(); } catch (HRESULT hr) { return hr; } catch (...) { return E_FAIL; } }
}
extern "C" {
int32_t __cdecl ft_recording_probe(const wchar_t* path, uint32_t video, int64_t* duration) { return protect([&]() -> HRESULT {
    if (!path || !duration) return E_POINTER;
    Runtime runtime; ComPtr<IMFAttributes> attr; check(MFCreateAttributes(&attr, 1)); check(attr->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE));
    ComPtr<IMFSourceReader> reader; check(MFCreateSourceReaderFromURL(path, attr.Get(), &reader));
    const DWORD index = video ? video_stream : audio_stream;
    check(reader->SetStreamSelection(all_streams, FALSE)); check(reader->SetStreamSelection(index, TRUE));
    ComPtr<IMFMediaType> type; check(MFCreateMediaType(&type)); check(type->SetGUID(MF_MT_MAJOR_TYPE, video ? MFMediaType_Video : MFMediaType_Audio)); check(type->SetGUID(MF_MT_SUBTYPE, video ? MFVideoFormat_RGB32 : MFAudioFormat_PCM));
    check(reader->SetCurrentMediaType(index, nullptr, type.Get()));
    PROPVARIANT value{}; PropVariantInit(&value); auto hr = reader->GetPresentationAttribute(media_source, MF_PD_DURATION, &value);
    if (FAILED(hr) || value.vt != VT_UI8 || value.uhVal.QuadPart == 0) { PropVariantClear(&value); return MF_E_INVALID_FILE_FORMAT; }
    *duration = static_cast<int64_t>(value.uhVal.QuadPart); PropVariantClear(&value);
    for (int attempt = 0; attempt < 100; ++attempt) { DWORD flags{}; ComPtr<IMFSample> sample; check(reader->ReadSample(index, 0, nullptr, &flags, nullptr, &sample));
        if (sample) { DWORD size{}; check(sample->GetTotalLength(&size)); if (size) return S_OK; }
        if (flags & (MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READERF_ERROR)) break;
    }
    return MF_E_INVALID_FILE_FORMAT;
}); }
int32_t __cdecl ft_camera_enumerate(ft_camera_device* output, uint32_t capacity, uint32_t* count) { return protect([&]() -> HRESULT {
    if (!count) return E_POINTER; Runtime runtime; auto list = devices(); *count = static_cast<uint32_t>(list.size()); if (!output) return S_OK; if (capacity < *count) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    for (uint32_t i = 0; i < *count; ++i) { output[i] = {}; wcscpy_s(output[i].id, device_string(list[i].Get(), MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK).c_str()); wcsncpy_s(output[i].name, device_string(list[i].Get(), MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME).c_str(), _TRUNCATE); } return S_OK; }); }
int32_t __cdecl ft_camera_formats(const wchar_t* id, ft_camera_format* output, uint32_t capacity, uint32_t* count) { return protect([&]() -> HRESULT {
    if (!id || !count) return E_POINTER; Runtime runtime; auto source = open_source(id); ComPtr<IMFSourceReader> reader;
    check(MFCreateSourceReaderFromMediaSource(source.Get(), nullptr, &reader)); std::vector<ft_camera_format> formats;
    for (DWORD index = 0;; ++index) { ComPtr<IMFMediaType> type; if (FAILED(reader->GetNativeMediaType(video_stream, index, &type))) break;
        ft_camera_format f{}; MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &f.width, &f.height); MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &f.fps_numerator, &f.fps_denominator);
        if (f.width && f.height && f.fps_numerator && f.fps_denominator && f.width <= 1920 && f.height <= 1080 && static_cast<uint64_t>(f.fps_numerator) <= 60ULL * f.fps_denominator &&
            std::none_of(formats.begin(), formats.end(), [&](auto& x) { return x.width == f.width && x.height == f.height && static_cast<uint64_t>(x.fps_numerator) * f.fps_denominator == static_cast<uint64_t>(f.fps_numerator) * x.fps_denominator; })) formats.push_back(f);
    }
    source->Shutdown(); *count = static_cast<uint32_t>(formats.size()); if (!output) return S_OK; if (capacity < *count) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER); std::copy(formats.begin(), formats.end(), output); return S_OK; }); }
int32_t __cdecl ft_camera_start(const ft_camera_options* options, ft_camera_handle* handle) { return protect([&]() -> HRESULT {
    if (!options || !handle || options->size != sizeof(*options) || !options->device_id || !options->output_path || !options->fps_numerator || !options->fps_denominator || static_cast<uint64_t>(options->fps_numerator) > 60ULL * options->fps_denominator || !options->width || !options->height || options->width > 1920 || options->height > 1080) return E_INVALIDARG;
    *handle = nullptr; auto camera = new Camera(*options); auto hr = camera->start(); if (FAILED(hr)) { camera->stop(now()); camera->Release(); return hr; } *handle = camera; return S_OK; }); }
int32_t __cdecl ft_camera_set_epoch(ft_camera_handle handle, int64_t qpc, int64_t frequency) { return handle ? static_cast<Camera*>(handle)->epoch(qpc, frequency) : E_POINTER; }
int32_t __cdecl ft_camera_stop_at(ft_camera_handle handle, int64_t qpc) { return protect([&]() -> HRESULT { if (!handle || qpc <= 0) return E_INVALIDARG; static_cast<Camera*>(handle)->stop(qpc); return S_OK; }); }
int32_t __cdecl ft_camera_get_status(ft_camera_handle handle, ft_camera_status* status) { if (!handle || !status || status->size != sizeof(*status)) return E_INVALIDARG; *status = static_cast<Camera*>(handle)->status(); return S_OK; }
void __cdecl ft_camera_destroy(ft_camera_handle handle) { if (handle) { auto camera = static_cast<Camera*>(handle); camera->stop(now()); camera->Release(); } }
}
