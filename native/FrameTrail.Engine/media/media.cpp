#include "internal.hpp"
#include <mmsystem.h>
#include <array>

using namespace frametrail::media;
namespace {
std::mutex sessions_mutex; std::unordered_map<uint64_t, std::shared_ptr<Session>> sessions; std::atomic_uint64_t sequence{1};
thread_local std::wstring error;
std::shared_ptr<Session> get(uint64_t handle) {
    std::scoped_lock lock(sessions_mutex); auto it = sessions.find(handle); if (it == sessions.end()) winrt::throw_hresult(E_HANDLE); return it->second;
}
template<class F> int32_t protect(F&& function) noexcept {
    try { Apartment apartment; function(); return S_OK; }
    catch (const winrt::hresult_error& e) { error = e.message().c_str(); return e.code(); }
    catch (const std::exception& e) { error = winrt::to_hstring(e.what()).c_str(); return E_FAIL; }
    catch (...) { error = L"Unexpected media engine error"; return E_FAIL; }
}
}
extern "C" {
int32_t __cdecl ft_media_probe_visual(const wchar_t* path,uint32_t video,uint32_t* width,uint32_t* height,int64_t* duration) {
    return protect([&] {
        if(!path || !width || !height || !duration)winrt::throw_hresult(E_INVALIDARG);
        MfScope runtime;*width=*height=0;*duration=0;
        if(video) {
            VideoReader reader(path,nullptr);*width=reader.width;*height=reader.height;*duration=reader.duration;
            DWORD flags{};ComPtr<IMFSample> sample;FT_MEDIA_CHECK(reader.reader->ReadSample(video_stream_id,0,nullptr,&flags,nullptr,&sample));if(!sample || *duration<=0)throw ValidationError("asset","video has no decodable frame");
        } else {
            ComPtr<IWICImagingFactory> wic;FT_MEDIA_CHECK(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&wic)));
            ComPtr<IWICBitmapDecoder> decoder;FT_MEDIA_CHECK(wic->CreateDecoderFromFilename(path,nullptr,GENERIC_READ,WICDecodeMetadataCacheOnLoad,&decoder));
            ComPtr<IWICBitmapFrameDecode> frame;FT_MEDIA_CHECK(decoder->GetFrame(0,&frame));FT_MEDIA_CHECK(frame->GetSize(width,height));
            if(uint64_t(*width)* *height>16'777'216)throw ValidationError("asset","image exceeds 16 megapixel limit");
            ComPtr<IWICFormatConverter> converter;FT_MEDIA_CHECK(wic->CreateFormatConverter(&converter));FT_MEDIA_CHECK(converter->Initialize(frame.Get(),GUID_WICPixelFormat32bppPBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom));
            WICRect pixel{0,0,1,1};BYTE data[4]{};FT_MEDIA_CHECK(converter->CopyPixels(&pixel,4,4,data));
        }
        if(!*width || !*height || *width>8192 || *height>8192)throw ValidationError("asset","unsupported visual asset dimensions");
    });
}
int32_t __cdecl ft_media_create(const char* json, uint32_t length, const wchar_t* root, ft_media_handle* handle) {
    if (!handle) return E_POINTER; *handle = 0;
    return protect([&] {
        if (!json || !length || length > 16u * 1024u * 1024u || !root) winrt::throw_hresult(E_INVALIDARG);
        auto p = frametrail::core::parse_project(std::string_view(json, length)); auto directory = std::filesystem::absolute(root).lexically_normal();
        if (!std::filesystem::is_directory(directory)) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND));
        for (auto& m : p.media) if (!std::filesystem::is_regular_file(safe_path(directory, m.path))) throw ValidationError("media", "source media missing");
        auto s = std::make_shared<Session>(std::move(p), directory); auto id = sequence.fetch_add(1);
        std::scoped_lock lock(sessions_mutex); sessions.emplace(id, std::move(s)); *handle = id;
    });
}
void __cdecl ft_media_destroy(ft_media_handle handle) {
    protect([&] { std::shared_ptr<Session> s; { std::scoped_lock lock(sessions_mutex); auto it = sessions.find(handle); if (it != sessions.end()) { s = it->second; sessions.erase(it); } } if (s) { s->cancelled = true; s->stop_audio(); } });
}
int32_t __cdecl ft_media_swapchain(ft_media_handle handle, void** output) {
    if (!output) return E_POINTER; *output = nullptr;
    return protect([&] { auto s = get(handle); std::scoped_lock lock(s->mutex); auto& gpu = s->gpu(); s->software = gpu.software; FT_MEDIA_CHECK(gpu.swapchain.CopyTo(__uuidof(IDXGISwapChain1), output)); });
}
int32_t __cdecl ft_media_resize(ft_media_handle handle, uint32_t width, uint32_t height, double sx, double sy) {
    return protect([&] { auto s = get(handle); std::scoped_lock lock(s->mutex); s->gpu().resize(width, height, sx, sy); });
}
int32_t __cdecl ft_media_generation(ft_media_handle handle, uint64_t generation) {
    return protect([&] { auto s = get(handle); auto old = s->generation.load(); while (generation > old && !s->generation.compare_exchange_weak(old, generation)) { } });
}
int32_t __cdecl ft_media_update_effects(ft_media_handle handle,const char* json,uint32_t length,uint64_t generation) {
    return protect([&] {
        if(!json || !length || length>16u*1024u*1024u)winrt::throw_hresult(E_INVALIDARG);
        auto changes=Json::parse(std::string_view(json,length));
        if(!changes.is_object() || changes.size()!=2 || !changes.contains("revision") || !changes.contains("effects"))winrt::throw_hresult(E_INVALIDARG);
        auto s=get(handle);std::scoped_lock lock(s->mutex);s->check_cancel();
        if(generation<s->generation)winrt::throw_hresult(cancelled_hr);
        auto value=Json::parse(serialize_project(s->project));value["revision"]=changes["revision"];value["effects"]=changes["effects"];
        auto project=parse_project(value.dump());FrameSceneEvaluator evaluator(project);
        if(generation<s->generation)winrt::throw_hresult(cancelled_hr);
        s->project=std::move(project);s->evaluator=std::move(evaluator);
        auto old=s->generation.load();while(generation>old && !s->generation.compare_exchange_weak(old,generation)){}
    });
}
int32_t __cdecl ft_media_update_project(ft_media_handle handle,const char* json,uint32_t length,uint64_t generation) {
    return protect([&] {
        if(!json || !length || length>16u*1024u*1024u)winrt::throw_hresult(E_INVALIDARG);
        auto project=parse_project(std::string_view(json,length)); FrameSceneEvaluator evaluator(project);
        auto s=get(handle); std::scoped_lock lock(s->mutex); s->check_cancel();
        if(generation<s->generation)winrt::throw_hresult(cancelled_hr);
        // Cached readers and pointer tracks are valid only while source identity,
        // paths, offsets and formats remain unchanged. Switching projects reloads.
        auto before=Json::parse(serialize_project(s->project)),after=Json::parse(serialize_project(project));
        for(auto key:{"schemaVersion","projectId","sourceDuration","media"})
            if(before.at(key)!=after.at(key))throw ValidationError("preview","source changes require a new media session");
        if(before.value("primarySource",Json{})!=after.value("primarySource",Json{}))throw ValidationError("preview","primary source changed");
        if(s->audio_running)throw ValidationError("preview","stop preview audio before editing");
        if(generation<s->generation)winrt::throw_hresult(cancelled_hr);
        s->project=std::move(project); s->evaluator=std::move(evaluator);
        s->output_duration=s->evaluator.timeline().output_duration();
        if(s->renderer)s->renderer->settings_source=nullptr;
        auto old=s->generation.load();while(generation>old && !s->generation.compare_exchange_weak(old,generation)){}
    });
}
int32_t __cdecl ft_media_render(ft_media_handle handle, int64_t ticks, uint64_t generation, const wchar_t* png) {
    return protect([&] {
        auto s = get(handle); if (generation < s->generation) winrt::throw_hresult(cancelled_hr);
        std::scoped_lock lock(s->mutex); s->check_cancel(); if (generation < s->generation) winrt::throw_hresult(cancelled_hr);
        auto scene = s->evaluator.evaluate(ticks); if (!scene) winrt::throw_hresult(E_INVALIDARG);
        auto& gpu = s->gpu();
        // Preview caps raster resolution without altering authored output settings.
        const double scale = std::min({1.0, 1920.0 / scene->width, 1080.0 / scene->height}); auto w = static_cast<uint32_t>(std::max(2.0, scene->width * scale)), h = static_cast<uint32_t>(std::max(2.0, scene->height * scale));
        gpu.render(s->project, *scene, w, h);
        if (generation < s->generation) winrt::throw_hresult(cancelled_hr);
        if (png && *png) write_png(gpu.device.Get(), gpu.context.Get(), gpu.output.Get(), png);
        gpu.present(); s->rendered_ticks = ticks; s->software = gpu.software; s->cpu_fallback = gpu.fallbacks;
    });
}
int32_t __cdecl ft_media_status_get(ft_media_handle handle, ft_media_status* status) {
    if (!status || status->size != sizeof(*status)) return E_INVALIDARG;
    return protect([&] { auto s = get(handle); *status = {sizeof(*status), s->stage, s->completed, s->total, s->last_progress, s->output_duration, s->rendered_ticks, s->audio_ticks, s->software, s->cpu_fallback, s->audio_running}; });
}
int32_t __cdecl ft_media_cancel(ft_media_handle handle) { return protect([&] { auto s = get(handle); s->cancelled = true; }); }
int32_t __cdecl ft_media_export(ft_media_handle handle, const wchar_t* target, uint32_t audio_only) {
    auto result = protect([&] { if (!target) winrt::throw_hresult(E_INVALIDARG); auto s = get(handle); std::scoped_lock lock(s->mutex); s->stage = 1; s->export_file(target, audio_only != 0); });
    if (result < 0) { try { get(handle)->stage = result == cancelled_hr ? 5 : 6; } catch (...) { } } return result;
}
int32_t __cdecl ft_media_audio_prepare(ft_media_handle handle, const wchar_t* wav) {
    return protect([&] { if (!wav) winrt::throw_hresult(E_INVALIDARG); auto s = get(handle); std::scoped_lock lock(s->mutex); s->mix_audio(wav); });
}
int32_t __cdecl ft_media_audio_stop(ft_media_handle handle) { return protect([&] { get(handle)->stop_audio(); }); }
int32_t __cdecl ft_media_audio_play(ft_media_handle handle, const wchar_t* wav, int64_t ticks) {
    return protect([&] {
        if (!wav || ticks < 0) winrt::throw_hresult(E_INVALIDARG); auto s = get(handle); s->stop_audio();
        std::ifstream file(wav, std::ios::binary); std::array<char, 44> header{}; file.read(header.data(), header.size());
        if (!file || memcmp(header.data(), "RIFF", 4) || memcmp(header.data() + 8, "WAVEfmt ", 8) || memcmp(header.data() + 36, "data", 4)) throw ValidationError("audio", "invalid prepared PCM cache");
        file.seekg(44 + ticks_samples(ticks) * channels * 2); if (!file) throw ValidationError("audio", "seek outside prepared audio");
        WAVEFORMATEX format{WAVE_FORMAT_PCM, channels, sample_rate, sample_rate * channels * 2, channels * 2, 16, 0}; HWAVEOUT device{};
        const auto result = waveOutOpen(&device, WAVE_MAPPER, &format, 0, 0, CALLBACK_NULL); if (result != MMSYSERR_NOERROR) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED));
        std::scoped_lock lock(s->audio_mutex); s->audio_stop = false; s->audio_ticks = ticks; s->audio_running = 1;
        try {
            s->audio_thread = std::thread([state = s.get(), file = std::move(file), device, ticks]() mutable {
                struct Block { WAVEHDR header{}; std::array<char, 2048 * channels * 2> data{}; bool prepared{}; };
                std::array<Block, 3> blocks{}; bool eof = false; int64_t frames = 0;
                while (!state->audio_stop) {
                    bool pending = false;
                    for (auto& block : blocks) {
                        if (block.prepared && !(block.header.dwFlags & WHDR_DONE)) { pending = true; continue; }
                        if (block.prepared) { waveOutUnprepareHeader(device, &block.header, sizeof(WAVEHDR)); block.prepared = false; }
                        if (eof) continue;
                        file.read(block.data.data(), block.data.size()); auto n = file.gcount(); if (!n) { eof = true; continue; }
                        block.header = {}; block.header.lpData = block.data.data(); block.header.dwBufferLength = static_cast<DWORD>(n);
                        if (waveOutPrepareHeader(device, &block.header, sizeof(WAVEHDR)) != MMSYSERR_NOERROR) { eof = true; continue; }
                        block.prepared = true;
                        if (waveOutWrite(device, &block.header, sizeof(WAVEHDR)) != MMSYSERR_NOERROR) { eof = true; continue; }
                        frames += n / (channels * 2); pending = true;
                    }
                    MMTIME position{}; position.wType = TIME_SAMPLES;
                    if (waveOutGetPosition(device, &position, sizeof(position)) == MMSYSERR_NOERROR && position.wType == TIME_SAMPLES) state->audio_ticks = ticks + sample_ticks(position.u.sample);
                    if (eof && !pending) { state->audio_ticks = ticks + sample_ticks(frames); break; } Sleep(3);
                }
                waveOutReset(device); for (auto& block : blocks) if (block.prepared) waveOutUnprepareHeader(device, &block.header, sizeof(WAVEHDR)); waveOutClose(device); state->audio_running = 0;
            });
        } catch (...) { waveOutClose(device); s->audio_running = 0; throw; }
    });
}
int32_t __cdecl ft_media_error(wchar_t* output, uint32_t capacity) {
    if (!output || !capacity) return E_INVALIDARG; wcsncpy_s(output, capacity, error.c_str(), _TRUNCATE); return S_OK;
}
}
