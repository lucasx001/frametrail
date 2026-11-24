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
constexpr double pi = 3.14159265358979323846;
using Fetch = std::function<std::vector<float>(int64_t, int)>;
using Emit = std::function<void(const float*, size_t)>;
using DualEmit = std::function<void(const float*, const float*, size_t)>;

enum class TrackGroup { system, microphone };

// 2nd-order Butterworth high-pass biquad filter (85 Hz at 48 kHz) to remove rumble/hum.
struct HighPassBiquad {
    double b0{}, b1{}, b2{}, a1{}, a2{};
    std::array<double, channels> x1{}, x2{}, y1{}, y2{};

    explicit HighPassBiquad(double cutoff_hz = 85.0, double fs = static_cast<double>(sample_rate)) {
        const double w0 = 2.0 * pi * cutoff_hz / fs;
        const double cos_w0 = std::cos(w0);
        const double sin_w0 = std::sin(w0);
        const double alpha = sin_w0 / (2.0 * 0.7071067811865475);
        const double a0 = 1.0 + alpha;
        b0 = ((1.0 + cos_w0) * 0.5) / a0;
        b1 = -(1.0 + cos_w0) / a0;
        b2 = ((1.0 + cos_w0) * 0.5) / a0;
        a1 = (-2.0 * cos_w0) / a0;
        a2 = (1.0 - alpha) / a0;
    }

    float process_sample(int ch, float in) {
        const double x = static_cast<double>(in);
        const double y = b0 * x + b1 * x1[ch] + b2 * x2[ch] - a1 * y1[ch] - a2 * y2[ch];
        x2[ch] = x1[ch];
        x1[ch] = x;
        y2[ch] = y1[ch];
        y1[ch] = y;
        return static_cast<float>(y);
    }
};

// Smooth soft-knee RMS expander / noise gate (-42 dBFS threshold, 3:1 below threshold, 0.18 floor, 5ms/60ms).
struct MicNoiseGate {
    double env_sq{};
    double alpha_atk{};
    double alpha_rel{};
    static constexpr double thresh_db = -42.0;
    static constexpr double knee_db = 6.0;
    static constexpr double ratio = 3.0;
    static constexpr double floor_gain = 0.18;

    explicit MicNoiseGate(double fs = static_cast<double>(sample_rate)) {
        alpha_atk = 1.0 - std::exp(-1.0 / (0.005 * fs));
        alpha_rel = 1.0 - std::exp(-1.0 / (0.060 * fs));
    }

    float compute_gain(float l, float r) {
        const double sq = 0.5 * (static_cast<double>(l) * l + static_cast<double>(r) * r);
        const double alpha = sq > env_sq ? alpha_atk : alpha_rel;
        env_sq += alpha * (sq - env_sq);
        const double rms = std::sqrt(std::max(0.0, env_sq));
        const double level_db = 20.0 * std::log10(std::max(rms, 1e-9));
        const double diff = thresh_db - level_db;
        double gain_db = 0.0;
        if (diff >= knee_db * 0.5) {
            gain_db = -(ratio - 1.0) * diff;
        } else if (diff > -knee_db * 0.5) {
            const double x = diff + knee_db * 0.5;
            gain_db = -(ratio - 1.0) * (x * x) / (2.0 * knee_db);
        }
        return static_cast<float>(std::max(floor_gain, std::pow(10.0, gain_db / 20.0)));
    }
};

// Short-term microphone speech envelope ducker (15ms attack, 280ms release, -35 dBFS threshold -> 0.28x).
struct VoiceDucker {
    double env_sq{};
    double duck_gain{1.0};
    double alpha_atk{};
    double alpha_rel{};
    double alpha_smooth_down{};
    double alpha_smooth_up{};
    static constexpr double threshold = 0.017782794; // -35 dBFS (~0.018)
    static constexpr double duck_floor = 0.28;

    explicit VoiceDucker(double fs = static_cast<double>(sample_rate)) {
        alpha_atk = 1.0 - std::exp(-1.0 / (0.015 * fs));
        alpha_rel = 1.0 - std::exp(-1.0 / (0.280 * fs));
        alpha_smooth_down = 1.0 - std::exp(-1.0 / (0.010 * fs));
        alpha_smooth_up = 1.0 - std::exp(-1.0 / (0.040 * fs));
    }

