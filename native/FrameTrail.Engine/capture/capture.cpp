#include "capture.h"
#include "frame_pacing.h"
#include <windows.h>
#include <roapi.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi1_6.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <mftransform.h>
#include <dwmapi.h>
#include <shellscalingapi.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <algorithm>
#include <cmath>
#include <condition_variable>
#include <deque>
#include <filesystem>
#include <future>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
namespace {
constexpr int64_t hns_per_second = 10000000;
thread_local std::wstring last_error;
int64_t qpc_now() { LARGE_INTEGER v{}; QueryPerformanceCounter(&v); return v.QuadPart; }
int64_t qpc_frequency() { LARGE_INTEGER v{}; QueryPerformanceFrequency(&v); return v.QuadPart; }
int64_t scale_time(int64_t value, int64_t from, int64_t to) {
    return (value / from) * to + (value % from) * to / from;
}
struct apartment_scope {
    HRESULT result = RoInitialize(RO_INIT_MULTITHREADED);
    apartment_scope() { if (FAILED(result) && result != RPC_E_CHANGED_MODE) check_hresult(result); }
    ~apartment_scope() { if (SUCCEEDED(result)) { winrt::clear_factory_cache(); RoUninitialize(); } }
};
struct mf_scope { mf_scope() { check_hresult(MFStartup(MF_VERSION)); } ~mf_scope() { MFShutdown(); } };
template <class F> HRESULT guard(F&& fn) noexcept {
    try { fn(); return S_OK; } catch (hresult_error const& e) { last_error = e.message().c_str(); return e.code(); }
    catch (std::bad_alloc const&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
}
uint32_t encoder_count(UINT32 flags, std::vector<GUID>* classes = nullptr) {
    MFT_REGISTER_TYPE_INFO output{ MFMediaType_Video, MFVideoFormat_H264 };
    IMFActivate** activations{}; UINT32 count{};
    check_hresult(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags | MFT_ENUM_FLAG_SORTANDFILTER,
        nullptr, &output, &activations, &count));
    for (UINT32 i = 0; i < count; ++i) {
        GUID id{};
        if (classes && SUCCEEDED(activations[i]->GetGUID(MFT_TRANSFORM_CLSID_Attribute, &id))) classes->push_back(id);
        activations[i]->Release();
    }
    CoTaskMemFree(activations);
    return count;
}
com_ptr<IDXGIAdapter1> adapter_for(HMONITOR monitor, uint32_t* hdr = nullptr) {
    com_ptr<IDXGIFactory1> factory; check_hresult(CreateDXGIFactory1(__uuidof(IDXGIFactory1), factory.put_void()));
    if (hdr) *hdr = 2;
    for (UINT a = 0;; ++a) {
        com_ptr<IDXGIAdapter1> adapter;
        if (factory->EnumAdapters1(a, adapter.put()) == DXGI_ERROR_NOT_FOUND) break;
        for (UINT o = 0;; ++o) {
            com_ptr<IDXGIOutput> output;
            if (adapter->EnumOutputs(o, output.put()) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_OUTPUT_DESC desc{}; check_hresult(output->GetDesc(&desc));
            if (desc.Monitor != monitor) continue;
            if (hdr) {
                auto output6 = output.try_as<IDXGIOutput6>();
                DXGI_OUTPUT_DESC1 d{};
                if (output6 && SUCCEEDED(output6->GetDesc1(&d)))
                    *hdr = d.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ? 1U : 0U;
            }
            return adapter;
        }
    }
    return {};
}
uint32_t source_hdr(uint32_t kind, uintptr_t handle) {
    if (kind != FT_CAPTURE_WINDOW) { uint32_t hdr; adapter_for(reinterpret_cast<HMONITOR>(handle), &hdr); return hdr; }
    RECT bounds{};
    if (!GetWindowRect(reinterpret_cast<HWND>(handle), &bounds)) return 2;
    struct hdr_data { uint32_t value; } data{};
    EnumDisplayMonitors(nullptr, &bounds, [](HMONITOR mon, HDC, LPRECT, LPARAM value) -> BOOL {
        auto& d = *reinterpret_cast<hdr_data*>(value); uint32_t hdr = 2;
        try { adapter_for(mon, &hdr); } catch (...) { hdr = 2; }
        d.value = std::max(d.value, hdr); return TRUE;
    }, reinterpret_cast<LPARAM>(&data));
    return data.value;
}
std::vector<ft_capture_source> enumerate_sources() {
    std::vector<ft_capture_source> result;
    EnumDisplayMonitors(nullptr, nullptr, [](HMONITOR mon, HDC, LPRECT rect, LPARAM value) -> BOOL {
        auto& sources = *reinterpret_cast<std::vector<ft_capture_source>*>(value);
        ft_capture_source source{}; source.struct_size = sizeof(source); source.kind = FT_CAPTURE_MONITOR;
        source.handle = reinterpret_cast<uintptr_t>(mon); source.x = rect->left; source.y = rect->top;
        source.width = rect->right - rect->left; source.height = rect->bottom - rect->top;
        UINT x = 96, y = 96; GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, &x, &y); source.dpi = x;
        MONITORINFOEXW info{}; info.cbSize = sizeof(info); GetMonitorInfoW(mon, &info);
        wcsncpy_s(source.name, info.szDevice, _TRUNCATE);
        try { source.hdr = source_hdr(source.kind, source.handle); } catch (...) { source.hdr = 2; }
        try { sources.push_back(source); } catch (...) { return FALSE; }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&result));
    EnumWindows([](HWND hwnd, LPARAM value) -> BOOL {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || GetWindowTextLengthW(hwnd) == 0) return TRUE;
        if (GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) return TRUE;
        DWORD cloaked{}; if (SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked) return TRUE;
        RECT rect{}; if (!GetWindowRect(hwnd, &rect)) return TRUE;
        ft_capture_source source{}; source.struct_size = sizeof(source); source.kind = FT_CAPTURE_WINDOW;
        source.handle = reinterpret_cast<uintptr_t>(hwnd); source.x = rect.left; source.y = rect.top;
        source.width = rect.right - rect.left; source.height = rect.bottom - rect.top;
        source.dpi = GetDpiForWindow(hwnd); GetWindowTextW(hwnd, source.name, 256);
        try { source.hdr = source_hdr(source.kind, source.handle); } catch (...) { source.hdr = 2; }
        try { reinterpret_cast<std::vector<ft_capture_source>*>(value)->push_back(source); } catch (...) { return FALSE; }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&result));
    return result;
}
struct frame_entry { com_ptr<ID3D11Texture2D> texture; int64_t time{}; };
class capture_run {
    ft_capture_options options_{};
    std::wstring path_;
    std::thread thread_;
    mutable std::mutex mutex_;
    std::mutex callback_mutex_;
    std::condition_variable cv_;
    std::deque<frame_entry> queue_;
    std::vector<com_ptr<ID3D11Texture2D>> free_;
    ft_capture_status status_{};
    ft_capture_performance performance_{sizeof(ft_capture_performance)};
    bool stopping_{};
    int64_t first_hns_{-1}, last_accepted_hns_{-1}, resume_cutoff_qpc_{};
    int64_t stop_qpc_{};
    std::promise<HRESULT> ready_;
    com_ptr<ID3D11Device> device_;
    com_ptr<ID3D11DeviceContext> context_;
    GraphicsCaptureItem item_{nullptr};
    Direct3D11CaptureFramePool pool_{nullptr};
    GraphicsCaptureSession session_{nullptr};
    event_token arrived_token_{}, closed_token_{};
    winrt::Windows::Graphics::SizeInt32 source_size_{};
    D3D11_BOX crop_{};
    com_ptr<IMFSinkWriter> writer_;
    com_ptr<IMFDXGIDeviceManager> device_manager_;
    com_ptr<IMFVideoSampleAllocatorEx> sample_allocator_;
    DWORD stream_{};
    com_ptr<ID3D11VideoDevice> video_device_;
    com_ptr<ID3D11VideoContext> video_context_;
    com_ptr<ID3D11VideoProcessorEnumerator> processor_enum_;
    com_ptr<ID3D11VideoProcessor> processor_;
    com_ptr<ID3D11Texture2D> nv12_, staging_;
    com_ptr<ID3D11VideoProcessorOutputView> nv12_view_;

