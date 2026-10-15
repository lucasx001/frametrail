#pragma once
#include "recording.h"
#include <mfapi.h>
#include <wrl/client.h>
#include <atomic>
#include <condition_variable>
#include <deque>
#include <mutex>

// Each file owns its timestamps and bounded queue. Overlapping files share only immutable
// payload buffers from the device; their IMFSample metadata must never be shared.
struct CameraFrames {
    struct Frame { Microsoft::WRL::ComPtr<IMFSample> sample; int64_t qpc; };
    std::mutex mutex;
    std::condition_variable changed;
    std::deque<Frame> queue;
    std::atomic<bool> stopping{};
    bool anchored{};
    int64_t epoch_qpc{};
    ft_camera_status status{sizeof(ft_camera_status), 0};

    void receive(IMFSample* sample, int64_t qpc) {
        std::lock_guard lock(mutex);
        if (stopping) return;
        if (queue.size() == 4) {
            // Discarding old pre-roll while a writer initializes is intentional;
            // count only samples belonging to this file as encoder backpressure.
            if (epoch_qpc && queue.front().qpc >= epoch_qpc) ++status.frames_dropped;
            queue.pop_front();
        }
        queue.push_back({sample, qpc}); anchored = true; changed.notify_all();
    }
    void fail(HRESULT hr) {
        std::lock_guard lock(mutex);
        status.state = 3; status.error_code = hr;
        wcsncpy_s(status.message, L"Camera ended, changed format or disconnected.", _TRUNCATE);
        stopping = true; changed.notify_all();
    }
};

inline Microsoft::WRL::ComPtr<IMFSample> camera_file_sample(IMFSample* source, int64_t time, int64_t duration) {
    Microsoft::WRL::ComPtr<IMFSample> result;
    HRESULT hr = MFCreateSample(&result);
    if (FAILED(hr)) throw hr;
    hr = source->CopyAllItems(result.Get()); if (FAILED(hr)) throw hr;
    DWORD count{}; hr = source->GetBufferCount(&count); if (FAILED(hr)) throw hr;
    for (DWORD i = 0; i < count; ++i) {
        Microsoft::WRL::ComPtr<IMFMediaBuffer> buffer;
        hr = source->GetBufferByIndex(i, &buffer); if (FAILED(hr)) throw hr;
        hr = result->AddBuffer(buffer.Get()); if (FAILED(hr)) throw hr;
    }
    hr = result->SetSampleTime(time); if (FAILED(hr)) throw hr;
    hr = result->SetSampleDuration(duration); if (FAILED(hr)) throw hr;
    return result;
}