    float next_gain(float mic_l, float mic_r) {
        const double sq = 0.5 * (static_cast<double>(mic_l) * mic_l + static_cast<double>(mic_r) * mic_r);
        const double alpha = sq > env_sq ? alpha_atk : alpha_rel;
        env_sq += alpha * (sq - env_sq);
        const double env = std::sqrt(std::max(0.0, env_sq));
        const double target = env > threshold ? duck_floor : 1.0;
        const double smooth = target < duck_gain ? alpha_smooth_down : alpha_smooth_up;
        duck_gain += smooth * (target - duck_gain);
        return static_cast<float>(duck_gain);
    }
};

// Adaptive RMS loudness normalizer (-16 dBFS target, max 2.5x makeup, 10ms/150ms) + -1.0 dBFS soft-knee limiter.
struct LoudnessNormalizer {
    double env_sq{};
    double current_gain{1.0};
    double alpha_rms{};
    double alpha_atk{};
    double alpha_rel{};
    static constexpr double target_rms = 0.158489319; // -16 dBFS
    static constexpr double max_makeup = 2.5;
    static constexpr float limit_peak = 0.891250938f; // -1.0 dBFS
    static constexpr float knee_start = 0.70f * limit_peak;
    static constexpr float headroom = limit_peak - knee_start;

    explicit LoudnessNormalizer(double fs = static_cast<double>(sample_rate)) {
        alpha_rms = 1.0 - std::exp(-1.0 / (0.100 * fs));
        alpha_atk = 1.0 - std::exp(-1.0 / (0.010 * fs));
        alpha_rel = 1.0 - std::exp(-1.0 / (0.150 * fs));
    }

    static float soft_limit(float x) {
        const float ax = std::abs(x);
        if (ax <= knee_start) return x;
        const float y = knee_start + headroom * std::tanh((ax - knee_start) / headroom);
        return x < 0.0f ? -y : y;
    }

    void process(float* samples, size_t count) {
        for (size_t i = 0; i < count; ++i) {
            const double l = samples[i * channels];
            const double r = samples[i * channels + 1];
            const double sq = 0.5 * (l * l + r * r);
            env_sq += alpha_rms * (sq - env_sq);
            const double rms = std::sqrt(std::max(0.0, env_sq));
            double desired = 1.0;
            if (rms > 1e-4) {
                const double raw = std::clamp(target_rms / rms, 0.1, max_makeup);
                const double blend = std::clamp((rms - 1e-4) / 9e-4, 0.0, 1.0);
                desired = 1.0 + blend * (raw - 1.0);
            }
            const double gain_alpha = desired < current_gain ? alpha_atk : alpha_rel;
            current_gain += gain_alpha * (desired - current_gain);
            const float g = static_cast<float>(current_gain);
            samples[i * channels] = soft_limit(samples[i * channels] * g);
            samples[i * channels + 1] = soft_limit(samples[i * channels + 1] * g);
        }
    }
};

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
            const auto window = static_cast<float>(0.5 - 0.5 * std::cos(2 * pi * (k + 0.5) / grain));
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