    void fail(HRESULT code, std::wstring const& message) noexcept {
        std::lock_guard lock(mutex_);
        if (SUCCEEDED(status_.error_code)) { status_.error_code = code; wcsncpy_s(status_.message, message.c_str(), _TRUNCATE); }
        stopping_ = true; status_.state = FT_CAPTURE_FAILED;
        if (!stop_qpc_) stop_qpc_ = qpc_now();
        cv_.notify_all();
    }
    void setup_device() {
        HMONITOR monitor = options_.kind == FT_CAPTURE_WINDOW
            ? MonitorFromWindow(reinterpret_cast<HWND>(options_.source_handle), MONITOR_DEFAULTTONULL)
            : reinterpret_cast<HMONITOR>(options_.source_handle);
        auto adapter = adapter_for(monitor);
        if (!adapter) throw hresult_error(E_INVALIDARG, L"Selected display is no longer connected.");
        check_hresult(D3D11CreateDevice(adapter.get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT, nullptr, 0,
            D3D11_SDK_VERSION, device_.put(), nullptr, context_.put()));
        // Protect immediate context use by WGC's callback and our encoding worker.
        auto multithread = context_.as<ID3D11Multithread>(); multithread->SetMultithreadProtected(TRUE);
        video_device_ = device_.as<ID3D11VideoDevice>(); video_context_ = context_.as<ID3D11VideoContext>();
        auto interop = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        if (options_.kind == FT_CAPTURE_WINDOW)
            check_hresult(interop->CreateForWindow(reinterpret_cast<HWND>(options_.source_handle), guid_of<GraphicsCaptureItem>(), put_abi(item_)));
        else check_hresult(interop->CreateForMonitor(monitor, guid_of<GraphicsCaptureItem>(), put_abi(item_)));
        source_size_ = item_.Size();
        if (source_size_.Width <= 0 || source_size_.Height <= 0) throw hresult_error(E_INVALIDARG, L"Capture source has no visible content.");
        int32_t x = 0, y = 0, w = source_size_.Width, h = source_size_.Height;
        if (options_.kind == FT_CAPTURE_REGION) { x = options_.crop_x; y = options_.crop_y; w = options_.crop_width; h = options_.crop_height; }
        if (x < 0 || y < 0 || w <= 1 || h <= 1 || int64_t(x) + w > source_size_.Width || int64_t(y) + h > source_size_.Height)
            throw hresult_error(E_INVALIDARG, L"Region must fit one display in physical pixels.");
        // H.264 NV12 requires even dimensions. Trim at most one pixel on right/bottom; reported dimensions are authoritative.
        w &= ~1; h &= ~1;
        crop_ = { static_cast<UINT>(x), static_cast<UINT>(y), 0, static_cast<UINT>(x + w), static_cast<UINT>(y + h), 1 };
        status_.width = w; status_.height = h;
        D3D11_TEXTURE2D_DESC desc{}; desc.Width = w; desc.Height = h; desc.MipLevels = 1; desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        for (uint32_t i = 0; i < options_.queue_capacity; ++i) { com_ptr<ID3D11Texture2D> t; check_hresult(device_->CreateTexture2D(&desc, nullptr, t.put())); free_.push_back(std::move(t)); }
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{}; content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        content.InputWidth = w; content.InputHeight = h; content.OutputWidth = w; content.OutputHeight = h;
        content.InputFrameRate = { options_.fps, 1 }; content.OutputFrameRate = content.InputFrameRate;
        content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        check_hresult(video_device_->CreateVideoProcessorEnumerator(&content, processor_enum_.put()));
        UINT format_support{}; check_hresult(processor_enum_->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &format_support));
        if (!(format_support & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT)) throw hresult_error(MF_E_UNSUPPORTED_D3D_TYPE, L"GPU cannot convert this capture to NV12.");
        check_hresult(video_device_->CreateVideoProcessor(processor_enum_.get(), 0, processor_.put()));
        desc.Format = DXGI_FORMAT_NV12; check_hresult(device_->CreateTexture2D(&desc, nullptr, nv12_.put()));
        desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        check_hresult(device_->CreateTexture2D(&desc, nullptr, staging_.put()));
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC output{}; output.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        check_hresult(video_device_->CreateVideoProcessorOutputView(nv12_.get(), processor_enum_.get(), &output, nv12_view_.put()));
        RECT full{ 0,0,w,h };
        video_context_->VideoProcessorSetStreamFrameFormat(processor_.get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        video_context_->VideoProcessorSetStreamSourceRect(processor_.get(), 0, TRUE, &full);
        video_context_->VideoProcessorSetStreamDestRect(processor_.get(), 0, TRUE, &full);
        video_context_->VideoProcessorSetOutputTargetRect(processor_.get(), TRUE, &full);
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE rgb{}; rgb.RGB_Range = 0; rgb.YCbCr_Matrix = 1;
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE yuv{}; yuv.YCbCr_Matrix = 1; yuv.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
        video_context_->VideoProcessorSetStreamColorSpace(processor_.get(), 0, &rgb);
        video_context_->VideoProcessorSetOutputColorSpace(processor_.get(), &yuv);
    }
    bool inspect_hardware(std::vector<GUID> const& hardware_classes) {
        auto extended = writer_.try_as<IMFSinkWriterEx>(); if (!extended) return false;
        for (DWORD index = 0; index < 16; ++index) {
            GUID category{}; com_ptr<IMFTransform> transform;
            if (FAILED(extended->GetTransformForStream(stream_, index, &category, transform.put()))) break;
            if (category != MFT_CATEGORY_VIDEO_ENCODER) continue;
            com_ptr<IMFAttributes> attrs; if (FAILED(transform->GetAttributes(attrs.put()))) return false;
            UINT32 length{};
            if (SUCCEEDED(attrs->GetStringLength(MFT_ENUM_HARDWARE_URL_Attribute, &length)) && length) return true;
            GUID id{};
            if (SUCCEEDED(attrs->GetGUID(MFT_TRANSFORM_CLSID_Attribute, &id)))
                for (auto const& hardware : hardware_classes) if (hardware == id) return true;
        }
        return false;
    }
    void setup_writer() {
        std::vector<GUID> hardware_classes;
        status_.hardware_available = encoder_count(MFT_ENUM_FLAG_HARDWARE, &hardware_classes);
        bool software_allowed = options_.allow_software && status_.width <= 1920 && status_.height <= 1080 && options_.fps <= 30;
        if (!status_.hardware_available && !software_allowed) throw hresult_error(MF_E_TOPO_CODEC_NOT_FOUND, L"No H.264 hardware encoder. Software capture is limited to 1080p30 and must be enabled.");
        HANDLE file = CreateFileW(path_.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) throw_last_error();
        CloseHandle(file);
        com_ptr<IMFAttributes> attrs; check_hresult(MFCreateAttributes(attrs.put(), 3));
        check_hresult(attrs->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE));
        // WGC already supplies real-time pacing. Writer throttling blocks the only
        // conversion worker and can keep the shared D3D context busy across frames.
        check_hresult(attrs->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE));
        UINT reset_token{}; check_hresult(MFCreateDXGIDeviceManager(&reset_token, device_manager_.put()));
        check_hresult(device_manager_->ResetDevice(device_.get(), reset_token));
        check_hresult(attrs->SetUnknown(MF_SINK_WRITER_D3D_MANAGER, device_manager_.get()));
        check_hresult(MFCreateSinkWriterFromURL(path_.c_str(), nullptr, attrs.get(), writer_.put()));
        com_ptr<IMFMediaType> output; check_hresult(MFCreateMediaType(output.put()));
        check_hresult(output->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        check_hresult(output->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
        check_hresult(output->SetUINT32(MF_MT_AVG_BITRATE, options_.bitrate));
        check_hresult(output->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
        check_hresult(MFSetAttributeSize(output.get(), MF_MT_FRAME_SIZE, status_.width, status_.height));
        check_hresult(MFSetAttributeRatio(output.get(), MF_MT_FRAME_RATE, options_.fps, 1));
        check_hresult(MFSetAttributeRatio(output.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
        check_hresult(output->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709));
        check_hresult(output->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709));
        check_hresult(output->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709));
        check_hresult(output->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235));
        check_hresult(writer_->AddStream(output.get(), &stream_));
        com_ptr<IMFMediaType> input; check_hresult(MFCreateMediaType(input.put()));
        check_hresult(output->CopyAllItems(input.get())); check_hresult(input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
        check_hresult(writer_->SetInputMediaType(stream_, input.get(), nullptr));
        check_hresult(writer_->BeginWriting());
        // The allocator recycles textures only after the encoder releases the sample.
        // A fixed NV12 surface reused immediately after WriteSample would corrupt
        // asynchronous encoders. Keep a bounded pool, including encoder lookahead.
        bool d3d_encoder = false;
        if (auto extended = writer_.try_as<IMFSinkWriterEx>()) {
            for (DWORD i = 0; i < 16; ++i) {
                GUID category{}; com_ptr<IMFTransform> transform;
                if (FAILED(extended->GetTransformForStream(stream_, i, &category, transform.put()))) break;
                if (category != MFT_CATEGORY_VIDEO_ENCODER) continue;
                com_ptr<IMFAttributes> encoder_attrs; UINT32 aware{};
                d3d_encoder = SUCCEEDED(transform->GetAttributes(encoder_attrs.put())) && SUCCEEDED(encoder_attrs->GetUINT32(MF_SA_D3D11_AWARE, &aware)) && aware;
            }
        }
        if (d3d_encoder) {
            com_ptr<IMFVideoSampleAllocatorEx> allocator;
            com_ptr<IMFAttributes> allocation; check_hresult(MFCreateAttributes(allocation.put(), 2));
            check_hresult(allocation->SetUINT32(MF_SA_D3D11_BINDFLAGS, D3D11_BIND_RENDER_TARGET));
            check_hresult(allocation->SetUINT32(MF_SA_D3D11_USAGE, D3D11_USAGE_DEFAULT));
            HRESULT initialized = MFCreateVideoSampleAllocatorEx(__uuidof(IMFVideoSampleAllocatorEx), allocator.put_void());
            if (SUCCEEDED(initialized)) initialized = allocator->SetDirectXManager(device_manager_.get());
            if (SUCCEEDED(initialized)) initialized = allocator->InitializeSampleAllocatorEx(options_.queue_capacity, 16, allocation.get(), input.get());
            if (SUCCEEDED(initialized)) { sample_allocator_ = std::move(allocator); performance_.gpu_samples = 1; }
        }
        status_.hardware_active = inspect_hardware(hardware_classes) ? 1U : 0U;
        if (!status_.hardware_active && !software_allowed) throw hresult_error(MF_E_TOPO_CODEC_NOT_FOUND,
            L"Hardware encoding was not confirmed. Enable bounded 1080p30 software mode or choose a supported GPU configuration.");
    }
    void on_frame() noexcept {
        std::lock_guard callback_lock(callback_mutex_);
        try {
            auto frame = pool_.TryGetNextFrame(); if (!frame) return;
            auto size = frame.ContentSize();
            if (size.Width != source_size_.Width || size.Height != source_size_.Height) {
                frame.Close(); fail(HRESULT_FROM_WIN32(ERROR_INVALID_STATE), L"Source size changed. Recording ended; completed frames are retained."); return;
            }
            const auto hns = frame.SystemRelativeTime().count();
            const auto qpc = scale_time(hns, hns_per_second, status_.qpc_frequency);
            com_ptr<ID3D11Texture2D> target;
            int64_t mapped_time{};
            {
                std::lock_guard lock(mutex_); ++status_.frames_received;
                if (stopping_ || status_.state == FT_CAPTURE_PAUSED || qpc < resume_cutoff_qpc_) { frame.Close(); return; }
                if (first_hns_ < 0) { first_hns_ = hns; status_.first_frame_qpc = qpc; }
                mapped_time = hns - first_hns_ - scale_time(status_.paused_qpc_ticks, status_.qpc_frequency, hns_per_second);
                // Bucket against session time instead of last callback: jitter near a frame interval must not halve capture rate.
                if (last_accepted_hns_ >= 0 && capture_frame_slot(mapped_time, options_.fps) <= capture_frame_slot(last_accepted_hns_, options_.fps)) {
                    ++status_.frames_rate_limited; frame.Close(); return;
                }
                if (free_.empty()) { ++status_.frames_dropped; frame.Close(); return; }
                target = std::move(free_.back()); free_.pop_back();
            }
            auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            com_ptr<ID3D11Texture2D> source; check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), source.put_void()));
            context_->CopySubresourceRegion(target.get(), 0, 0, 0, 0, source.get(), 0, &crop_);
            context_->Flush();
            // No frame or WGC surface is retained after this point. Only our owned texture enters the queue.
            frame.Close();
            {
                std::lock_guard lock(mutex_);
                if (stopping_) { free_.push_back(std::move(target)); return; }
                last_accepted_hns_ = mapped_time;
                queue_.push_back({ std::move(target), mapped_time });
                status_.queue_depth = static_cast<uint32_t>(queue_.size());
                status_.queue_high_water = std::max(status_.queue_high_water, status_.queue_depth);
            }
            cv_.notify_one();
        } catch (hresult_error const& e) { fail(e.code(), e.message().c_str()); }
        catch (...) { fail(E_FAIL, L"Capture callback failed."); }
    }
    com_ptr<IMFSample> encode_sample(frame_entry const& entry) {
        com_ptr<IMFSample> gpu_sample;
        com_ptr<ID3D11VideoProcessorOutputView> output_view;
        if (sample_allocator_) {
            HRESULT allocated = sample_allocator_->AllocateSample(gpu_sample.put());
            if (allocated == MF_E_SAMPLEALLOCATOR_EMPTY) return {}; // bounded backpressure, never grow without limit
            check_hresult(allocated);
            com_ptr<IMFMediaBuffer> buffer; check_hresult(gpu_sample->GetBufferByIndex(0, buffer.put()));
            DWORD length{}; check_hresult(buffer.as<IMF2DBuffer>()->GetContiguousLength(&length));
            check_hresult(buffer->SetCurrentLength(length));
            auto dxgi = buffer.as<IMFDXGIBuffer>(); com_ptr<ID3D11Texture2D> texture;
            check_hresult(dxgi->GetResource(__uuidof(ID3D11Texture2D), texture.put_void()));
            UINT subresource{}; check_hresult(dxgi->GetSubresourceIndex(&subresource));
            D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC output{};
            output.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2DARRAY;
            output.Texture2DArray.MipSlice = subresource % desc.MipLevels;
            output.Texture2DArray.FirstArraySlice = subresource / desc.MipLevels; output.Texture2DArray.ArraySize = 1;
            check_hresult(video_device_->CreateVideoProcessorOutputView(texture.get(), processor_enum_.get(), &output, output_view.put()));
        }
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC input{}; input.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        com_ptr<ID3D11VideoProcessorInputView> view;
        check_hresult(video_device_->CreateVideoProcessorInputView(entry.texture.get(), processor_enum_.get(), &input, view.put()));
        D3D11_VIDEO_PROCESSOR_STREAM stream{}; stream.Enable = TRUE; stream.pInputSurface = view.get();
        check_hresult(video_context_->VideoProcessorBlt(processor_.get(), output_view ? output_view.get() : nv12_view_.get(), 0, 1, &stream));
        if (gpu_sample) {
            context_->Flush(); // Submit conversion before the encoder consumes the GPU buffer.
            check_hresult(gpu_sample->SetSampleTime(entry.time)); return gpu_sample;
        }
        context_->CopyResource(staging_.get(), nv12_.get());
        D3D11_MAPPED_SUBRESOURCE mapped{}; check_hresult(context_->Map(staging_.get(), 0, D3D11_MAP_READ, 0, &mapped));
        com_ptr<IMFMediaBuffer> buffer;
        const DWORD bytes = status_.width * status_.height * 3 / 2;
        HRESULT create = MFCreateMemoryBuffer(bytes, buffer.put());
        if (FAILED(create)) { context_->Unmap(staging_.get(), 0); check_hresult(create); }
        BYTE* data{}; HRESULT locked = buffer->Lock(&data, nullptr, nullptr);
        if (FAILED(locked)) { context_->Unmap(staging_.get(), 0); check_hresult(locked); }
        auto source = static_cast<BYTE*>(mapped.pData);
        for (UINT row = 0; row < status_.height + status_.height / 2; ++row)
            memcpy(data + row * status_.width, source + row * mapped.RowPitch, status_.width);
        buffer->Unlock(); context_->Unmap(staging_.get(), 0); check_hresult(buffer->SetCurrentLength(bytes));
        com_ptr<IMFSample> sample; check_hresult(MFCreateSample(sample.put())); check_hresult(sample->AddBuffer(buffer.get()));
        check_hresult(sample->SetSampleTime(entry.time));
        return sample;
    }
    void cleanup_capture() noexcept {
        try { if (pool_) pool_.FrameArrived(arrived_token_); } catch (...) {}
        try { if (item_) item_.Closed(closed_token_); } catch (...) {}
        try { if (session_) session_.Close(); } catch (...) {}
        std::lock_guard callback_lock(callback_mutex_);
        try { if (pool_) pool_.Close(); } catch (...) {}
        session_ = nullptr; pool_ = nullptr; item_ = nullptr;
    }
    void worker() noexcept {
        bool signalled = false;
        std::unique_ptr<apartment_scope> apartment;
        std::unique_ptr<mf_scope> media;
        try {
            apartment = std::make_unique<apartment_scope>(); media = std::make_unique<mf_scope>();
            if (!GraphicsCaptureSession::IsSupported()) throw hresult_error(E_NOTIMPL, L"Windows Graphics Capture is unavailable.");
            if (source_hdr(options_.kind, options_.source_handle) != 0) throw hresult_error(E_NOTIMPL, L"HDR or unknown display color mode is unsupported. Use an SDR display.");
            setup_device(); setup_writer();
            auto dxgi = device_.as<IDXGIDevice>(); com_ptr<IInspectable> inspectable;
            check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
            auto direct = inspectable.as<IDirect3DDevice>();
            pool_ = Direct3D11CaptureFramePool::CreateFreeThreaded(direct, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, source_size_);
            session_ = pool_.CreateCaptureSession(item_); session_.IsCursorCaptureEnabled(false);
            arrived_token_ = pool_.FrameArrived([this](auto const&, auto const&) { on_frame(); });
            closed_token_ = item_.Closed([this](auto const&, auto const&) { fail(HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE), L"Capture source closed; completed frames are retained."); });
            { std::lock_guard lock(mutex_); status_.state = FT_CAPTURE_RECORDING; }
            session_.StartCapture(); ready_.set_value(S_OK); signalled = true;
            com_ptr<IMFSample> pending; int64_t pending_time{};
            int64_t health_checked{};
            while (true) {
                frame_entry entry{};
                {
                    std::unique_lock lock(mutex_);
                    cv_.wait_for(lock, std::chrono::milliseconds(250), [this] { return stopping_ || !queue_.empty(); });
                    if (queue_.empty()) {
                        if (stopping_) break;
                        if (!status_.first_frame_qpc && qpc_now() - started_qpc_ > status_.qpc_frequency * 10) {
                            lock.unlock(); throw hresult_error(HRESULT_FROM_WIN32(WAIT_TIMEOUT), L"No capture frame arrived within ten seconds.");
                        }
                        continue;
                    }
                    entry = std::move(queue_.front()); queue_.pop_front(); status_.queue_depth = static_cast<uint32_t>(queue_.size());
                }
                check_hresult(device_->GetDeviceRemovedReason());
                if (qpc_now() - health_checked > status_.qpc_frequency) {
                    if (source_hdr(options_.kind, options_.source_handle) != 0) throw hresult_error(E_NOTIMPL, L"Source moved to HDR/unknown display color mode; recording ended.");
                    health_checked = qpc_now();
                }
                auto convert_start = qpc_now();
                auto current = encode_sample(entry);
                if (!current) {
                    std::lock_guard lock(mutex_); ++status_.frames_dropped; free_.push_back(std::move(entry.texture)); continue;
                }
                auto convert_elapsed = static_cast<uint64_t>(qpc_now() - convert_start);
                { std::lock_guard lock(mutex_); ++performance_.converted; performance_.conversion_qpc += convert_elapsed; performance_.conversion_max_qpc = std::max(performance_.conversion_max_qpc, convert_elapsed); }
                if (pending) {
                    check_hresult(pending->SetSampleDuration(std::max<int64_t>(1, entry.time - pending_time)));
                    auto write_start = qpc_now();
                    check_hresult(writer_->WriteSample(stream_, pending.get()));
                    auto write_elapsed = static_cast<uint64_t>(qpc_now() - write_start);
                    { std::lock_guard lock(mutex_); performance_.writer_qpc += write_elapsed; performance_.writer_max_qpc = std::max(performance_.writer_max_qpc, write_elapsed); }
                    std::lock_guard lock(mutex_); ++status_.frames_written; status_.duration_100ns = entry.time;
                }
                pending = std::move(current); pending_time = entry.time;
                { std::lock_guard lock(mutex_); free_.push_back(std::move(entry.texture)); }
            }
            cleanup_capture();
            if (pending) {
                int64_t end{};
                { std::lock_guard lock(mutex_);
                    auto paused = status_.paused_qpc_ticks + (status_.pause_started_qpc ? stop_qpc_ - status_.pause_started_qpc : 0);
                    end = scale_time(stop_qpc_ - status_.first_frame_qpc - paused, status_.qpc_frequency, hns_per_second);
                }
                end = std::max(end, pending_time + 1);
                check_hresult(pending->SetSampleDuration(end - pending_time));
                check_hresult(writer_->WriteSample(stream_, pending.get()));
                { std::lock_guard lock(mutex_); ++status_.frames_written; status_.duration_100ns = end; }
                pending = nullptr;
            }
            check_hresult(writer_->Finalize()); writer_ = nullptr;
            { std::lock_guard lock(mutex_); if (SUCCEEDED(status_.error_code)) status_.state = FT_CAPTURE_STOPPED; }
        } catch (hresult_error const& e) {
            fail(e.code(), e.message().c_str()); cleanup_capture();
            if (writer_) { writer_->Finalize(); writer_ = nullptr; }
            if (!signalled) { ready_.set_value(e.code()); signalled = true; }
        } catch (...) {
            fail(E_FAIL, L"Recording worker failed."); cleanup_capture();
            if (writer_) { writer_->Finalize(); writer_ = nullptr; }
            if (!signalled) ready_.set_value(E_FAIL);
        }
        if (sample_allocator_) { sample_allocator_->UninitializeSampleAllocator(); sample_allocator_ = nullptr; }
        device_manager_ = nullptr;
    }
    int64_t started_qpc_{};
