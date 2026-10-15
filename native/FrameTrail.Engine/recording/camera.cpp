#include "recording.h"
#include "camera_frames.h"
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
#include <map>
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
struct CameraFeedState {
    std::mutex mutex;
    bool stopping{};
    int64_t anchor_qpc{}, anchor_time{}, frequency{};
    HRESULT error{S_OK};
    ComPtr<IMFSourceReader> reader;
    std::vector<std::weak_ptr<CameraFrames>> subscribers;
};
class CameraCallback final : public IMFSourceReaderCallback {
    std::atomic<ULONG> refs_{1};
    std::shared_ptr<CameraFeedState> state_;
public:
    explicit CameraCallback(std::shared_ptr<CameraFeedState> state) : state_(std::move(state)) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override { if (!value) return E_POINTER; *value = nullptr; if (iid == __uuidof(IUnknown) || iid == __uuidof(IMFSourceReaderCallback)) { *value = static_cast<IMFSourceReaderCallback*>(this); AddRef(); return S_OK; } return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override { auto count = --refs_; if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE OnEvent(DWORD, IMFMediaEvent*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnFlush(DWORD) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnReadSample(HRESULT hr, DWORD, DWORD flags, LONGLONG timestamp, IMFSample* sample) override {
        // State outlives callbacks, but does not own the feed. Last-file shutdown always runs
        // on a recording worker, never inside this source-reader callback.
        std::unique_lock lock(state_->mutex);
        if (state_->stopping) return S_OK;
        if (SUCCEEDED(hr) && (flags & (MF_SOURCE_READERF_ERROR | MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED))) hr = E_FAIL;
        try {
            if (SUCCEEDED(hr) && sample) {
                if (!state_->anchor_qpc) { state_->anchor_qpc = now(); state_->anchor_time = timestamp; }
                auto qpc = state_->anchor_qpc + static_cast<int64_t>((static_cast<long double>(timestamp - state_->anchor_time) * state_->frequency) / 10'000'000);
                std::erase_if(state_->subscribers, [](auto const& value) { return value.expired(); });
                for (auto const& value : state_->subscribers) if (auto sink = value.lock()) sink->receive(sample, qpc);
            }
        } catch (...) { hr = E_OUTOFMEMORY; }
        if (SUCCEEDED(hr)) {
            auto reader = state_->reader;
            lock.unlock();
            hr = reader->ReadSample(video_stream, 0, nullptr, nullptr, nullptr, nullptr);
            reader.Reset(); // COM release must also stay outside the callback-state mutex.
            lock.lock();
            if (state_->stopping) return S_OK;
        }
        if (FAILED(hr)) {
            state_->error = hr; state_->stopping = true;
            for (auto const& value : state_->subscribers) if (auto sink = value.lock()) sink->fail(hr);
        }
        return S_OK;
    }
};
class CameraFeed {
    std::shared_ptr<CameraFeedState> state_ = std::make_shared<CameraFeedState>();
    ComPtr<IMFMediaSource> source_;
public:
    inline static std::atomic<uint64_t> next_identity{};
    const uint64_t identity = ++next_identity;
    ft_camera_format format{};
    ComPtr<IMFMediaType> input;
    ~CameraFeed() {
        ComPtr<IMFSourceReader> reader;
        { std::lock_guard lock(state_->mutex); state_->stopping = true; reader = std::move(state_->reader); }
        if (source_) source_->Shutdown();
        reader.Reset(); source_.Reset();
    }
    void start(std::wstring const& id, ft_camera_format requested) {
        format = requested;
        LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency); state_->frequency = frequency.QuadPart;
        source_ = open_source(id);
        ComPtr<CameraCallback> callback; callback.Attach(new CameraCallback(state_));
        ComPtr<IMFAttributes> attr; check(MFCreateAttributes(&attr, 2));
        check(attr->SetUnknown(MF_SOURCE_READER_ASYNC_CALLBACK, callback.Get()));
        check(attr->SetUINT32(MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE));
        check(MFCreateSourceReaderFromMediaSource(source_.Get(), attr.Get(), &state_->reader));
        auto reader = state_->reader;
        check(reader->SetStreamSelection(all_streams, FALSE)); check(reader->SetStreamSelection(video_stream, TRUE));
        ComPtr<IMFMediaType> selected;
        for (DWORD index = 0;; ++index) {
            ComPtr<IMFMediaType> type; if (FAILED(reader->GetNativeMediaType(video_stream, index, &type))) break;
            UINT32 w{}, h{}, n{}, d{}; MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h); MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &n, &d);
            if (w == format.width && h == format.height && d && static_cast<uint64_t>(n) * format.fps_denominator == static_cast<uint64_t>(format.fps_numerator) * d) { selected = type; break; }
        }
        if (!selected) throw MF_E_INVALIDMEDIATYPE;
        check(reader->SetCurrentMediaType(video_stream, nullptr, selected.Get()));
        check(MFCreateMediaType(&input)); check(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video)); check(input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32));
        check(reader->SetCurrentMediaType(video_stream, nullptr, input.Get()));
        check(reader->GetCurrentMediaType(video_stream, &input));
        check(reader->ReadSample(video_stream, 0, nullptr, nullptr, nullptr, nullptr));
    }
    void subscribe(std::shared_ptr<CameraFrames> const& frames) {
        std::lock_guard lock(state_->mutex);
        if (FAILED(state_->error)) throw state_->error;
        if (state_->stopping) throw HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED);
        state_->subscribers.push_back(frames);
    }
};
std::mutex feeds_mutex;
std::map<std::wstring, std::weak_ptr<CameraFeed>> feeds;
std::shared_ptr<CameraFeed> camera_feed(std::wstring const& id, ft_camera_format format) {
    std::lock_guard lock(feeds_mutex);
    if (auto existing = feeds[id].lock()) {
        auto f = existing->format;
        if (f.width != format.width || f.height != format.height || static_cast<uint64_t>(f.fps_numerator) * format.fps_denominator != static_cast<uint64_t>(format.fps_numerator) * f.fps_denominator) throw MF_E_INVALIDMEDIATYPE;
        return existing;
    }
    auto result = std::make_shared<CameraFeed>(); result->start(id, format); feeds[id] = result; return result;
}
class Camera final {
    std::shared_ptr<CameraFrames> frames_ = std::make_shared<CameraFrames>();
    using Frame = CameraFrames::Frame;
    std::mutex& mutex_ = frames_->mutex;
    std::condition_variable& changed_ = frames_->changed;
    std::deque<Frame>& queue_ = frames_->queue;
    std::atomic<bool>& stopping_ = frames_->stopping;
    ft_camera_status& status_ = frames_->status;
    std::thread worker_;
    std::wstring id_, path_;
    uint32_t width_, height_, fps_numerator_, fps_denominator_; int64_t epoch_{}, frequency_{}, stop_qpc_{};
    uint64_t source_identity_{};
public:
    Camera(const ft_camera_options& options) : id_(options.device_id), path_(options.output_path), width_(options.width), height_(options.height), fps_numerator_(options.fps_numerator), fps_denominator_(options.fps_denominator) {}
    void fail(HRESULT hr, const wchar_t* message) { status_.state = 3; status_.error_code = hr; wcsncpy_s(status_.message, message, _TRUNCATE); }
    HRESULT start() {
        std::promise<HRESULT> ready; auto future = ready.get_future();
        worker_ = std::thread([this, ready = std::move(ready)]() mutable {
            bool signaled = false; std::unique_ptr<Runtime> runtime; std::shared_ptr<CameraFeed> feed; ComPtr<IMFSinkWriter> writer; DWORD stream{};
            try {
                runtime = std::make_unique<Runtime>();
                if (GetFileAttributesW(path_.c_str()) != INVALID_FILE_ATTRIBUTES) throw HRESULT_FROM_WIN32(ERROR_FILE_EXISTS);
                feed = camera_feed(id_, {width_, height_, fps_numerator_, fps_denominator_});
                { std::lock_guard lock(mutex_); source_identity_ = feed->identity; }
                feed->subscribe(frames_);
                ComPtr<IMFAttributes> writer_attr; check(MFCreateAttributes(&writer_attr, 2)); check(writer_attr->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE)); check(writer_attr->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE));
                check(MFCreateSinkWriterFromURL(path_.c_str(), nullptr, writer_attr.Get(), &writer));
                ComPtr<IMFMediaType> output; check(MFCreateMediaType(&output)); check(output->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video)); check(output->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
                check(output->SetUINT32(MF_MT_AVG_BITRATE, 4'000'000)); check(output->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
                check(MFSetAttributeSize(output.Get(), MF_MT_FRAME_SIZE, width_, height_)); check(MFSetAttributeRatio(output.Get(), MF_MT_FRAME_RATE, fps_numerator_, fps_denominator_)); check(MFSetAttributeRatio(output.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
                check(writer->AddStream(output.Get(), &stream)); check(writer->SetInputMediaType(stream, feed->input.Get(), nullptr)); check(writer->BeginWriting());
                { std::lock_guard lock(mutex_); status_.state = 1; status_.width = width_; status_.height = height_; }
                {
                    std::unique_lock lock(mutex_);
                    if (!changed_.wait_for(lock, std::chrono::seconds(10), [&] { return frames_->anchored || stopping_.load(); }))
                        throw HRESULT_FROM_WIN32(WAIT_TIMEOUT);
                    if (FAILED(status_.error_code)) throw status_.error_code;
                    if (!frames_->anchored || stopping_) throw HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED);
                }
                ready.set_value(S_OK); signaled = true;
                int64_t first_time = -1, last_time = -1;
                for (;;) {
                    Frame frame;
                    int64_t epoch, frequency, end;
                    { std::unique_lock lock(mutex_); changed_.wait(lock, [&] { return stopping_ || (epoch_ && !queue_.empty()); });
                        if (queue_.empty()) { if (stopping_) break; continue; }
                        if (!epoch_) { queue_.clear(); break; }
                        // Seed the new file with the latest frame already visible at its
                        // epoch. Otherwise every checkpoint creates a blank camera overlay
                        // until the next camera tick (screen and camera rates may differ).
                        while (first_time < 0 && queue_.size() > 1 && queue_[1].qpc <= epoch_) queue_.pop_front();
                        frame = std::move(queue_.front()); queue_.pop_front(); epoch = epoch_; frequency = frequency_; end = stop_qpc_;
                    }
                    if (end && frame.qpc >= end) continue;
                    if (frame.qpc < epoch && (first_time >= 0 || scaled(epoch - frame.qpc, frequency) > static_cast<int64_t>(20'000'000ULL * fps_denominator_ / fps_numerator_))) continue;
                    auto time = scaled(std::max(frame.qpc, epoch) - epoch, frequency);
                    if (first_time < 0) first_time = time;
                    auto local_time = time - first_time;
                    if (local_time <= last_time) continue;
                    auto frame_duration = static_cast<int64_t>(10'000'000ULL * fps_denominator_ / fps_numerator_);
                    auto sample = camera_file_sample(frame.sample.Get(), local_time, frame_duration);
                    check(writer->WriteSample(stream, sample.Get()));
                    last_time = local_time;
                    { std::lock_guard lock(mutex_); ++status_.frames_written; status_.first_offset_100ns = first_time; status_.duration_100ns = local_time + frame_duration; }
                }
                check(writer->Finalize());
                { std::lock_guard lock(mutex_); if (status_.state != 3) status_.state = 2; }
            } catch (HRESULT hr) { if (writer) writer->Finalize(); std::lock_guard lock(mutex_); fail(hr, L"Camera capture/encoding failed. Verify device access and selected format."); if (!signaled) { ready.set_value(hr); signaled = true; } }
            catch (...) { std::lock_guard lock(mutex_); fail(E_FAIL, L"Unexpected camera failure."); if (!signaled) ready.set_value(E_FAIL); }
            stopping_ = true;
            { std::lock_guard lock(mutex_); queue_.clear(); }
            // The next checkpoint retains the feed. Last-file destruction closes the device
            // outside the callback mutex and before this worker's MF runtime shuts down.
            feed.reset();
            writer.Reset(); runtime.reset();
        });
        return future.get();
    }
    HRESULT epoch(int64_t qpc, int64_t frequency) { std::lock_guard lock(mutex_); if (epoch_ || qpc <= 0 || frequency <= 0) return E_INVALIDARG; epoch_ = qpc; frames_->epoch_qpc = qpc; frequency_ = frequency; changed_.notify_all(); return S_OK; }
    void stop(int64_t qpc) { { std::lock_guard lock(mutex_); if (!stop_qpc_) stop_qpc_ = qpc; stopping_ = true; changed_.notify_all(); } if (worker_.joinable()) worker_.join(); }
    ft_camera_status status() { std::lock_guard lock(mutex_); return status_; }
    uint64_t source_identity() { std::lock_guard lock(mutex_); return source_identity_; }
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
    *handle = nullptr; auto camera = std::make_unique<Camera>(*options); auto hr = camera->start(); if (FAILED(hr)) { camera->stop(now()); return hr; } *handle = camera.release(); return S_OK; }); }
int32_t __cdecl ft_camera_set_epoch(ft_camera_handle handle, int64_t qpc, int64_t frequency) { return handle ? static_cast<Camera*>(handle)->epoch(qpc, frequency) : E_POINTER; }
int32_t __cdecl ft_camera_stop_at(ft_camera_handle handle, int64_t qpc) { return protect([&]() -> HRESULT { if (!handle || qpc <= 0) return E_INVALIDARG; static_cast<Camera*>(handle)->stop(qpc); return S_OK; }); }
int32_t __cdecl ft_camera_get_status(ft_camera_handle handle, ft_camera_status* status) { if (!handle || !status || status->size != sizeof(*status)) return E_INVALIDARG; *status = static_cast<Camera*>(handle)->status(); return S_OK; }
int32_t __cdecl ft_camera_get_source_identity(ft_camera_handle handle, uint64_t* identity) { if (!handle || !identity) return E_INVALIDARG; *identity = static_cast<Camera*>(handle)->source_identity(); return S_OK; }
void __cdecl ft_camera_destroy(ft_camera_handle handle) { if (handle) { auto camera = static_cast<Camera*>(handle); camera->stop(now()); delete camera; } }
}