// Dual-track lockstep WSOLA keeping system and microphone tracks phase-aligned for downstream DSP.
void wsola_dual(
    const Fetch& fetch_sys, bool has_sys,
    const Fetch& fetch_mic, bool has_mic,
    double rate, int64_t output_frames, const DualEmit& emit, int64_t output_first = 0) {
    if (rate == 1) {
        std::vector<float> zeros;
        for (int64_t p = 0; p < output_frames; p += 4096) {
            const auto n = static_cast<int>(std::min<int64_t>(4096, output_frames - p));
            std::vector<float> sys_buf = has_sys ? fetch_sys(output_first + p, n) : std::vector<float>{};
            std::vector<float> mic_buf = has_mic ? fetch_mic(output_first + p, n) : std::vector<float>{};
            if (!has_sys || !has_mic) zeros.assign(static_cast<size_t>(n) * channels, 0.0f);
            emit(has_sys ? sys_buf.data() : zeros.data(), has_mic ? mic_buf.data() : zeros.data(), static_cast<size_t>(n));
        }
        return;
    }
    const int block_frames = grain + search * 2;
    std::vector<float> sum_sys(has_sys ? static_cast<size_t>(grain) * channels : 0);
    std::vector<float> sum_mic(has_mic ? static_cast<size_t>(grain) * channels : 0);
    std::vector<float> weight(grain), previous(hop * channels);
    bool seeded = false; int64_t written = 0;
    for (int64_t p = 0; p < output_frames; p += hop) {
        const int64_t nominal = static_cast<int64_t>(std::llround((output_first + p) * rate));
        const auto begin = std::max<int64_t>(0, nominal - search);
        std::vector<float> sys_block = has_sys ? fetch_sys(begin, block_frames) : std::vector<float>{};
        std::vector<float> mic_block = has_mic ? fetch_mic(begin, block_frames) : std::vector<float>{};
        auto combined_at = [&](int idx) -> float {
            return (has_sys ? sys_block[static_cast<size_t>(idx)] : 0.0f) + (has_mic ? mic_block[static_cast<size_t>(idx)] : 0.0f);
        };
        int best = static_cast<int>(nominal - begin);
        if (seeded) {
            double best_score = -2;
            for (int delta = 0; delta <= search * 2; delta += 4) {
                double dot = 0, aa = 1e-12, bb = 1e-12;
                for (int k = 0; k < hop * channels; k += 8) {
                    const auto a = previous[k], b = combined_at(delta * channels + k);
                    dot += a * b; aa += a * a; bb += b * b;
                }
                const auto score = dot / std::sqrt(aa * bb) - std::abs(delta - static_cast<int>(nominal - begin)) * 0.00001;
                if (score > best_score) { best_score = score; best = delta; }
            }
        }
        for (int k = 0; k < grain; ++k) {
            const auto window = static_cast<float>(0.5 - 0.5 * std::cos(2 * pi * (k + 0.5) / grain));
            weight[k] += window;
            for (int c = 0; c < channels; ++c) {
                const size_t src_idx = static_cast<size_t>(best + k) * channels + c;
                const size_t dst_idx = static_cast<size_t>(k) * channels + c;
                if (has_sys) sum_sys[dst_idx] += sys_block[src_idx] * window;
                if (has_mic) sum_mic[dst_idx] += mic_block[src_idx] * window;
            }
        }
        for (int k = 0; k < hop * channels; ++k) previous[k] = combined_at((best + hop) * channels + k);
        seeded = true;
        const auto count = static_cast<size_t>(std::min<int64_t>(hop, output_frames - written));
        std::vector<float> ready_sys(count * channels, 0.0f), ready_mic(count * channels, 0.0f);
        for (size_t i = 0; i < count; ++i) {
            const float inv_w = 1.0f / std::max(1e-6f, weight[i]);
            for (int c = 0; c < channels; ++c) {
                const size_t idx = i * channels + c;
                if (has_sys) ready_sys[idx] = sum_sys[idx] * inv_w;
                if (has_mic) ready_mic[idx] = sum_mic[idx] * inv_w;
            }
        }
        emit(ready_sys.data(), ready_mic.data(), count);
        written += count;
        if (has_sys) {
            std::move(sum_sys.begin() + hop * channels, sum_sys.end(), sum_sys.begin());
            std::fill(sum_sys.begin() + hop * channels, sum_sys.end(), 0.0f);
        }
        if (has_mic) {
            std::move(sum_mic.begin() + hop * channels, sum_mic.end(), sum_mic.begin());
            std::fill(sum_mic.begin() + hop * channels, sum_mic.end(), 0.0f);
        }
        std::move(weight.begin() + hop, weight.end(), weight.begin());
        std::fill(weight.begin() + hop, weight.end(), 0.0f);
    }
}

