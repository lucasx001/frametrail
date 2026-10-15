#include "internal.hpp"
#include <algorithm>
#include <array>
#include <propvarutil.h>

namespace frametrail::media {
namespace {
struct Cleanup { std::vector<std::filesystem::path> files; ~Cleanup() { for (auto& p : files) { std::error_code ec; std::filesystem::remove(p, ec); } } };
std::wstring job_id() { GUID id{}; FT_MEDIA_CHECK(CoCreateGuid(&id)); wchar_t value[40]{}; StringFromGUID2(id, value, 40); return value; }
ComPtr<IMFMediaType> pcm_type() {
    ComPtr<IMFMediaType> type; FT_MEDIA_CHECK(MFCreateMediaType(&type));
    FT_MEDIA_CHECK(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio)); FT_MEDIA_CHECK(type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM));
    FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, channels)); FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, sample_rate));
    FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16)); FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, channels * 2));
    FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, sample_rate * channels * 2)); return type;
}
ComPtr<IMFSample> sample_bytes(const BYTE* bytes, DWORD length, int64_t time, int64_t duration) {
    ComPtr<IMFMediaBuffer> buffer; FT_MEDIA_CHECK(MFCreateMemoryBuffer(length, &buffer)); BYTE* data{}; FT_MEDIA_CHECK(buffer->Lock(&data, nullptr, nullptr));
    memcpy(data, bytes, length); buffer->Unlock(); FT_MEDIA_CHECK(buffer->SetCurrentLength(length));
    ComPtr<IMFSample> sample; FT_MEDIA_CHECK(MFCreateSample(&sample)); FT_MEDIA_CHECK(sample->AddBuffer(buffer.Get())); FT_MEDIA_CHECK(sample->SetSampleTime(time)); FT_MEDIA_CHECK(sample->SetSampleDuration(duration)); return sample;
}
// GPU BT.709 RGB -> limited-range NV12. Encoder upload is an explicit bounded
// CPU fallback, shared by hardware/software MF encoders; no pixels cross the ABI.
struct Nv12 {
    Renderer& gpu; uint32_t w, h;
    ComPtr<ID3D11VideoDevice> device; ComPtr<ID3D11VideoContext> context;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator; ComPtr<ID3D11VideoProcessor> processor;
    ComPtr<ID3D11Texture2D> output, staging; ComPtr<ID3D11VideoProcessorOutputView> view;
    Nv12(Renderer& gpu, uint32_t w, uint32_t h, uint32_t fps) : gpu(gpu), w(w), h(h) {
        FT_MEDIA_CHECK(gpu.device.As(&device)); FT_MEDIA_CHECK(gpu.context.As(&context));
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc{}; desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE; desc.InputWidth = desc.OutputWidth = w; desc.InputHeight = desc.OutputHeight = h;
        desc.InputFrameRate = desc.OutputFrameRate = {fps, 1}; desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        FT_MEDIA_CHECK(device->CreateVideoProcessorEnumerator(&desc, &enumerator)); UINT support{}; FT_MEDIA_CHECK(enumerator->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &support));
        if (!(support & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT)) winrt::throw_hresult(MF_E_INVALIDMEDIATYPE);
        FT_MEDIA_CHECK(device->CreateVideoProcessor(enumerator.Get(), 0, &processor));
        D3D11_TEXTURE2D_DESC texture{}; texture.Width = w; texture.Height = h; texture.MipLevels = texture.ArraySize = 1; texture.Format = DXGI_FORMAT_NV12; texture.SampleDesc.Count = 1; texture.BindFlags = D3D11_BIND_RENDER_TARGET;
        FT_MEDIA_CHECK(gpu.device->CreateTexture2D(&texture, nullptr, &output)); texture.BindFlags = 0; texture.Usage = D3D11_USAGE_STAGING; texture.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        FT_MEDIA_CHECK(gpu.device->CreateTexture2D(&texture, nullptr, &staging));
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC output_desc{}; output_desc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D; FT_MEDIA_CHECK(device->CreateVideoProcessorOutputView(output.Get(), enumerator.Get(), &output_desc, &view));
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE rgb{}, yuv{}; rgb.Nominal_Range = 2; rgb.RGB_Range = 0; yuv.YCbCr_Matrix = 1; yuv.Nominal_Range = 1;
        context->VideoProcessorSetStreamColorSpace(processor.Get(), 0, &rgb); context->VideoProcessorSetOutputColorSpace(processor.Get(), &yuv);
        context->VideoProcessorSetStreamAutoProcessingMode(processor.Get(), 0, FALSE);
    }
    std::vector<BYTE> convert(ID3D11Texture2D* input) {
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC desc{}; desc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        ComPtr<ID3D11VideoProcessorInputView> in; FT_MEDIA_CHECK(device->CreateVideoProcessorInputView(input, enumerator.Get(), &desc, &in));
        RECT rect{0, 0, static_cast<LONG>(w), static_cast<LONG>(h)}; context->VideoProcessorSetStreamSourceRect(processor.Get(), 0, TRUE, &rect); context->VideoProcessorSetStreamDestRect(processor.Get(), 0, TRUE, &rect);
        D3D11_VIDEO_PROCESSOR_STREAM stream{}; stream.Enable = TRUE; stream.pInputSurface = in.Get(); FT_MEDIA_CHECK(context->VideoProcessorBlt(processor.Get(), view.Get(), 0, 1, &stream));
        gpu.context->CopyResource(staging.Get(), output.Get()); D3D11_MAPPED_SUBRESOURCE mapped{}; FT_MEDIA_CHECK(gpu.context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
        std::vector<BYTE> result(static_cast<size_t>(w) * h * 3 / 2);
        for (UINT y = 0; y < h + h / 2; ++y) memcpy(result.data() + static_cast<size_t>(y) * w, static_cast<BYTE*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch, w);
        gpu.context->Unmap(staging.Get(), 0); return result;
    }
};
void validate_output(const std::filesystem::path& path, bool video, int64_t expected) {
    ComPtr<IMFSourceReader> reader; FT_MEDIA_CHECK(MFCreateSourceReaderFromURL(path.c_str(), nullptr, &reader));
    PROPVARIANT duration{}; FT_MEDIA_CHECK(reader->GetPresentationAttribute(media_source_id, MF_PD_DURATION, &duration));
    const auto actual = static_cast<int64_t>(duration.uhVal.QuadPart); PropVariantClear(&duration);
    if (std::abs(actual - expected) > time_scale / 10) throw ValidationError("export", "finalized media duration differs from timeline by over 100 ms");
    ComPtr<IMFSample> sample; DWORD flags{}; FT_MEDIA_CHECK(reader->ReadSample(video ? video_stream_id : audio_stream_id, 0, nullptr, &flags, nullptr, &sample));
    if (!sample || std::filesystem::file_size(path) < 128) throw ValidationError("export", "finalized media has no readable sample");
}
}
void Session::export_file(const std::filesystem::path& destination, bool audio_only) {
    if (!audio_only && !project.effects.empty()) throw ValidationError("effects", "advanced visual effects require M3; export is disabled to avoid silently omitting authored effects");
    check_cancel(); auto target = std::filesystem::absolute(destination).lexically_normal();
    for (auto path = target; !path.empty();) {
        const auto attributes = GetFileAttributesW(path.c_str());
        if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) throw ValidationError("export", "output path cannot traverse a reparse point");
        auto next = path.parent_path(); if (next == path) break; path = next;
    }
    // Refuse any target inside the project, including the source files themselves.
    for (auto path = target.parent_path(); !path.empty();) {
        if (CompareStringOrdinal(path.c_str(), -1, root.c_str(), -1, TRUE) == CSTR_EQUAL) throw ValidationError("export", "choose an output outside the project directory");
        auto next = path.parent_path(); if (next == path) break; path = next;
    }
    const bool has_audio = std::any_of(project.media.begin(), project.media.end(), [](auto& m) { return m.kind == MediaKind::microphone || m.kind == MediaKind::system_audio; });
    if (audio_only && !has_audio) throw ValidationError("audio", "project has no recorded audio track");
    auto id = job_id(); const auto temp = target.parent_path() / (L".frametrail-" + id + L".mp4"), wav = target.parent_path() / (L".frametrail-" + id + L".wav");
    Cleanup cleanup{{temp, wav}};
    const auto duration = evaluator.timeline().output_duration(); const auto fps = project.export_settings.fps.numerator;
    const uint64_t frame_count = static_cast<uint64_t>((duration * fps + time_scale - 1) / time_scale);
    const auto audio_frames = ticks_samples(duration); DWORD video_stream{}, audio_stream{};
    ComPtr<IMFAttributes> attributes; FT_MEDIA_CHECK(MFCreateAttributes(&attributes, 4));
    FT_MEDIA_CHECK(attributes->SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4));
    FT_MEDIA_CHECK(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE)); FT_MEDIA_CHECK(attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE));
    ComPtr<IMFSinkWriter> writer; FT_MEDIA_CHECK(MFCreateSinkWriterFromURL(temp.c_str(), nullptr, attributes.Get(), &writer));
    std::unique_ptr<Nv12> converter;
    if (!audio_only) {
        ComPtr<IMFMediaType> type; FT_MEDIA_CHECK(MFCreateMediaType(&type)); FT_MEDIA_CHECK(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video)); FT_MEDIA_CHECK(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
        FT_MEDIA_CHECK(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, project.export_settings.width, project.export_settings.height)); FT_MEDIA_CHECK(MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, fps, 1));
        FT_MEDIA_CHECK(MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1)); FT_MEDIA_CHECK(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
        FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AVG_BITRATE, std::clamp<uint32_t>(project.export_settings.width * project.export_settings.height * fps / 8, 4000000, 80000000)));
        FT_MEDIA_CHECK(type->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709)); FT_MEDIA_CHECK(type->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709));
        FT_MEDIA_CHECK(type->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709)); FT_MEDIA_CHECK(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235));
        FT_MEDIA_CHECK(writer->AddStream(type.Get(), &video_stream));
        ComPtr<IMFMediaType> input; FT_MEDIA_CHECK(MFCreateMediaType(&input)); FT_MEDIA_CHECK(type->CopyAllItems(input.Get()));
        FT_MEDIA_CHECK(input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12)); FT_MEDIA_CHECK(writer->SetInputMediaType(video_stream, input.Get(), nullptr));
        converter = std::make_unique<Nv12>(gpu(), project.export_settings.width, project.export_settings.height, fps);
    }
    if (has_audio) {
        auto type = pcm_type(); FT_MEDIA_CHECK(type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC)); FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000));
        FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0)); FT_MEDIA_CHECK(type->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29));
        FT_MEDIA_CHECK(writer->AddStream(type.Get(), &audio_stream)); auto pcm = pcm_type(); FT_MEDIA_CHECK(writer->SetInputMediaType(audio_stream, pcm.Get(), nullptr));
        mix_audio(wav);
    }
    check_cancel(); FT_MEDIA_CHECK(writer->BeginWriting()); stage = 2; progress(0, audio_only ? static_cast<uint64_t>(audio_frames) : frame_count);
    std::ifstream audio; if (has_audio) { audio.open(wav, std::ios::binary); audio.seekg(44); }
    int64_t audio_position = 0;
    auto write_audio = [&](int64_t until) {
        if (!has_audio) return;
        while (audio_position < until) {
            check_cancel(); auto n = static_cast<DWORD>(std::min<int64_t>(1024, until - audio_position)); std::vector<BYTE> bytes(n * channels * 2);
            audio.read(reinterpret_cast<char*>(bytes.data()), bytes.size()); if (audio.gcount() != static_cast<std::streamsize>(bytes.size())) throw ValidationError("audio", "truncated prepared audio");
            auto sample = sample_bytes(bytes.data(), static_cast<DWORD>(bytes.size()), sample_ticks(audio_position), sample_ticks(audio_position + n) - sample_ticks(audio_position)); FT_MEDIA_CHECK(writer->WriteSample(audio_stream, sample.Get())); audio_position += n;
            if (audio_only) progress(static_cast<uint64_t>(audio_position), static_cast<uint64_t>(audio_frames));
        }
    };
    if (audio_only) write_audio(audio_frames);
    else for (uint64_t i = 0; i < frame_count; ++i) {
        check_cancel(); const auto ticks = static_cast<int64_t>(i) * time_scale / fps, end = std::min<int64_t>(duration, static_cast<int64_t>(i + 1) * time_scale / fps);
        const auto scene = evaluator.evaluate(ticks); if (!scene) throw ValidationError("export", "missing scheduled output frame");
        auto* texture = gpu().render(project, *scene, project.export_settings.width, project.export_settings.height); auto bytes = converter->convert(texture);
        auto sample = sample_bytes(bytes.data(), static_cast<DWORD>(bytes.size()), ticks, end - ticks); FT_MEDIA_CHECK(writer->WriteSample(video_stream, sample.Get()));
        write_audio(ticks_samples(end)); progress(i + 1, frame_count);
    }
    check_cancel(); stage = 3; FT_MEDIA_CHECK(writer->Finalize()); writer.Reset(); audio.close(); check_cancel();
    validate_output(temp, !audio_only, duration); check_cancel();
    if (!MoveFileExW(temp.c_str(), target.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) FT_MEDIA_CHECK(HRESULT_FROM_WIN32(GetLastError()));
    stage = 4; progress(total, total);
}
}
