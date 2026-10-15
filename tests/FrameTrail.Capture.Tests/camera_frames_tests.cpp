#include "camera_frames.h"
#include <windows.h>
#include <iostream>
using Microsoft::WRL::ComPtr;
int main() {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED); MFStartup(MF_VERSION);
    int failures{};
    auto expect = [&](bool value, const char* name) { if (!value) { ++failures; std::cerr << name << '\n'; } };
    {
        ComPtr<IMFSample> source; MFCreateSample(&source); source->SetSampleTime(123456);
        ComPtr<IMFMediaBuffer> payload; MFCreateMemoryBuffer(16, &payload); source->AddBuffer(payload.Get());
        CameraFrames previous, next; previous.epoch_qpc = 100;
        previous.receive(source.Get(), 100); next.receive(source.Get(), 100);
        auto a = camera_file_sample(previous.queue.front().sample.Get(), 900, 333667);
        auto b = camera_file_sample(next.queue.front().sample.Get(), 0, 333667);
        LONGLONG time{}; a->GetSampleTime(&time); expect(time == 900, "overlap changed previous file timestamp");
        b->GetSampleTime(&time); expect(time == 0, "next file timestamp not independent");
        source->GetSampleTime(&time); expect(time == 123456, "source metadata was mutated");
        ComPtr<IMFMediaBuffer> a_buffer, b_buffer; a->GetBufferByIndex(0, &a_buffer); b->GetBufferByIndex(0, &b_buffer);
        expect(a_buffer.Get() == b_buffer.Get(), "immutable payload should be retained without a second copy");
        for (int i = 101; i < 106; ++i) previous.receive(source.Get(), i);
        expect(previous.queue.size() == 4 && previous.queue.front().qpc == 102 && previous.status.frames_dropped == 2, "slow encoder queue is not bounded");
        previous.stopping = true; previous.receive(source.Get(), 200);
        expect(previous.queue.back().qpc == 105, "stopped file accepted a new sample");
        next.fail(E_FAIL); expect(next.stopping && next.status.error_code == E_FAIL, "device failure not delivered to subscriber");
    }
    MFShutdown(); CoUninitialize();
    std::cout << "camera-frames failures=" << failures << '\n'; return failures ? 1 : 0;
}