std::vector<int64_t> resolve_transition_seam_half_samples(const Project& project, const std::vector<ResolvedSegment>& segments) {
    std::vector<int64_t> half_samples(segments.size() > 1 ? segments.size() - 1 : 0, 0);
    if (segments.size() < 2) return half_samples;
    for (const auto& raw_effect : project.effects) {
        if (raw_effect.kind != "transition") continue;
        const auto e = parse_visual_effect(raw_effect);
        size_t seam_index = segments.size();
        if (!e.from_segment_id.empty() && !e.to_segment_id.empty()) {
            for (size_t i = 0; i + 1 < segments.size(); ++i) {
                if (segments[i].id == e.from_segment_id && segments[i + 1].id == e.to_segment_id) {
                    seam_index = i;
                    break;
                }
            }
        } else {
            const auto e_end = e.start + e.duration;
            for (size_t i = 0; i + 1 < segments.size(); ++i) {
                if ((segments[i + 1].source_start >= e.start && segments[i + 1].source_start <= e_end) ||
                    (segments[i].source_end() >= e.start && segments[i].source_end() <= e_end)) {
                    seam_index = i;
                    break;
                }
            }
        }
        if (seam_index + 1 >= segments.size() || half_samples[seam_index] > 0) continue;
        const auto& left = segments[seam_index];
        const auto& right = segments[seam_index + 1];
        const auto desired = static_cast<int64_t>(std::llround(e.transition_duration * time_scale));
        const auto half_ticks = std::min({desired / 2, left.output_duration / 2, right.output_duration / 2});
        if (half_ticks <= 0) continue;
        const int64_t left_out_total = ticks_samples(left.output_end()) - ticks_samples(left.output_start);
        const int64_t right_out_total = ticks_samples(right.output_end()) - ticks_samples(right.output_start);
        const int64_t clamped_half = std::min({ticks_samples(half_ticks), left_out_total / 2, right_out_total / 2});
        if (clamped_half > 0) half_samples[seam_index] = clamped_half;
    }
    return half_samples;
}
}
std::vector<float> stretch_wsola(const std::vector<float>& input, double rate, size_t output_frames) {
    std::vector<float> result; result.reserve(output_frames * channels);
    wsola([&](int64_t first, int frames) { std::vector<float> v(static_cast<size_t>(frames) * channels); for (int i = 0; i < frames; ++i) for (int c = 0; c < channels; ++c) if (first + i >= 0 && static_cast<size_t>(first + i) * channels + c < input.size()) v[i * channels + c] = input[(first + i) * channels + c]; return v; }, rate, static_cast<int64_t>(output_frames), [&](const float* p, size_t frames) { result.insert(result.end(), p, p + frames * channels); }); return result;
}
void Session::mix_pcm(int64_t first, int64_t frames, const Emit& emit, bool report_status) {
    const auto& timeline = evaluator.timeline();
    std::unordered_map<std::string, std::unique_ptr<AudioReader>> readers;
    std::unordered_map<std::string, uint64_t> reader_lru;
    uint64_t reader_clock = 0;
    const auto settings = Json::parse(project.editor_json);
    const bool mic_denoise = settings.contains("micNoiseReduction") && settings["micNoiseReduction"].is_boolean()
        ? settings["micNoiseReduction"].get<bool>() : false;
    const bool audio_normalize = settings.contains("audioNormalize") && settings["audioNormalize"].is_boolean()
        ? settings["audioNormalize"].get<bool>() : false;
    const bool audio_ducking = settings.contains("audioDucking") && settings["audioDucking"].is_boolean()
        ? settings["audioDucking"].get<bool>() : true;
    std::string bgm_path;
    if (settings.contains("bgmPath") && settings["bgmPath"].is_string()) {
        bgm_path = settings["bgmPath"].get<std::string>();
    }
    const double bgm_gain = std::clamp(
        (settings.contains("bgmGain") && settings["bgmGain"].is_number()) ? settings["bgmGain"].get<double>() : 0.35,
        0.0, 2.0);

    std::unique_ptr<AudioReader> bgm_reader;
    int64_t bgm_total_samples = 0;
    if (!bgm_path.empty() && bgm_gain > 0.0) {
        bgm_reader = std::make_unique<AudioReader>(safe_path(root, bgm_path));
        PROPVARIANT dur{};
        if (SUCCEEDED(bgm_reader->reader->GetPresentationAttribute(media_source_id, MF_PD_DURATION, &dur)) && dur.vt == VT_UI8) {
            bgm_total_samples = ticks_samples(static_cast<int64_t>(dur.uhVal.QuadPart));
        }
        PropVariantClear(&dur);
    }

    HighPassBiquad mic_hpf;
    MicNoiseGate mic_gate;
    VoiceDucker ducker;
    LoudnessNormalizer normalizer;

    if (report_status) { stage = 1; progress(0, static_cast<uint64_t>(frames)); }
    int64_t done = 0;
    const auto& segments = timeline.segments();
    const auto seam_half_samples = resolve_transition_seam_half_samples(project, segments);
    auto get_reader = [&](const Media& m) -> AudioReader& {
        if (!readers.contains(m.id)) {
            while (readers.size() >= 8 && !reader_lru.empty()) {
                auto victim = std::min_element(reader_lru.begin(), reader_lru.end(),
                    [](const auto& a, const auto& b) { return a.second < b.second; });
                readers.erase(victim->first);
                reader_lru.erase(victim);
            }
            if (readers.size() >= 8) readers.clear();
            readers[m.id] = std::make_unique<AudioReader>(safe_path(root, m.path));
        }
        reader_lru[m.id] = ++reader_clock;
        return *readers.at(m.id);
    };
    auto clip_settings_for = [&](const std::string& id) {
        if (settings.contains("clipAudio") && settings["clipAudio"].contains(id))
            return settings["clipAudio"][id];
        return Json::object();
    };
    auto is_track_active_for_segment = [&](const ResolvedSegment& seg, TrackGroup group) -> bool {
        const Json seg_settings = clip_settings_for(seg.id);
        const int64_t seg_start = ticks_samples(seg.source_start);
        const int64_t seg_end = ticks_samples(seg.source_end());
        for (const auto& m : project.media) {
            if (m.kind != MediaKind::system_audio && m.kind != MediaKind::microphone && m.kind != MediaKind::android_audio) continue;
            const bool system = m.kind == MediaKind::system_audio || m.kind == MediaKind::android_audio;
            if ((group == TrackGroup::system) != system) continue;
            const char* gain_key = system ? "systemGain" : "microphoneGain";
            const char* mute_key = system ? "systemMuted" : "microphoneMuted";
            if (settings.value(mute_key, false) || seg_settings.value(mute_key, false)) continue;
            if (settings.value(gain_key, 1.0) * seg_settings.value(gain_key, 1.0) == 0.0) continue;
            const int64_t offset = ticks_samples(normalized_ticks(m.start_offset));
            const int64_t duration = ticks_samples(normalized_ticks(m.duration));
            if (offset + duration > seg_start && offset < seg_end) return true;
        }
        return false;
    };
    auto mix_source_range = [&](const ResolvedSegment& seg, int64_t src_first, int count, TrackGroup group) {
        check_cancel();
        std::vector<float> mixed(static_cast<size_t>(count) * channels, 0.0f);
        const Json seg_settings = clip_settings_for(seg.id);
        for (const auto& m : project.media) {
            if (m.kind != MediaKind::system_audio && m.kind != MediaKind::microphone && m.kind != MediaKind::android_audio) continue;
            const bool system = m.kind == MediaKind::system_audio || m.kind == MediaKind::android_audio;
            if ((group == TrackGroup::system) != system) continue;
            const char* gain_key = system ? "systemGain" : "microphoneGain";
            const char* mute_key = system ? "systemMuted" : "microphoneMuted";
            if (settings.value(mute_key, false) || seg_settings.value(mute_key, false)) continue;
            const auto gain = settings.value(gain_key, 1.0) * seg_settings.value(gain_key, 1.0);
            if (gain == 0) continue;
            const auto offset = ticks_samples(normalized_ticks(m.start_offset));
            const auto duration = ticks_samples(normalized_ticks(m.duration));
            const auto m_begin = std::max<int64_t>(src_first, offset);
            const auto m_end = std::min<int64_t>(src_first + count, offset + duration);
            if (m_end <= m_begin) continue;
            auto& reader = get_reader(m);
            reader.read(m_begin - offset, m_end - m_begin);
            for (int64_t s = m_begin; s < m_end; ++s) {
                for (int c = 0; c < channels; ++c) {
                    mixed[static_cast<size_t>(s - src_first) * channels + c] += static_cast<float>(gain) * reader.at(s - offset, c);
                }
            }
        }
        return mixed;
    };
    auto fill_bgm = [&](int64_t out_pos, size_t count, std::vector<float>& dest) {
        dest.assign(count * channels, 0.0f);
        if (!bgm_reader || bgm_gain <= 0.0 || count == 0) return;
        size_t filled = 0;
        while (filled < count) {
            const int64_t abs_pos = out_pos + static_cast<int64_t>(filled);
            int64_t read_pos = abs_pos;
            size_t take = count - filled;
            if (bgm_total_samples > 0) {
                read_pos = abs_pos % bgm_total_samples;
                if (read_pos < 0) read_pos += bgm_total_samples;
                take = std::min<size_t>(take, static_cast<size_t>(bgm_total_samples - read_pos));
            }
            bgm_reader->read(read_pos, static_cast<int64_t>(take));
            for (size_t i = 0; i < take; ++i) {
                for (int c = 0; c < channels; ++c) {
                    dest[(filled + i) * channels + c] = static_cast<float>(bgm_gain) * bgm_reader->at(read_pos + static_cast<int64_t>(i), c);
                }
            }
            filled += take;
        }
    };
    auto available_handle_out = [&](const ResolvedSegment& seg, bool post_roll, int64_t max_out_samples) -> int64_t {
        if (max_out_samples <= 0) return 0;
        const double rate = static_cast<double>(seg.rate.numerator) / seg.rate.denominator;
        const int64_t needed_src = static_cast<int64_t>(std::ceil(static_cast<double>(max_out_samples) * rate)) + 1;
        const int64_t seg_start = ticks_samples(seg.source_start);
        const int64_t seg_end = ticks_samples(seg.source_end());
        const Json seg_settings = clip_settings_for(seg.id);
        int64_t avail_src = 0;
        for (const auto& m : project.media) {
            if (m.kind != MediaKind::system_audio && m.kind != MediaKind::microphone && m.kind != MediaKind::android_audio) continue;
            const bool system = m.kind == MediaKind::system_audio || m.kind == MediaKind::android_audio;
            const char* gain_key = system ? "systemGain" : "microphoneGain";
            const char* mute_key = system ? "systemMuted" : "microphoneMuted";
            if (settings.value(mute_key, false) || seg_settings.value(mute_key, false)) continue;
            if (settings.value(gain_key, 1.0) * seg_settings.value(gain_key, 1.0) == 0.0) continue;
            const int64_t m_start = ticks_samples(normalized_ticks(m.start_offset));
            const int64_t m_end = m_start + ticks_samples(normalized_ticks(m.duration));
            if (m_end <= seg_start || m_start >= seg_end) continue;
            if (post_roll && m_end > seg_end) {
                avail_src = std::max(avail_src, std::min(needed_src, m_end - seg_end));
            } else if (!post_roll && m_start < seg_start) {
                avail_src = std::max(avail_src, std::min(needed_src, seg_start - m_start));
            }
        }
        if (avail_src <= 0) return 0;
        const int64_t avail_out = static_cast<int64_t>(std::floor(static_cast<double>(avail_src) / rate));
        return std::min(max_out_samples, avail_out);
    };
    struct DualHandle { std::vector<float> sys; std::vector<float> mic; };
    auto render_handle_audio = [&](const ResolvedSegment& seg, int64_t base_src, int64_t src_begin, int64_t src_end, int64_t out_frames) -> DualHandle {
        DualHandle buf;
        if (out_frames <= 0) return buf;
        const bool has_sys = is_track_active_for_segment(seg, TrackGroup::system);
        const bool has_mic = is_track_active_for_segment(seg, TrackGroup::microphone);
        if (has_sys) buf.sys.reserve(static_cast<size_t>(out_frames) * channels);
        if (has_mic) buf.mic.reserve(static_cast<size_t>(out_frames) * channels);
        auto make_handle_fetch = [&](TrackGroup group) -> Fetch {
            return [&, group](int64_t rel_first, int count) {
                std::vector<float> mixed(static_cast<size_t>(count) * channels, 0.0f);
                const int64_t abs_begin = std::max(src_begin, base_src + rel_first);
                const int64_t abs_end = std::min(src_end, base_src + rel_first + count);
                if (abs_end > abs_begin) {
                    auto part = mix_source_range(seg, abs_begin, static_cast<int>(abs_end - abs_begin), group);
                    std::copy(part.begin(), part.end(), mixed.begin() + static_cast<size_t>(abs_begin - (base_src + rel_first)) * channels);
                }
                return mixed;
            };
        };
        wsola_dual(
            make_handle_fetch(TrackGroup::system), has_sys,
            make_handle_fetch(TrackGroup::microphone), has_mic,
            static_cast<double>(seg.rate.numerator) / seg.rate.denominator, out_frames,
            [&](const float* sys_p, const float* mic_p, size_t n) {
                if (has_sys) buf.sys.insert(buf.sys.end(), sys_p, sys_p + n * channels);
                if (has_mic) buf.mic.insert(buf.mic.end(), mic_p, mic_p + n * channels);
            });
        return buf;
    };
    std::vector<float> bgm_buf;
    for (size_t seg_idx = 0; seg_idx < segments.size(); ++seg_idx) {
        const auto& clip = segments[seg_idx];
        check_cancel(); const auto clip_samples = ticks_samples(clip.source_duration), source_first = ticks_samples(clip.source_start);
        const auto clip_first = ticks_samples(clip.output_start);
        const auto begin = std::max(first, clip_first), end = std::min(first + frames, ticks_samples(clip.output_end()));
        if (end <= begin) continue;
        const auto output_count = end - begin;
        const bool has_sys = is_track_active_for_segment(clip, TrackGroup::system);
        const bool has_mic = is_track_active_for_segment(clip, TrackGroup::microphone);
        const bool seam_in = seg_idx > 0 && clip.source_start != segments[seg_idx - 1].source_end();
        const bool seam_out = seg_idx + 1 < segments.size() && clip.source_end() != segments[seg_idx + 1].source_start;
        const int64_t clip_out_total = ticks_samples(clip.output_end()) - clip_first;
        const int64_t trans_in_half = seg_idx > 0 ? seam_half_samples[seg_idx - 1] : 0;
        const int64_t trans_out_half = seg_idx + 1 < segments.size() ? seam_half_samples[seg_idx] : 0;
        const int64_t seam_fade_in = (trans_in_half == 0 && seam_in) ? std::min<int64_t>(288, clip_out_total / 4) : 0;
        const int64_t seam_fade_out = (trans_out_half == 0 && seam_out) ? std::min<int64_t>(288, clip_out_total / 4) : 0;
        const int64_t left_postroll_len = trans_in_half > 0 ? available_handle_out(segments[seg_idx - 1], true, trans_in_half) : 0;
        const int64_t right_preroll_len_for_in = trans_in_half > 0 ? available_handle_out(clip, false, trans_in_half) : 0;
        const int64_t in_span = trans_in_half + right_preroll_len_for_in;
        const int64_t in_out_span = trans_in_half + left_postroll_len;
        const int64_t left_postroll_len_for_out = trans_out_half > 0 ? available_handle_out(clip, true, trans_out_half) : 0;
        const int64_t right_preroll_len = trans_out_half > 0 ? available_handle_out(segments[seg_idx + 1], false, trans_out_half) : 0;
        const int64_t out_span = trans_out_half + left_postroll_len_for_out;
        const int64_t out_in_span = right_preroll_len + trans_out_half;
        DualHandle left_postroll;
        if (left_postroll_len > 0 && (begin - clip_first) < left_postroll_len) {
            const auto& left = segments[seg_idx - 1];
            const double left_rate = static_cast<double>(left.rate.numerator) / left.rate.denominator;
            const int64_t left_anchor = ticks_samples(left.source_end());
            const int64_t needed_src = static_cast<int64_t>(std::llround(static_cast<double>(left_postroll_len) * left_rate));
            left_postroll = render_handle_audio(left, left_anchor, left_anchor, left_anchor + needed_src, left_postroll_len);
        }
        DualHandle right_preroll;
        if (right_preroll_len > 0 && (end - clip_first) > clip_out_total - right_preroll_len) {
            const auto& right = segments[seg_idx + 1];
            const double right_rate = static_cast<double>(right.rate.numerator) / right.rate.denominator;
            const int64_t right_anchor = ticks_samples(right.source_start);
            const int64_t needed_src = static_cast<int64_t>(std::llround(static_cast<double>(right_preroll_len) * right_rate));
            const int64_t base_src = std::max<int64_t>(0, right_anchor - needed_src);
            right_preroll = render_handle_audio(right, base_src, base_src, right_anchor, right_preroll_len);
        }
        int64_t clip_out_cursor = begin - clip_first;
        auto make_fetch = [&](TrackGroup group) -> Fetch {
            return [&, group](int64_t rel_first, int count) {
                std::vector<float> mixed(static_cast<size_t>(count) * channels, 0.0f);
                const int64_t clamped_begin = std::max<int64_t>(0, rel_first);
                const int64_t clamped_end = std::min<int64_t>(clip_samples, rel_first + count);
                if (clamped_end > clamped_begin) {
                    auto part = mix_source_range(clip, source_first + clamped_begin, static_cast<int>(clamped_end - clamped_begin), group);
                    std::copy(part.begin(), part.end(), mixed.begin() + static_cast<size_t>(clamped_begin - rel_first) * channels);
                }
                return mixed;
            };
        };
        wsola_dual(
            make_fetch(TrackGroup::system), has_sys,
            make_fetch(TrackGroup::microphone), has_mic,
            static_cast<double>(clip.rate.numerator) / clip.rate.denominator, output_count,
            [&](const float* sys_samples, const float* mic_samples, size_t count) {
                check_cancel();
                const int64_t chunk_start = clip_out_cursor;
                const int64_t chunk_end = chunk_start + static_cast<int64_t>(count);
                const int64_t out_pos = clip_first + chunk_start;
                const int64_t active_in = trans_in_half > 0 ? trans_in_half : seam_fade_in;
                const int64_t active_out = trans_out_half > 0 ? trans_out_half : seam_fade_out;
                const bool touches_in = active_in > 0 && chunk_start < active_in;
                const bool touches_out = active_out > 0 && chunk_end > clip_out_total - active_out;

                std::vector<float> sys(sys_samples, sys_samples + count * channels);
                std::vector<float> mic(mic_samples, mic_samples + count * channels);
                if (touches_in || touches_out) {
                    for (size_t i = 0; i < count; ++i) {
                        const int64_t pos = chunk_start + static_cast<int64_t>(i);
                        float gain = 1.0f;
                        if (trans_in_half > 0 && pos < trans_in_half) {
                            gain *= static_cast<float>(0.5 * (1.0 - std::cos(pi * (static_cast<double>(right_preroll_len_for_in + pos) + 0.5) / static_cast<double>(in_span))));
                        } else if (seam_fade_in > 0 && pos < seam_fade_in) {
                            gain *= static_cast<float>(0.5 * (1.0 - std::cos(pi * (static_cast<double>(pos) + 0.5) / static_cast<double>(seam_fade_in))));
                        }
                        if (trans_out_half > 0 && pos >= clip_out_total - trans_out_half) {
                            const int64_t k = pos - (clip_out_total - trans_out_half);
                            gain *= static_cast<float>(0.5 * (1.0 + std::cos(pi * (static_cast<double>(k) + 0.5) / static_cast<double>(out_span))));
                        } else if (seam_fade_out > 0 && pos >= clip_out_total - seam_fade_out) {
                            const int64_t rem = clip_out_total - 1 - pos;
                            gain *= static_cast<float>(0.5 * (1.0 - std::cos(pi * (static_cast<double>(rem) + 0.5) / static_cast<double>(seam_fade_out))));
                        }
                        if (gain != 1.0f) {
                            for (int c = 0; c < channels; ++c) {
                                sys[i * channels + c] *= gain;
                                mic[i * channels + c] *= gain;
                            }
                        }
                        if (pos < left_postroll_len) {
                            const float g_post = static_cast<float>(0.5 * (1.0 + std::cos(pi * (static_cast<double>(trans_in_half + pos) + 0.5) / static_cast<double>(in_out_span))));
                            for (int c = 0; c < channels; ++c) {
                                if (!left_postroll.sys.empty()) sys[i * channels + c] += g_post * left_postroll.sys[static_cast<size_t>(pos) * channels + c];
                                if (!left_postroll.mic.empty()) mic[i * channels + c] += g_post * left_postroll.mic[static_cast<size_t>(pos) * channels + c];
                            }
                        }
                        if (pos >= clip_out_total - right_preroll_len) {
                            const int64_t idx = pos - (clip_out_total - right_preroll_len);
                            const float g_pre = static_cast<float>(0.5 * (1.0 - std::cos(pi * (static_cast<double>(idx) + 0.5) / static_cast<double>(out_in_span))));
                            for (int c = 0; c < channels; ++c) {
                                if (!right_preroll.sys.empty()) sys[i * channels + c] += g_pre * right_preroll.sys[static_cast<size_t>(idx) * channels + c];
                                if (!right_preroll.mic.empty()) mic[i * channels + c] += g_pre * right_preroll.mic[static_cast<size_t>(idx) * channels + c];
                            }
                        }
                    }
                }

                const bool any_mic_in_chunk = has_mic || !left_postroll.mic.empty() || !right_preroll.mic.empty();
                const bool any_sys_in_chunk = has_sys || !left_postroll.sys.empty() || !right_preroll.sys.empty();

                if (mic_denoise && any_mic_in_chunk) {
                    for (size_t i = 0; i < count; ++i) {
                        const float l = mic_hpf.process_sample(0, mic[i * channels]);
                        const float r = mic_hpf.process_sample(1, mic[i * channels + 1]);
                        const float g = mic_gate.compute_gain(l, r);
                        mic[i * channels] = l * g;
                        mic[i * channels + 1] = r * g;
                    }
                }

                if (bgm_reader) {
                    fill_bgm(out_pos, count, bgm_buf);
                }

                const bool apply_duck = audio_ducking && any_mic_in_chunk && (any_sys_in_chunk || static_cast<bool>(bgm_reader));
                std::vector<float> out(count * channels, 0.0f);
                for (size_t i = 0; i < count; ++i) {
                    const float m_l = mic[i * channels];
                    const float m_r = mic[i * channels + 1];
                    const float duck = apply_duck ? ducker.next_gain(m_l, m_r) : 1.0f;
                    const float bg_l = bgm_reader ? bgm_buf[i * channels] : 0.0f;
                    const float bg_r = bgm_reader ? bgm_buf[i * channels + 1] : 0.0f;
                    out[i * channels] = m_l + duck * (sys[i * channels] + bg_l);
                    out[i * channels + 1] = m_r + duck * (sys[i * channels + 1] + bg_r);
                }

                if (audio_normalize) {
                    normalizer.process(out.data(), count);
                }

                emit(out.data(), count);
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
