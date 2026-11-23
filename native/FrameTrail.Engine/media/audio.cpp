#include "internal.hpp"
#include "pcm_wave.hpp"
#include <propvarutil.h>
#include <mmsystem.h>
#include <algorithm>
#include <cmath>
#include <functional>
#include <array>

namespace frametrail::media {
AudioReader::AudioReader(const std::filesystem::path& path) {
    FT_MEDIA_CHECK(MFCreateSourceReaderFromURL(path.c_str(), nullptr, &reader));
    FT_MEDIA_CHECK(reader->SetStreamSelection(all_streams_id, FALSE)); FT_MEDIA_CHECK(reader->SetStreamSelection(audio_stream_id, TRUE));
    ComPtr<IMFMediaType> type; FT_MEDIA_CHECK(MFCreateMediaType(&type));
    FT_MEDIA_CHECK(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio)); FT_MEDIA_CHECK(type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_Float));
    FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, channels)); FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, sample_rate));
    FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 32)); FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, channels * 4));
    FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, sample_rate * channels * 4));
    FT_MEDIA_CHECK(reader->SetCurrentMediaType(audio_stream_id, nullptr, type.Get()));
}
void AudioReader::read(int64_t first, int64_t frames) {
    if (first >= start && first + frames <= start + count) return;
    start = std::max<int64_t>(0, first - sample_rate / 4); count = std::max<int64_t>(sample_rate * 3, frames + sample_rate / 2);
    data.assign(static_cast<size_t>(count) * channels, 0);
    PROPVARIANT position{}; FT_MEDIA_CHECK(InitPropVariantFromInt64(sample_ticks(start), &position));
    auto hr = reader->SetCurrentPosition(GUID_NULL, position); PropVariantClear(&position); FT_MEDIA_CHECK(hr);
    for (int guard = 0; guard < 10000; ++guard) {
        DWORD flags{}; LONGLONG time{}; ComPtr<IMFSample> sample; FT_MEDIA_CHECK(reader->ReadSample(audio_stream_id, 0, nullptr, &flags, &time, &sample));
        if (flags & MF_SOURCE_READERF_ERROR) winrt::throw_hresult(MF_E_INVALID_FILE_FORMAT);
        if (sample) {
            ComPtr<IMFMediaBuffer> buffer; FT_MEDIA_CHECK(sample->ConvertToContiguousBuffer(&buffer)); BYTE* bytes{}; DWORD length{}; FT_MEDIA_CHECK(buffer->Lock(&bytes, nullptr, &length));
            const auto offset = ticks_samples(time); const auto n = static_cast<int64_t>(length / (channels * sizeof(float)));
            const auto begin = std::max<int64_t>(offset, start), end = std::min<int64_t>(offset + n, start + count);
            if (end > begin) memcpy(data.data() + (begin - start) * channels, reinterpret_cast<float*>(bytes) + (begin - offset) * channels, static_cast<size_t>(end - begin) * channels * sizeof(float));
            buffer->Unlock(); if (offset + static_cast<int64_t>(n) >= start + count) break;
        }
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
    }
}
float AudioReader::at(int64_t sample, int channel) const { return sample < start || sample >= start + count ? 0 : data[static_cast<size_t>(sample - start) * channels + channel]; }
namespace {
constexpr int grain = 960, hop = 480, search = 240;
using Fetch = std::function<std::vector<float>(int64_t, int)>;
using Emit = std::function<void(const float*, size_t)>;
void wsola(const Fetch& fetch, double rate, int64_t output_frames, const Emit& emit, int64_t output_first = 0) {
    if (rate == 1) {
        for (int64_t p = 0; p < output_frames; p += 4096) { auto n = static_cast<int>(std::min<int64_t>(4096, output_frames - p)); auto data = fetch(output_first + p, n); emit(data.data(), n); } return;
    }
    std::vector<float> sum(grain * channels), weight(grain), previous(hop * channels);
    bool seeded = false; int64_t written = 0;
    for (int64_t p = 0; p < output_frames; p += hop) {
        const int64_t nominal = static_cast<int64_t>(std::llround((output_first + p) * rate)); const auto begin = std::max<int64_t>(0, nominal - search);
        auto block = fetch(begin, grain + search * 2); int best = static_cast<int>(nominal - begin);
        if (seeded) {
            double best_score = -2;
            for (int delta = 0; delta <= search * 2; delta += 4) {
                double dot = 0, aa = 1e-12, bb = 1e-12;
                for (int k = 0; k < hop * channels; k += 8) { const auto a = previous[k], b = block[delta * channels + k]; dot += a * b; aa += a * a; bb += b * b; }
                auto score = dot / std::sqrt(aa * bb) - std::abs(delta - static_cast<int>(nominal - begin)) * 0.00001;
                if (score > best_score) { best_score = score; best = delta; }
            }
        }
        for (int k = 0; k < grain; ++k) {
            const auto window = static_cast<float>(0.5 - 0.5 * std::cos(2 * 3.141592653589793 * (k + 0.5) / grain));
            weight[k] += window;
            for (int c = 0; c < channels; ++c) sum[k * channels + c] += block[(best + k) * channels + c] * window;
        }
        std::copy_n(block.data() + (best + hop) * channels, hop * channels, previous.data()); seeded = true;
        const auto count = static_cast<size_t>(std::min<int64_t>(hop, output_frames - written));
        std::vector<float> ready(count * channels); for (size_t i = 0; i < count; ++i) for (int c = 0; c < channels; ++c) ready[i * channels + c] = sum[i * channels + c] / std::max(1e-6f, weight[i]);
        emit(ready.data(), count); written += count;
        std::move(sum.begin() + hop * channels, sum.end(), sum.begin()); std::fill(sum.begin() + hop * channels, sum.end(), 0.0f);
        std::move(weight.begin() + hop, weight.end(), weight.begin()); std::fill(weight.begin() + hop, weight.end(), 0.0f);
    }
}

}
std::vector<float> stretch_wsola(const std::vector<float>& input, double rate, size_t output_frames) {
    std::vector<float> result; result.reserve(output_frames * channels);
    wsola([&](int64_t first, int frames) { std::vector<float> v(static_cast<size_t>(frames) * channels); for (int i = 0; i < frames; ++i) for (int c = 0; c < channels; ++c) if (first + i >= 0 && static_cast<size_t>(first + i) * channels + c < input.size()) v[i * channels + c] = input[(first + i) * channels + c]; return v; }, rate, static_cast<int64_t>(output_frames), [&](const float* p, size_t frames) { result.insert(result.end(), p, p + frames * channels); }); return result;
}
void Session::mix_pcm(int64_t first, int64_t frames, const Emit& emit, bool report_status) {
    const auto& timeline = evaluator.timeline();
    std::unordered_map<std::string, std::unique_ptr<AudioReader>> readers; const auto settings = Json::parse(project.editor_json);
    if (report_status) { stage = 1; progress(0, static_cast<uint64_t>(frames)); }
    int64_t done = 0;
    const auto& segments = timeline.segments();
    constexpr double pi = 3.14159265358979323846;
    for (size_t seg_idx = 0; seg_idx < segments.size(); ++seg_idx) {
        const auto& clip = segments[seg_idx];
        check_cancel(); const auto clip_samples = ticks_samples(clip.source_duration), source_first = ticks_samples(clip.source_start);
        const auto clip_first = ticks_samples(clip.output_start);
        const auto begin = std::max(first, clip_first), end = std::min(first + frames, ticks_samples(clip.output_end()));
        if (end <= begin) continue;
        const auto output_count = end - begin;
        const bool seam_in = seg_idx > 0 && clip.source_start != segments[seg_idx - 1].source_end();
        const bool seam_out = seg_idx + 1 < segments.size() && clip.source_end() != segments[seg_idx + 1].source_start;
        const int64_t clip_out_total = ticks_samples(clip.output_duration);
        const int64_t fade_len = (seam_in || seam_out) ? std::min<int64_t>(288, clip_out_total / 4) : 0; // 6ms @ 48kHz
        int64_t clip_out_cursor = begin - clip_first;
        Json clip_settings = Json::object(); if (settings.contains("clipAudio") && settings["clipAudio"].contains(clip.id)) clip_settings = settings["clipAudio"][clip.id];
        auto fetch = [&](int64_t first, int count) {
            check_cancel(); std::vector<float> mixed(static_cast<size_t>(count) * channels, 0);
            for (auto& m : project.media) {
                if (m.kind != MediaKind::system_audio && m.kind != MediaKind::microphone && m.kind != MediaKind::android_audio) continue;
                const bool system = m.kind == MediaKind::system_audio || m.kind == MediaKind::android_audio; const char* gain_key = system ? "systemGain" : "microphoneGain", *mute_key = system ? "systemMuted" : "microphoneMuted";
                if (settings.value(mute_key, false) || clip_settings.value(mute_key, false)) continue;
                const auto gain = settings.value(gain_key, 1.0) * clip_settings.value(gain_key, 1.0);
                const auto offset = ticks_samples(normalized_ticks(m.start_offset)), duration = ticks_samples(normalized_ticks(m.duration));
                const auto begin = std::max<int64_t>({0, first, offset - source_first}), end = std::min<int64_t>({first + count, clip_samples, offset + duration - source_first});
                if (end <= begin || gain == 0) continue;
                if (!readers.contains(m.id)) { if (readers.size() >= 4) readers.clear(); readers[m.id] = std::make_unique<AudioReader>(safe_path(root, m.path)); }
                auto& reader = *readers.at(m.id); reader.read(source_first + begin - offset, end - begin);
                for (auto i = begin; i < end; ++i) for (int c = 0; c < channels; ++c) mixed[(i - first) * channels + c] += static_cast<float>(gain) * reader.at(source_first + i - offset, c);
            }
            return mixed;
        };
        wsola(fetch, static_cast<double>(clip.rate.numerator) / clip.rate.denominator, output_count, [&](const float* samples, size_t count) {
            check_cancel();
            const int64_t chunk_start = clip_out_cursor;
            const int64_t chunk_end = chunk_start + static_cast<int64_t>(count);
            const bool touches_in = seam_in && fade_len > 0 && chunk_start < fade_len;
            const bool touches_out = seam_out && fade_len > 0 && chunk_end > clip_out_total - fade_len;
            if (touches_in || touches_out) {
                std::vector<float> faded(samples, samples + count * channels);
                for (size_t i = 0; i < count; ++i) {
                    const int64_t pos = chunk_start + static_cast<int64_t>(i);
                    float gain = 1.0f;
                    if (seam_in && pos < fade_len)
                        gain *= static_cast<float>(0.5 * (1.0 - std::cos(pi * (static_cast<double>(pos) + 0.5) / static_cast<double>(fade_len))));
                    if (seam_out && pos >= clip_out_total - fade_len) {
                        const int64_t rem = clip_out_total - 1 - pos;
                        gain *= static_cast<float>(0.5 * (1.0 - std::cos(pi * (static_cast<double>(rem) + 0.5) / static_cast<double>(fade_len))));
                    }
                    if (gain != 1.0f)
                        for (int c = 0; c < channels; ++c) faded[i * channels + c] *= gain;
                }
                emit(faded.data(), count);
            } else {
                emit(samples, count);
            }
            clip_out_cursor = chunk_end;
            done += static_cast<int64_t>(count);
            if (report_status) progress(static_cast<uint64_t>(done), static_cast<uint64_t>(frames));
        }, begin - clip_first);
    }
    if (report_status) stage = 0;
}
void Session::mix_audio(const std::filesystem::path& target) {
    const auto frames = ticks_samples(evaluator.timeline().output_duration());
    std::ofstream file(target, std::ios::binary | std::ios::trunc);
    if (!file) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED));
    write_pcm_header(file, static_cast<uint64_t>(frames));
    mix_pcm(0, frames, [&](const float* samples, size_t count) {
        std::vector<int16_t> pcm(count * channels);
        for (size_t i = 0; i < pcm.size(); ++i) pcm[i] = static_cast<int16_t>(std::lround(std::clamp(samples[i], -1.0f, 1.0f) * 32767));
        file.write(reinterpret_cast<const char*>(pcm.data()), static_cast<std::streamsize>(pcm.size() * sizeof(int16_t)));
        if (!file) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_DISK_FULL));
    });
    file.flush(); if (!file) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_WRITE_FAULT));
}
void Session::prepare_peaks(const std::filesystem::path& target) {
    const auto frames = ticks_samples(evaluator.timeline().output_duration());
    const auto buckets = std::clamp<int64_t>(frames / 2400, 1, 24000);
    std::vector<float> peaks(static_cast<size_t>(buckets)); int64_t at = 0;
    mix_pcm(0, frames, [&](const float* samples, size_t count) {
        for (size_t i = 0; i < count; ++i) {
            const auto bucket = std::min<int64_t>((at + static_cast<int64_t>(i)) * buckets / std::max<int64_t>(1, frames), buckets - 1);
            peaks[static_cast<size_t>(bucket)] = std::max(peaks[static_cast<size_t>(bucket)], std::min(1.0f, std::max(std::abs(samples[i*2]), std::abs(samples[i*2+1]))));
        }
        at += static_cast<int64_t>(count);
    });
    std::ofstream out(target); out << Json(peaks); if (!out) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_DISK_FULL));
}
void Session::stream_audio(int64_t ticks) {
    stop_audio();
    // Freeze the authored project: live picture edits never race the mixer.
    Project frozen; { std::scoped_lock lock(mutex); frozen = project; }
    std::scoped_lock lock(audio_mutex); audio_stop = false; audio_ticks = ticks; audio_running = 1;
    try { audio_thread = std::thread([this, frozen = std::move(frozen), ticks]() mutable {
        HWAVEOUT device{};
        struct Block { WAVEHDR header{}; std::array<int16_t, 4096> pcm{}; bool prepared{}; };
        std::array<Block, 3> blocks{}; size_t slot = 0;
        try {
            Apartment apartment; Session mix(std::move(frozen), root);
            WAVEFORMATEX format{WAVE_FORMAT_PCM, channels, sample_rate, sample_rate*channels*2, channels*2, 16, 0};
            if (waveOutOpen(&device, WAVE_MAPPER, &format, 0, 0, CALLBACK_NULL) != MMSYSERR_NOERROR) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED));
            auto clock = [&] { MMTIME p{}; p.wType=TIME_SAMPLES; if (waveOutGetPosition(device,&p,sizeof(p))==MMSYSERR_NOERROR && p.wType==TIME_SAMPLES) audio_ticks=ticks+sample_ticks(p.u.sample); };
            auto wait = [&](Block& block) { while (block.prepared && !(block.header.dwFlags & WHDR_DONE)) { if(audio_stop) winrt::throw_hresult(cancelled_hr); clock(); Sleep(2); } if(audio_stop) winrt::throw_hresult(cancelled_hr); if(block.prepared) { waveOutUnprepareHeader(device,&block.header,sizeof(WAVEHDR));block.prepared=false; } };
            const auto first=ticks_samples(ticks), total=ticks_samples(mix.evaluator.timeline().output_duration())-first;
            mix.mix_pcm(first,total,[&](const float* data,size_t count) {
                size_t offset=0;
                while(offset<count) {
                    auto& b=blocks[slot++%blocks.size()];wait(b); const auto n=std::min<size_t>(count-offset,b.pcm.size()/channels);
                    for(size_t i=0;i<n*channels;++i)b.pcm[i]=static_cast<int16_t>(std::lround(std::clamp(data[offset*channels+i],-1.0f,1.0f)*32767));
                    b.header={};b.header.lpData=reinterpret_cast<char*>(b.pcm.data());b.header.dwBufferLength=static_cast<DWORD>(n*channels*2);
                    if(waveOutPrepareHeader(device,&b.header,sizeof(WAVEHDR))!=MMSYSERR_NOERROR)winrt::throw_hresult(E_FAIL);b.prepared=true;
                    if(waveOutWrite(device,&b.header,sizeof(WAVEHDR))!=MMSYSERR_NOERROR)winrt::throw_hresult(E_FAIL);
                    offset+=n;clock();
                }
            });
            for(auto& b:blocks)wait(b); audio_ticks=output_duration.load();
        } catch(const winrt::hresult_error& e) { if(e.code()!=cancelled_hr) audio_running=3; }
          catch(...) { audio_running=3; }
        if(device) { waveOutReset(device);for(auto& b:blocks)if(b.prepared)waveOutUnprepareHeader(device,&b.header,sizeof(WAVEHDR));waveOutClose(device); }
        if(audio_running!=3)audio_running=0;
    }); } catch(...) { audio_running=0;throw; }
}
void Session::stop_audio() { std::scoped_lock lock(audio_mutex); audio_stop = true; if (audio_thread.joinable()) audio_thread.join(); }
Session::~Session() { stop_audio(); }