public:
    explicit capture_run(ft_capture_options const& options) : options_(options), path_(options.output_path) {
        status_.struct_size = sizeof(status_); status_.state = FT_CAPTURE_STARTING; status_.qpc_frequency = qpc_frequency();
    }
    ~capture_run() { stop(); }
    HRESULT start() { auto ready = ready_.get_future(); started_qpc_ = qpc_now(); thread_ = std::thread([this] { worker(); }); return ready.get(); }
    HRESULT stop() noexcept {
        { std::lock_guard lock(mutex_); if (!stopping_) { stopping_ = true; stop_qpc_ = qpc_now(); if (SUCCEEDED(status_.error_code)) status_.state = FT_CAPTURE_FINALIZING; } }
        cv_.notify_all(); if (thread_.joinable()) thread_.join();
        std::lock_guard lock(mutex_); return status_.error_code;
    }
    HRESULT pause(int64_t boundary) {
        std::lock_guard lock(mutex_);
        if (stopping_ || status_.state != FT_CAPTURE_RECORDING || !status_.first_frame_qpc) return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        if (boundary < status_.first_frame_qpc || boundary > qpc_now()) return E_INVALIDARG;
        status_.pause_started_qpc = boundary; status_.state = FT_CAPTURE_PAUSED; return S_OK;
    }
    HRESULT resume(int64_t boundary) {
        std::lock_guard lock(mutex_); if (stopping_ || status_.state != FT_CAPTURE_PAUSED) return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
        if (boundary < status_.pause_started_qpc || boundary > qpc_now()) return E_INVALIDARG;
        resume_cutoff_qpc_ = boundary; status_.paused_qpc_ticks += resume_cutoff_qpc_ - status_.pause_started_qpc;
        status_.pause_started_qpc = 0; status_.state = FT_CAPTURE_RECORDING; return S_OK;
    }
    void status(ft_capture_status& target) const { std::lock_guard lock(mutex_); target = status_; }
    void performance(ft_capture_performance& target) const { std::lock_guard lock(mutex_); target = performance_; }
};
}
extern "C" {
int32_t FT_CAPTURE_CALL ft_capture_get_capabilities(ft_capture_capabilities* c) {
    if (!c || c->struct_size != sizeof(*c)) return E_INVALIDARG;
    return guard([&] { apartment_scope apartment; mf_scope media;
        c->qpc_frequency = qpc_frequency(); c->wgc_supported = GraphicsCaptureSession::IsSupported();
        c->hardware_h264_count = encoder_count(MFT_ENUM_FLAG_HARDWARE);
        c->software_h264_count = encoder_count(MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT); });
}
int32_t FT_CAPTURE_CALL ft_capture_enumerate_sources(ft_capture_source* sources, uint32_t capacity, uint32_t* count) {
    if (!count || (!sources && capacity)) return E_INVALIDARG;
    return guard([&] { auto all = enumerate_sources(); *count = static_cast<uint32_t>(all.size());
        if (capacity < all.size()) { if (!sources && !capacity) return; throw hresult_error(HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER)); }
        std::copy(all.begin(), all.end(), sources); });
}
int32_t FT_CAPTURE_CALL ft_capture_start(ft_capture_options const* o, ft_capture_handle* result) {
    if (result) *result = nullptr;
    if (!o || !result || o->struct_size != sizeof(*o) || !o->output_path || !*o->output_path || !o->source_handle ||
        o->kind < FT_CAPTURE_MONITOR || o->kind > FT_CAPTURE_REGION || (o->fps != 30 && o->fps != 60) ||
        o->queue_capacity < 2 || o->queue_capacity > 8 || o->bitrate < 100000 || o->bitrate > 200000000) return E_INVALIDARG;
    return guard([&] {
        if (std::filesystem::path(o->output_path).extension() != L".mp4") throw hresult_error(E_INVALIDARG, L"Output must use the .mp4 extension.");
        auto run = std::make_unique<capture_run>(*o); HRESULT start = run->start();
        if (FAILED(start)) { ft_capture_status status{}; run->status(status); throw hresult_error(start, status.message); }
        *result = run.release();
    });
}
int32_t FT_CAPTURE_CALL ft_capture_pause_at(ft_capture_handle h, int64_t boundary) { if (!h) return E_INVALIDARG; return guard([&] { check_hresult(static_cast<capture_run*>(h)->pause(boundary)); }); }
int32_t FT_CAPTURE_CALL ft_capture_resume_at(ft_capture_handle h, int64_t boundary) { if (!h) return E_INVALIDARG; return guard([&] { check_hresult(static_cast<capture_run*>(h)->resume(boundary)); }); }
int32_t FT_CAPTURE_CALL ft_capture_pause(ft_capture_handle h) { return ft_capture_pause_at(h, qpc_now()); }
int32_t FT_CAPTURE_CALL ft_capture_resume(ft_capture_handle h) { return ft_capture_resume_at(h, qpc_now()); }
const wchar_t* FT_CAPTURE_CALL ft_capture_last_error(void) { return last_error.c_str(); }
int32_t FT_CAPTURE_CALL ft_capture_stop(ft_capture_handle h) { if (!h) return E_INVALIDARG; return guard([&] { check_hresult(static_cast<capture_run*>(h)->stop()); }); }
int32_t FT_CAPTURE_CALL ft_capture_get_status(ft_capture_handle h, ft_capture_status* s) {
    if (!h || !s || s->struct_size != sizeof(*s)) return E_INVALIDARG;
    return guard([&] { static_cast<capture_run*>(h)->status(*s); });
}
int32_t FT_CAPTURE_CALL ft_capture_get_performance(ft_capture_handle h, ft_capture_performance* p) {
    if (!h || !p || p->struct_size != sizeof(*p)) return E_INVALIDARG;
    return guard([&] { static_cast<capture_run*>(h)->performance(*p); });
}
void FT_CAPTURE_CALL ft_capture_destroy(ft_capture_handle h) { try { delete static_cast<capture_run*>(h); } catch (...) {} }
int32_t FT_CAPTURE_CALL ft_capture_region_from_dip(uint32_t dpi, double x, double y, double w, double h, int32_t mw, int32_t mh, int32_t* result) {
    if (!result || dpi < 48 || dpi > 960 || !std::isfinite(x) || !std::isfinite(y) || !std::isfinite(w) || !std::isfinite(h) || x < 0 || y < 0 || w <= 0 || h <= 0 || mw <= 0 || mh <= 0) return E_INVALIDARG;
    double scale = dpi / 96.0; double left = std::ceil(x * scale), top = std::ceil(y * scale);
    double right = std::floor((x + w) * scale), bottom = std::floor((y + h) * scale);
    if (right > mw || bottom > mh || right - left < 2 || bottom - top < 2) return E_INVALIDARG;
    result[0] = static_cast<int32_t>(left); result[1] = static_cast<int32_t>(top);
    result[2] = static_cast<int32_t>(right - left) & ~1; result[3] = static_cast<int32_t>(bottom - top) & ~1; return S_OK;
}
}
