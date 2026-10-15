#include "internal.hpp"
#include <propvarutil.h>
#include <algorithm>

namespace frametrail::media {
std::filesystem::path safe_path(const std::filesystem::path& root, const std::string& relative) {
    if (!is_project_relative_path(relative)) throw ValidationError("path", "unsafe media path");
    auto result = (root / std::filesystem::path(winrt::to_hstring(relative).c_str())).lexically_normal();
    for (auto p = result; !p.empty();) {
        auto attributes = GetFileAttributesW(p.c_str());
        if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) throw ValidationError("path", "media cannot traverse a reparse point");
        auto parent = p.parent_path(); if (parent == p) break; p = parent;
    }
    return result;
}
VideoReader::VideoReader(const std::filesystem::path& path, IMFDXGIDeviceManager* manager) {
    auto open = [&](bool gpu) {
        ComPtr<IMFAttributes> attributes; FT_MEDIA_CHECK(MFCreateAttributes(&attributes, 5));
        FT_MEDIA_CHECK(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE));
        if (gpu) {
            FT_MEDIA_CHECK(attributes->SetUnknown(MF_SOURCE_READER_D3D_MANAGER, manager));
        }
        FT_MEDIA_CHECK(MFCreateSourceReaderFromURL(path.c_str(), attributes.Get(), &reader));
        FT_MEDIA_CHECK(reader->SetStreamSelection(all_streams_id, FALSE));
        FT_MEDIA_CHECK(reader->SetStreamSelection(video_stream_id, TRUE));
        ComPtr<IMFMediaType> native; FT_MEDIA_CHECK(reader->GetNativeMediaType(video_stream_id, 0, &native));
        UINT32 matrix{}, range{}, native_width{}, native_height{};
        MFGetAttributeSize(native.Get(), MF_MT_FRAME_SIZE, &native_width, &native_height);
        // FrameTrail recordings have an explicit BT.709 contract. MF's MPEG-4
        // source may omit the VUI matrix from its native media type at SD sizes;
        // never infer BT.601 solely from raster height.
        matrix709 = SUCCEEDED(native->GetUINT32(MF_MT_YUV_MATRIX, &matrix)) ? matrix == MFVideoTransferMatrix_BT709 : true;
        full_range = SUCCEEDED(native->GetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, &range)) && range == MFNominalRange_0_255;
        UINT32 transfer{}; if (SUCCEEDED(native->GetUINT32(MF_MT_TRANSFER_FUNCTION, &transfer)) && (transfer == MFVideoTransFunc_2084 || transfer == MFVideoTransFunc_HLG))
            throw ValidationError("video", "HDR input requires tone mapping; SDR sources only");
        ComPtr<IMFMediaType> type; FT_MEDIA_CHECK(MFCreateMediaType(&type));
        FT_MEDIA_CHECK(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video)); FT_MEDIA_CHECK(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
        FT_MEDIA_CHECK(reader->SetCurrentMediaType(video_stream_id, nullptr, type.Get()));
        FT_MEDIA_CHECK(reader->GetCurrentMediaType(video_stream_id, &type));
        if (SUCCEEDED(type->GetUINT32(MF_MT_YUV_MATRIX, &matrix))) matrix709 = matrix == MFVideoTransferMatrix_BT709;
        FT_MEDIA_CHECK(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height));
    };
    try { open(true); } catch (winrt::hresult_error const&) { reader.Reset(); cpu = true; open(false); }
    PROPVARIANT media_duration{};
    if (SUCCEEDED(reader->GetPresentationAttribute(media_source_id, MF_PD_DURATION, &media_duration)) && media_duration.vt == VT_UI8) duration = static_cast<int64_t>(media_duration.uhVal.QuadPart);
    PropVariantClear(&media_duration);
    // MF's MPEG-4 source can expose a positive composition-time origin for
    // B-frame files even when their edit list starts at zero. The manifest's
    // local zero is the first decoded frame, not the container DTS origin.
    for (int i = 0; i < 1000; ++i) {
        DWORD flags{}; LONGLONG timestamp{}; ComPtr<IMFSample> sample;
        FT_MEDIA_CHECK(reader->ReadSample(video_stream_id, 0, nullptr, &flags, &timestamp, &sample));
        if (sample) { origin = timestamp; break; }
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) winrt::throw_hresult(MF_E_END_OF_STREAM);
    }
}
ComPtr<ID3D11Texture2D> VideoReader::frame(int64_t ticks, ID3D11Device* device, ID3D11DeviceContext* context) {
    if (ticks < 0) winrt::throw_hresult(E_INVALIDARG);
    if (duration > 0 && ticks > duration + time_scale / 1000) throw ValidationError("video", "media is shorter than its manifest; refusing to extend a missing source interval");
    if (request < 0 || ticks < request || ticks - request > time_scale) {
        // SetCurrentPosition is in the file's presentation timeline, while
        // ReadSample may report the positive decoder composition origin.
        PROPVARIANT position{}; FT_MEDIA_CHECK(InitPropVariantFromInt64(ticks, &position));
        auto hr = reader->SetCurrentPosition(GUID_NULL, position); PropVariantClear(&position);
        // Sparse WGC files may contain one frame held until stop. Container
        // duration is quantized and MF can reject a valid logical tail seek.
        // Decode from zero, then retain the actual last sample across its hold.
        if (hr == MF_E_INVALID_POSITION) { PROPVARIANT zero{}; FT_MEDIA_CHECK(InitPropVariantFromInt64(0, &zero)); hr = reader->SetCurrentPosition(GUID_NULL, zero); PropVariantClear(&zero); }
        FT_MEDIA_CHECK(hr);
        current.Reset(); next.Reset(); current_time = next_time = -1; eof = false;
    }
    request = ticks;
    for (int guard = 0; guard < 30000; ++guard) {
        if (next && next_time > ticks && current) break;
        if (next) { current = next; current_time = next_time; next.Reset(); }
        if (eof) break;
        DWORD flags{}; LONGLONG timestamp{}; ComPtr<IMFSample> sample;
        FT_MEDIA_CHECK(reader->ReadSample(video_stream_id, 0, nullptr, &flags, &timestamp, &sample));
        if (flags & MF_SOURCE_READERF_ERROR) winrt::throw_hresult(MF_E_INVALID_FILE_FORMAT);
        if (flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) {
            ComPtr<IMFMediaType> type; UINT32 w{}, h{}; FT_MEDIA_CHECK(reader->GetCurrentMediaType(video_stream_id, &type)); FT_MEDIA_CHECK(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h));
            if (w != width || h != height) throw ValidationError("video", "source dimensions changed inside a media file; restart preview with a supported fixed-size source");
        }
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) eof = true;
        if (sample) { next = sample; next_time = timestamp - origin; if (!current) { current = next; current_time = next_time; next.Reset(); } }
        if (eof && !next) break;
    }
    if (!current) winrt::throw_hresult(MF_E_END_OF_STREAM);
    ComPtr<IMFMediaBuffer> buffer; FT_MEDIA_CHECK(current->GetBufferByIndex(0, &buffer));
    ComPtr<IMFDXGIBuffer> dxgi; ComPtr<ID3D11Texture2D> texture;
    if (SUCCEEDED(buffer.As(&dxgi))) {
        FT_MEDIA_CHECK(dxgi->GetResource(IID_PPV_ARGS(&texture))); UINT subresource{}; FT_MEDIA_CHECK(dxgi->GetSubresourceIndex(&subresource));
        D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
        // Copy out of decoder-owned buffers before ReadSample can recycle them.
        desc.ArraySize = 1; desc.MipLevels = 1; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        desc.MiscFlags = 0; desc.Usage = D3D11_USAGE_DEFAULT; desc.CPUAccessFlags = 0;
        if (!upload) FT_MEDIA_CHECK(device->CreateTexture2D(&desc, nullptr, &upload));
        context->CopySubresourceRegion(upload.Get(), 0, 0, 0, 0, texture.Get(), subresource, nullptr);
        return to_rgb(device, context);
    }
    cpu = true;
    ComPtr<IMF2DBuffer> two; BYTE* bytes{}; LONG stride{}; DWORD length{};
    const bool is2d = SUCCEEDED(buffer.As(&two));
    if (is2d) FT_MEDIA_CHECK(two->Lock2D(&bytes, &stride));
    else { FT_MEDIA_CHECK(buffer->Lock(&bytes, nullptr, &length)); stride = static_cast<LONG>(width * 4); }
    if (!is2d) stride = static_cast<LONG>(width);
    std::vector<BYTE> packed(static_cast<size_t>(width) * height * 3 / 2);
    for (uint32_t y = 0; y < height + height / 2; ++y) memcpy(packed.data() + static_cast<size_t>(y) * width, bytes + static_cast<ptrdiff_t>(y) * stride, width);
    if (is2d) two->Unlock2D(); else buffer->Unlock();
    if (!upload) {
        D3D11_TEXTURE2D_DESC desc{}; desc.Width = width; desc.Height = height; desc.MipLevels = desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_NV12; desc.SampleDesc.Count = 1; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        FT_MEDIA_CHECK(device->CreateTexture2D(&desc, nullptr, &upload));
    }
    context->UpdateSubresource(upload.Get(), 0, nullptr, packed.data(), width, 0); return to_rgb(device, context);
}
ComPtr<ID3D11Texture2D> VideoReader::to_rgb(ID3D11Device* device, ID3D11DeviceContext* immediate) {
    ComPtr<ID3D11VideoDevice> video; FT_MEDIA_CHECK(device->QueryInterface(IID_PPV_ARGS(&video)));
    ComPtr<ID3D11VideoContext> context; FT_MEDIA_CHECK(immediate->QueryInterface(IID_PPV_ARGS(&context)));
    if (!converted) {
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc{}; desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        desc.InputWidth = desc.OutputWidth = width; desc.InputHeight = desc.OutputHeight = height; desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        desc.InputFrameRate = desc.OutputFrameRate = {30, 1}; FT_MEDIA_CHECK(video->CreateVideoProcessorEnumerator(&desc, &video_enum)); FT_MEDIA_CHECK(video->CreateVideoProcessor(video_enum.Get(), 0, &processor));
        D3D11_TEXTURE2D_DESC target{}; target.Width = width; target.Height = height; target.Format = DXGI_FORMAT_B8G8R8A8_UNORM; target.ArraySize = target.MipLevels = 1;
        target.SampleDesc.Count = 1; target.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE; FT_MEDIA_CHECK(device->CreateTexture2D(&target, nullptr, &converted));
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC out{}; out.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D; FT_MEDIA_CHECK(video->CreateVideoProcessorOutputView(converted.Get(), video_enum.Get(), &out, &converted_view));
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE yuv{}, rgb{}; yuv.YCbCr_Matrix = matrix709 ? 1 : 0; yuv.Nominal_Range = full_range ? 2 : 1; rgb.Nominal_Range = 2;
        context->VideoProcessorSetStreamColorSpace(processor.Get(), 0, &yuv); context->VideoProcessorSetOutputColorSpace(processor.Get(), &rgb);
        context->VideoProcessorSetStreamAutoProcessingMode(processor.Get(), 0, FALSE);
    }
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC input{}; input.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    ComPtr<ID3D11VideoProcessorInputView> view; FT_MEDIA_CHECK(video->CreateVideoProcessorInputView(upload.Get(), video_enum.Get(), &input, &view));
    RECT bounds{0, 0, static_cast<LONG>(width), static_cast<LONG>(height)}; context->VideoProcessorSetStreamSourceRect(processor.Get(), 0, TRUE, &bounds); context->VideoProcessorSetStreamDestRect(processor.Get(), 0, TRUE, &bounds);
    D3D11_VIDEO_PROCESSOR_STREAM stream{}; stream.Enable = TRUE; stream.pInputSurface = view.Get(); FT_MEDIA_CHECK(context->VideoProcessorBlt(processor.Get(), converted_view.Get(), 0, 1, &stream));
    return converted;
}
void write_png(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* texture, const std::filesystem::path& path) {
    D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc); desc.BindFlags = desc.MiscFlags = 0;
    desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging; FT_MEDIA_CHECK(device->CreateTexture2D(&desc, nullptr, &staging)); context->CopyResource(staging.Get(), texture);
    D3D11_MAPPED_SUBRESOURCE mapped{}; FT_MEDIA_CHECK(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
    // Copy before any WIC call so failure always leaves the texture unmapped.
    std::vector<BYTE> bytes(static_cast<size_t>(desc.Width) * desc.Height * 4);
    for (UINT y = 0; y < desc.Height; ++y) memcpy(bytes.data() + static_cast<size_t>(y) * desc.Width * 4, static_cast<BYTE*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch, desc.Width * 4);
    context->Unmap(staging.Get(), 0);
    ComPtr<IWICImagingFactory> factory; FT_MEDIA_CHECK(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)));
    ComPtr<IWICStream> stream; FT_MEDIA_CHECK(factory->CreateStream(&stream)); FT_MEDIA_CHECK(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE));
    ComPtr<IWICBitmapEncoder> encoder; FT_MEDIA_CHECK(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder)); FT_MEDIA_CHECK(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache));
    ComPtr<IWICBitmapFrameEncode> frame; FT_MEDIA_CHECK(encoder->CreateNewFrame(&frame, nullptr)); FT_MEDIA_CHECK(frame->Initialize(nullptr)); FT_MEDIA_CHECK(frame->SetSize(desc.Width, desc.Height));
    auto format = GUID_WICPixelFormat32bppBGRA; FT_MEDIA_CHECK(frame->SetPixelFormat(&format));
    FT_MEDIA_CHECK(frame->WritePixels(desc.Height, desc.Width * 4, static_cast<UINT>(bytes.size()), bytes.data())); FT_MEDIA_CHECK(frame->Commit()); FT_MEDIA_CHECK(encoder->Commit());
}
}