void extract_audio_pcm16k(
    const std::filesystem::path& source_path,
    const std::filesystem::path& output_wav_path,
    int64_t source_start_ticks,
    int64_t source_duration_ticks) {
    if (source_start_ticks < 0) winrt::throw_hresult(E_INVALIDARG);

    AudioReader audio(source_path);
    const int64_t start_48k = ticks_samples(source_start_ticks);
    const bool has_end = source_duration_ticks > 0;
    const int64_t end_48k = has_end
        ? ticks_samples(source_start_ticks + source_duration_ticks)
        : (std::numeric_limits<int64_t>::max)();

    if (source_start_ticks > 0) {
        const int64_t seek_ticks = std::max<int64_t>(0, source_start_ticks - time_scale / 4);
        PROPVARIANT position{};
        FT_MEDIA_CHECK(InitPropVariantFromInt64(seek_ticks, &position));
        const HRESULT hr = audio.reader->SetCurrentPosition(GUID_NULL, position);
        PropVariantClear(&position);
        FT_MEDIA_CHECK(hr);
    }

    auto write_header_16k = [](std::ostream& out, uint32_t frames_16k) {
        const uint32_t data_bytes = frames_16k * 2u;
        const uint32_t riff_size = data_bytes + 36u;
        auto u16 = [&](uint16_t v) { out.write(reinterpret_cast<const char*>(&v), 2); };
        auto u32 = [&](uint32_t v) { out.write(reinterpret_cast<const char*>(&v), 4); };
        out.write("RIFF", 4); u32(riff_size); out.write("WAVE", 4);
        out.write("fmt ", 4); u32(16u);
        u16(1u);       // WAVE_FORMAT_PCM
        u16(1u);       // 1 channel (mono)
        u32(16000u);   // 16 kHz sample rate
        u32(32000u);   // byte rate = 16000 * 1 * 2
        u16(2u);       // block align = 1 * 2
        u16(16u);      // 16 bits per sample
        out.write("data", 4); u32(data_bytes);
    };

    std::ofstream out(output_wav_path, std::ios::binary | std::ios::trunc);
    if (!out) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED));
    write_header_16k(out, 0u);
    if (!out) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_DISK_FULL));

    // 15-tap symmetric Hamming-windowed sinc low-pass filter (fc = 0.145 * 48kHz = 6.96kHz)
    // for alias-free exact 3:1 decimation (48kHz -> 16kHz).
    constexpr int taps = 15;
    constexpr int half = taps / 2;
    std::array<float, taps> kernel{};
    {
        constexpr double fc = 0.145;
        constexpr double pi = 3.14159265358979323846;
        double sum = 0.0;
        for (int i = 0; i < taps; ++i) {
            const int k = i - half;
            const double sinc = (k == 0) ? (2.0 * fc) : (std::sin(2.0 * pi * fc * k) / (pi * k));
            const double win = 0.54 - 0.46 * std::cos(2.0 * pi * i / (taps - 1));
            kernel[static_cast<size_t>(i)] = static_cast<float>(sinc * win);
            sum += sinc * win;
        }
        for (float& c : kernel) c = static_cast<float>(c / sum);
    }

    constexpr uint64_t max_16k_frames = (UINT32_MAX - 36u) / 2u;
    std::vector<float> mono_buf(static_cast<size_t>(half), 0.0f);
    std::vector<int16_t> pcm_out;
    pcm_out.reserve(4096);
    size_t read_pos = 0;
    uint64_t written_16k = 0;

    auto drain_decimator = [&]() {
        while (read_pos + static_cast<size_t>(taps) <= mono_buf.size()) {
            if (written_16k >= max_16k_frames) break;
            float acc = 0.0f;
            for (int i = 0; i < taps; ++i) {
                acc += mono_buf[read_pos + static_cast<size_t>(i)] * kernel[static_cast<size_t>(i)];
            }
            pcm_out.push_back(static_cast<int16_t>(std::lround(std::clamp(acc, -1.0f, 1.0f) * 32767.0f)));
            ++written_16k;
            read_pos += 3;
            if (pcm_out.size() >= 4096) {
                out.write(reinterpret_cast<const char*>(pcm_out.data()), static_cast<std::streamsize>(pcm_out.size() * sizeof(int16_t)));
                if (!out) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_DISK_FULL));
                pcm_out.clear();
            }
        }
        if (read_pos > 16384) {
            mono_buf.erase(mono_buf.begin(), mono_buf.begin() + static_cast<std::ptrdiff_t>(read_pos));
            read_pos = 0;
        }
    };

    int64_t cursor_48k = start_48k;
    bool started = false;
    while (cursor_48k < end_48k && written_16k < max_16k_frames) {
        DWORD flags{};
        LONGLONG sample_time{};
        ComPtr<IMFSample> sample;
        FT_MEDIA_CHECK(audio.reader->ReadSample(audio_stream_id, 0, nullptr, &flags, &sample_time, &sample));
        if (flags & MF_SOURCE_READERF_ERROR) winrt::throw_hresult(MF_E_INVALID_FILE_FORMAT);
        if (sample) {
            ComPtr<IMFMediaBuffer> buffer;
            FT_MEDIA_CHECK(sample->ConvertToContiguousBuffer(&buffer));
            BYTE* bytes{};
            DWORD length{};
            FT_MEDIA_CHECK(buffer->Lock(&bytes, nullptr, &length));
            const auto* floats = reinterpret_cast<const float*>(bytes);
            const int64_t frame_count = static_cast<int64_t>(length / (channels * sizeof(float)));
            int64_t sample_offset = ticks_samples(sample_time);

            // Treat tiny timestamp rounding jitter as contiguous to prevent micro-gaps.
            if (started && std::abs(sample_offset - cursor_48k) <= 16) {
                sample_offset = cursor_48k;
            }

            if (sample_offset + frame_count > start_48k) {
                if (sample_offset > cursor_48k) {
                    const int64_t gap_end = std::min(sample_offset, end_48k);
                    if (gap_end > cursor_48k) {
                        mono_buf.insert(mono_buf.end(), static_cast<size_t>(gap_end - cursor_48k), 0.0f);
                        cursor_48k = gap_end;
                        started = true;
                        drain_decimator();
                    }
                }
                const int64_t slice_begin = std::max(sample_offset, cursor_48k);
                const int64_t slice_end = std::min(sample_offset + frame_count, end_48k);
                if (slice_end > slice_begin) {
                    mono_buf.reserve(mono_buf.size() + static_cast<size_t>(slice_end - slice_begin));
                    for (int64_t s = slice_begin; s < slice_end; ++s) {
                        const size_t idx = static_cast<size_t>(s - sample_offset) * channels;
                        mono_buf.push_back(0.5f * (floats[idx] + floats[idx + 1]));
                    }
                    cursor_48k = slice_end;
                    started = true;
                    drain_decimator();
                }
            }
            buffer->Unlock();
            if (has_end && cursor_48k >= end_48k) break;
        }
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
    }

    // Flush filter tail so exact sample count equals total_48k / 3.
    const int64_t total_48k = cursor_48k - start_48k;
    if (total_48k > 0) {
        mono_buf.insert(mono_buf.end(), static_cast<size_t>(half), 0.0f);
        drain_decimator();
    }
    if (!pcm_out.empty()) {
        out.write(reinterpret_cast<const char*>(pcm_out.data()), static_cast<std::streamsize>(pcm_out.size() * sizeof(int16_t)));
        if (!out) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_DISK_FULL));
    }

    out.seekp(0, std::ios::beg);
    write_header_16k(out, static_cast<uint32_t>(written_16k));
    out.flush();
    if (!out) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_WRITE_FAULT));
}
}
