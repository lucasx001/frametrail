#include "internal.hpp"
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
void wsola(const Fetch& fetch, double rate, int64_t output_frames, const Emit& emit) {
    if (rate == 1) {
        for (int64_t p = 0; p < output_frames; p += 4096) { auto n = static_cast<int>(std::min<int64_t>(4096, output_frames - p)); auto data = fetch(p, n); emit(data.data(), n); } return;
    }
    std::vector<float> sum(grain * channels), weight(grain), previous(hop * channels);
    bool seeded = false; int64_t written = 0;
    for (int64_t p = 0; p < output_frames; p += hop) {
        const int64_t nominal = static_cast<int64_t>(std::llround(p * rate)); const auto begin = std::max<int64_t>(0, nominal - search);
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
void wave_header(std::ofstream& file, uint32_t bytes) {
    const uint32_t size = bytes + 36, fmt = 16, sr = sample_rate, rate = sr * channels * 2;
    const uint16_t pcm = 1, ch = channels, block = channels * 2, bits = 16;
    file.write("RIFF", 4); file.write(reinterpret_cast<const char*>(&size), 4); file.write("WAVEfmt ", 8); file.write(reinterpret_cast<const char*>(&fmt), 4);
    file.write(reinterpret_cast<const char*>(&pcm), 2); file.write(reinterpret_cast<const char*>(&ch), 2); file.write(reinterpret_cast<const char*>(&sr), 4); file.write(reinterpret_cast<const char*>(&rate), 4);
    file.write(reinterpret_cast<const char*>(&block), 2); file.write(reinterpret_cast<const char*>(&bits), 2); file.write("data", 4); file.write(reinterpret_cast<const char*>(&bytes), 4);
}
}
std::vector<float> stretch_wsola(const std::vector<float>& input, double rate, size_t output_frames) {
    std::vector<float> result; result.reserve(output_frames * channels);
    wsola([&](int64_t first, int frames) { std::vector<float> v(static_cast<size_t>(frames) * channels); for (int i = 0; i < frames; ++i) for (int c = 0; c < channels; ++c) if (first + i >= 0 && static_cast<size_t>(first + i) * channels + c < input.size()) v[i * channels + c] = input[(first + i) * channels + c]; return v; }, rate, static_cast<int64_t>(output_frames), [&](const float* p, size_t frames) { result.insert(result.end(), p, p + frames * channels); }); return result;
}
void Session::mix_audio(const std::filesystem::path& target) {
    const auto& timeline = evaluator.timeline(); const auto frames = ticks_samples(timeline.output_duration());
    if (frames * channels * 2 > UINT32_MAX - 36) throw ValidationError("audio", "PCM cache exceeds RIFF 4 GiB limit; shorten the project before exporting");
    std::ofstream file(target, std::ios::binary | std::ios::trunc); if (!file) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED));
    wave_header(file, static_cast<uint32_t>(frames * channels * 2));
    std::unordered_map<std::string, std::unique_ptr<AudioReader>> readers; const auto settings = Json::parse(project.editor_json);
    stage = 1; progress(0, static_cast<uint64_t>(frames)); int64_t done = 0;
    for (const auto& clip : timeline.segments()) {
        check_cancel(); const auto clip_samples = ticks_samples(clip.source_duration), source_first = ticks_samples(clip.source_start);
        const auto output_count = ticks_samples(clip.output_end()) - ticks_samples(clip.output_start);
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
            check_cancel(); std::vector<int16_t> pcm(count * channels);
            for (size_t i = 0; i < pcm.size(); ++i) pcm[i] = static_cast<int16_t>(std::lround(std::clamp(samples[i], -1.0f, 1.0f) * 32767));
            file.write(reinterpret_cast<const char*>(pcm.data()), static_cast<std::streamsize>(pcm.size() * sizeof(int16_t))); if (!file) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_DISK_FULL));
            done += static_cast<int64_t>(count); progress(static_cast<uint64_t>(done), static_cast<uint64_t>(frames));
        });
    }
    file.flush(); if (!file) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_WRITE_FAULT)); stage = 0;
}
void Session::stop_audio() { std::scoped_lock lock(audio_mutex); audio_stop = true; if (audio_thread.joinable()) audio_thread.join(); }
Session::~Session() { stop_audio(); }
}
