#include "internal.hpp"
#include "frametrail/core/cursor.hpp"
#include "frametrail/core/mockup.hpp"
#include "subtitle_renderer.hpp"
#include "visual_gpu.hpp"
#include "image_decode.hpp"
#include <d2d1_1helper.h>
#include <algorithm>
#include <cmath>

namespace frametrail::media {
namespace {
// Evict the least-recently-used entry before inserting into a bounded cache.
template <typename CacheMap, typename LruMap>
void evict_lru_entry(CacheMap& cache, LruMap& lru, size_t max_entries) {
    while (cache.size() >= max_entries && !lru.empty()) {
        auto victim = std::min_element(lru.begin(), lru.end(),
            [](const auto& a, const auto& b) { return a.second < b.second; });
        cache.erase(victim->first);
        lru.erase(victim);
    }
    if (cache.size() >= max_entries) cache.clear();
}
ComPtr<IDWriteTextLayout> create_text_layout(
    IDWriteFactory* text_factory,
    const std::wstring& text,
    const wchar_t* font_family,
    DWRITE_FONT_WEIGHT weight,
    float font_size,
    float max_width,
    float max_height,
    DWRITE_TEXT_ALIGNMENT align,
    DWRITE_PARAGRAPH_ALIGNMENT valign,
    DWRITE_TEXT_METRICS& metrics) {
    ComPtr<IDWriteTextFormat> format;
    FT_MEDIA_CHECK(text_factory->CreateTextFormat(
        font_family, nullptr, weight, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
        std::max(8.0f, font_size), L"zh-CN", &format));
    FT_MEDIA_CHECK(format->SetTextAlignment(align));
    FT_MEDIA_CHECK(format->SetParagraphAlignment(valign));
    ComPtr<IDWriteTextLayout> layout;
    FT_MEDIA_CHECK(text_factory->CreateTextLayout(
        text.c_str(), static_cast<UINT32>(text.size()), format.Get(),
        std::max(1.0f, max_width), std::max(1.0f, max_height), &layout));
    FT_MEDIA_CHECK(layout->GetMetrics(&metrics));
    return layout;
}
}
Renderer::Renderer(const std::filesystem::path& path) : root(path) {
    FT_MEDIA_CHECK(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,__uuidof(IDWriteFactory),reinterpret_cast<IUnknown**>(text_factory.GetAddressOf())));
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    auto hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context);
    if (FAILED(hr)) { software = 1; FT_MEDIA_CHECK(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context)); }
    ComPtr<ID3D10Multithread> multithread; if (SUCCEEDED(context.As(&multithread))) multithread->SetMultithreadProtected(TRUE);
    UINT token{}; FT_MEDIA_CHECK(MFCreateDXGIDeviceManager(&token, &manager)); FT_MEDIA_CHECK(manager->ResetDevice(device.Get(), token));
    FT_MEDIA_CHECK(D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED, IID_PPV_ARGS(&factory)));
    ComPtr<IDXGIDevice> dxgi; FT_MEDIA_CHECK(device.As(&dxgi)); FT_MEDIA_CHECK(factory->CreateDevice(dxgi.Get(), &d2device)); FT_MEDIA_CHECK(d2device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &d2));
    ComPtr<IDXGIAdapter> adapter; FT_MEDIA_CHECK(dxgi->GetAdapter(&adapter)); ComPtr<IDXGIFactory2> dxgifactory; FT_MEDIA_CHECK(adapter->GetParent(IID_PPV_ARGS(&dxgifactory)));
    DXGI_SWAP_CHAIN_DESC1 desc{}; desc.Width = desc.Height = 2; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2; desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    FT_MEDIA_CHECK(dxgifactory->CreateSwapChainForComposition(device.Get(), &desc, nullptr, &swapchain));
}
Renderer::~Renderer() = default;
void Renderer::resize(uint32_t w, uint32_t h, double sx, double sy) {
    if (!w || !h || w > 8192 || h > 8192 || !std::isfinite(sx) || !std::isfinite(sy) || sx <= 0 || sy <= 0) winrt::throw_hresult(E_INVALIDARG);
    d2->SetTarget(nullptr); FT_MEDIA_CHECK(swapchain->ResizeBuffers(2, w, h, DXGI_FORMAT_B8G8R8A8_UNORM, 0));
    ComPtr<IDXGISwapChain2> chain; FT_MEDIA_CHECK(swapchain.As(&chain)); DXGI_MATRIX_3X2_F matrix{static_cast<float>(1 / sx), 0, 0, static_cast<float>(1 / sy), 0, 0};
    FT_MEDIA_CHECK(chain->SetMatrixTransform(&matrix));
    viewport_width = w; viewport_height = h;
}
ComPtr<ID2D1Bitmap1> Renderer::bitmap(ID3D11Texture2D* texture, bool target, bool alpha) {
    ComPtr<IDXGISurface> surface; FT_MEDIA_CHECK(texture->QueryInterface(IID_PPV_ARGS(&surface))); ComPtr<ID2D1Bitmap1> result;
    D3D11_TEXTURE2D_DESC descriptor{}; texture->GetDesc(&descriptor);
    auto options = target ? D2D1_BITMAP_OPTIONS_TARGET : D2D1_BITMAP_OPTIONS_NONE;
    if (target && !(descriptor.BindFlags & D3D11_BIND_SHADER_RESOURCE)) options = options | D2D1_BITMAP_OPTIONS_CANNOT_DRAW;
    auto props = D2D1::BitmapProperties1(options, D2D1::PixelFormat(DXGI_FORMAT_UNKNOWN, alpha ? D2D1_ALPHA_MODE_PREMULTIPLIED : D2D1_ALPHA_MODE_IGNORE));
    auto hr = d2->CreateBitmapFromDxgiSurface(surface.Get(), &props, &result);
    if (FAILED(hr)) { D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc); throw winrt::hresult_error(hr, L"D2D bitmap: format=" + std::to_wstring(desc.Format) + L", target=" + std::to_wstring(target) + L", bind=" + std::to_wstring(desc.BindFlags)); }
    return result;
}
ComPtr<ID2D1Bitmap1> Renderer::image(const std::filesystem::path& path) {
    auto key = path.wstring();
    if (images.contains(key)) {
        image_lru[key] = ++cache_clock;
        return images.at(key);
    }
    ComPtr<IWICImagingFactory> wic; FT_MEDIA_CHECK(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic)));
    ComPtr<IWICBitmapDecoder> decoder; FT_MEDIA_CHECK(wic->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad, &decoder));
    ComPtr<IWICBitmapFrameDecode> frame; FT_MEDIA_CHECK(decoder->GetFrame(0, &frame));
    UINT w{}, h{}; FT_MEDIA_CHECK(frame->GetSize(&w, &h)); auto size = image_decode_size(w, h);
    if (!size) throw ValidationError("image", "unsupported image dimensions");
    ComPtr<IWICBitmapSource> source; FT_MEDIA_CHECK(frame.As(&source));
    if (size->width != w || size->height != h) {
        ComPtr<IWICBitmapScaler> scaler; FT_MEDIA_CHECK(wic->CreateBitmapScaler(&scaler));
        FT_MEDIA_CHECK(scaler->Initialize(frame.Get(), size->width, size->height, WICBitmapInterpolationModeFant));
        FT_MEDIA_CHECK(scaler.As(&source));
    }
    ComPtr<IWICFormatConverter> converter; FT_MEDIA_CHECK(wic->CreateFormatConverter(&converter)); FT_MEDIA_CHECK(converter->Initialize(source.Get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom));
    ComPtr<ID2D1Bitmap1> result; FT_MEDIA_CHECK(d2->CreateBitmapFromWicBitmap(converter.Get(), nullptr, &result));
    evict_lru_entry(images, image_lru, 16);
    images[key] = result;
    image_lru[key] = ++cache_clock;
    return result;
}
void Renderer::layer(const Project& p, const MediaFrame& frame, float radius, bool shadow, const Json& settings, bool screen, const VisualScene& visual) {
    const auto asset = std::find_if(p.media.begin(), p.media.end(), [&](auto& m) { return m.id == frame.media_id; });
    if (asset == p.media.end()) throw ValidationError("scene", "missing media ID");
    if (!readers.contains(asset->id)) {
        evict_lru_entry(readers, reader_lru, 8);
        readers[asset->id] = std::make_unique<VideoReader>(safe_path(root, asset->path), manager.Get());
    }
    reader_lru[asset->id] = ++cache_clock;
    auto& reader = *readers.at(asset->id); auto texture = reader.frame(frame.local_ticks, device.Get(), context.Get());
    if (reader.cpu) fallbacks = 1;
    Rect crop{};
    if(screen && settings.contains("crop")) {auto c=settings["crop"];crop={c["x"].get<double>(),c["y"].get<double>(),c["width"].get<double>(),c["height"].get<double>()};}
    if(screen) {
        std::vector<VisualEffect> regions;for(auto&e:visual.regions)if(e.attachment=="screen")regions.push_back(e);
        if(!regions.empty()){if(!visuals)visuals=std::make_unique<VisualGpu>(*this);texture=visuals->process(texture.Get(),regions,crop,false);}
    }
    auto source = bitmap(texture.Get()); const auto r = frame.output_rect;
    const auto rect = D2D1::RectF(static_cast<float>(r.x), static_cast<float>(r.y), static_cast<float>(r.x + r.width), static_cast<float>(r.y + r.height));
    radius = std::min(radius, static_cast<float>(std::min(r.width, r.height) / 2));
    if(!screen) {if(visual.camera_shape=="circle")radius=float(std::min(r.width,r.height)/2);else if(visual.camera_shape=="rectangle")radius=0;}
    ComPtr<ID2D1SolidColorBrush> brush; FT_MEDIA_CHECK(d2->CreateSolidColorBrush(D2D1::ColorF(0, 0, 0, 1), &brush));
    auto shadow_rect=rect;float shadow_radius=radius;
    if(screen && visual.frame_style!="none") {
        auto f=frametrail::core::frame_decoration(visual.frame_style,settings.value("frameScale",1.0),settings.value("frameToolbarScale",1.0),settings.value("frameOuterCornerRadius",-1.0),
            settings.value("cornerRadius",0.0),std::min(p.canvas.width,p.canvas.height),r.width,r.height);
        radius=float(f.content_radius);shadow_radius=float(f.outer_radius);
        shadow_rect=D2D1::RectF(rect.left-float(f.inset),rect.top-float(f.top),rect.right+float(f.inset),rect.bottom+float(f.inset));
    }
    if (shadow) for (int i = 12; i >= 1; --i) {
        const float spread = i * 1.7f; brush->SetOpacity(static_cast<float>(settings.value("shadow", 0.35)) * 0.022f);
        auto sr = D2D1::RoundedRect(D2D1::RectF(shadow_rect.left - spread, shadow_rect.top - spread + 6, shadow_rect.right + spread, shadow_rect.bottom + spread + 6), shadow_radius + spread, shadow_radius + spread);
        d2->FillRoundedRectangle(sr, brush.Get());
    }
    if(screen && visual.frame_style!="none") {
        frame_chrome(rect,visual.frame_style,std::min(p.canvas.width,p.canvas.height),settings);
    }
    ComPtr<ID2D1RoundedRectangleGeometry> geometry; auto rounded = D2D1::RoundedRect(rect, radius, radius); FT_MEDIA_CHECK(factory->CreateRoundedRectangleGeometry(rounded, &geometry));
    d2->PushLayer(D2D1::LayerParameters(D2D1::InfiniteRect(), geometry.Get()), nullptr);
    D2D1_MATRIX_3X2_F original; d2->GetTransform(&original);
    if (frame.mirror) d2->SetTransform(D2D1::Matrix3x2F::Scale(-1, 1, D2D1::Point2F(static_cast<float>(r.x + r.width / 2), static_cast<float>(r.y + r.height / 2))) * original);
    auto src = D2D1::RectF(0, 0, static_cast<float>(asset->width), static_cast<float>(asset->height));
    if (screen && settings.contains("crop")) { auto c = settings["crop"]; src = D2D1::RectF(c["x"].get<float>() * asset->width, c["y"].get<float>() * asset->height, (c["x"].get<float>() + c["width"].get<float>()) * asset->width, (c["y"].get<float>() + c["height"].get<float>()) * asset->height); }
    if(!screen && visual.camera_shape=="circle") {float side=float(std::min(asset->width,asset->height));src=D2D1::RectF((asset->width-side)/2,(asset->height-side)/2,(asset->width+side)/2,(asset->height+side)/2);}
    d2->DrawBitmap(source.Get(), rect, 1, D2D1_INTERPOLATION_MODE_LINEAR, src); d2->SetTransform(original);
    const bool show_cursor = settings.value("cursorScale", 1.0) > 0;
    const bool show_keystroke_hud = settings.value("keystrokeHud", true);
    if (screen && (show_cursor || show_keystroke_hud)) {
        const auto source_ticks = frame.local_ticks + normalized_ticks(asset->start_offset);
        const auto offset = static_cast<int64_t>(std::llround(settings.value("cursorTimeOffset", 0.0) * time_scale));
        if (const auto window = pointer_window(p, source_ticks, offset)) {
            // Retain only this bounded history. Insertion may rehash the map,
            // but references remain valid until the next frame evicts a file.
            std::erase_if(pointers, [&](const auto& entry) {
                return std::none_of(window->media.begin(), window->media.end(), [&](auto media) { return media->id == entry.first; });
            });
            std::erase_if(pointer_keys, [&](const auto& entry) {
                return std::none_of(window->media.begin(), window->media.end(), [&](auto media) { return media->id == entry.first; });
            });
            auto load = [&](const Media& media) -> const PointerTrack& {
                if (pointers.contains(media.id)) return pointers.at(media.id);
                size_t retained_samples = 0;
                for (const auto& entry : pointers) retained_samples += entry.second.points.size();
                PointerTrack track;
                std::vector<KeyEvent> keys;
                std::ifstream input(safe_path(root, media.path)); std::string line;
                if (!input) throw ValidationError("pointer", "pointer file unavailable");
                while (std::getline(input, line)) {
                    if (line.size() > 32768 || track.points.size() + retained_samples >= maximum_cached_pointer_samples)
                        throw ValidationError("pointer", "pointer cache limit exceeded");
                    auto event = Json::parse(line);
                    const auto type = event.value("type", std::string{});
                    if (type == "shape") track.shapes[event.at("id").get<uint32_t>()] = {event.at("file").get<std::string>(), event.value("hotspotX", 0.0f), event.value("hotspotY", 0.0f)};
                    else if (type == "pointer") {
                        auto t = normalized_ticks({event.at("ticks").get<int64_t>(), event.at("timescale").get<int32_t>()});
                        float x = event.at("x").get<float>(), y = event.at("y").get<float>();
                        if (!std::isfinite(x) || !std::isfinite(y) || x < 0 || x > 1 || y < 0 || y > 1) throw ValidationError("pointer", "invalid coordinates");
                        track.points.push_back({t, x, y, event.value("shape", 0u), event.value("visible", false) && event.value("inside", false), event.value("kind", 0u)});
                    } else if (type == "key") {
                        auto t = normalized_ticks({event.at("ticks").get<int64_t>(), event.at("timescale").get<int32_t>()});
                        auto label = event.value("label", std::string{});
                        if (!label.empty() && label.size() <= 128 && keys.size() < 65536)
                            keys.push_back({t, std::move(label)});
                    }
                }
                std::stable_sort(track.points.begin(), track.points.end(), [](auto& a, auto& b) { return a.ticks < b.ticks; });
                std::stable_sort(keys.begin(), keys.end(), [](auto& a, auto& b) { return a.ticks < b.ticks; });
                pointer_keys.emplace(media.id, std::move(keys));
                return pointers.emplace(media.id, std::move(track)).first->second;
            };
            const auto cursor_ticks = window->ticks;
            if (show_cursor) {
                const double smoothing = settings.value("cursorSmoothing", 0.08);
                const bool hide_idle = settings.value("cursorHideWhenIdle", false);
                const double idle_delay = settings.value("cursorIdleDelay", 1.2);
                const auto sampled = sample_pointer_window(*window, load, smoothing, hide_idle, idle_delay);
                if (sampled) {
                    const auto& track = *sampled->latest.track;
                    const auto& m = *sampled->latest.media;
                    const float x = rect.left + (float(sampled->x) * asset->width - src.left) / (src.right - src.left) * (rect.right - rect.left);
                    const float y = rect.top + (float(sampled->y) * asset->height - src.top) / (src.bottom - src.top) * (rect.bottom - rect.top);
                    auto found = track.shapes.find(sampled->latest.point.shape); const auto scale = static_cast<float>(settings.value("cursorScale", 1.0)) * (rect.right - rect.left) / (src.right - src.left);
                    const auto opacity = static_cast<float>(sampled->opacity);
                    const auto click_style=settings.value("cursorClickStyle",std::string("none"));
                    if(click_style!="none")for(auto click=sampled->clicks.rbegin();click!=sampled->clicks.rend();++click) {
                        auto progress=static_cast<float>(cursor_click_progress(cursor_ticks,click->ticks));
                        float cx=rect.left+(click->x*asset->width-src.left)/(src.right-src.left)*(rect.right-rect.left);
                        float cy=rect.top+(click->y*asset->height-src.top)/(src.bottom-src.top)*(rect.bottom-rect.top);
                        auto color=settings.value("cursorClickColor",Json{{"r",0.35},{"g",0.68},{"b",1.0},{"a",1.0}});
                        brush->SetColor(D2D1::ColorF(color["r"].get<float>(),color["g"].get<float>(),color["b"].get<float>(),color.value("a",1.0f)));
                        brush->SetOpacity(static_cast<float>(settings.value("cursorClickOpacity",0.8))*(1-progress));
                        float click_radius=(click_style=="ripple"?8+22*progress:18)*scale*static_cast<float>(settings.value("cursorClickScale",1.0));
                        auto ellipse=D2D1::Ellipse(D2D1::Point2F(cx,cy),click_radius,click_radius);
                        if(click_style=="ripple")d2->DrawEllipse(ellipse,brush.Get(),std::max(1.0f,2*scale));else d2->FillEllipse(ellipse,brush.Get());
                    }
                    auto style=settings.value("cursorStyle",std::string("recorded"));
                    auto draw_cursor = [&](float cx, float cy, float alpha_val) {
                        if (alpha_val <= 0.002f) return;
                        if(style=="dot" || style=="crosshair") {
                            brush->SetOpacity(alpha_val); brush->SetColor(D2D1::ColorF(D2D1::ColorF::White));
                            if(style=="dot") {auto circle=D2D1::Ellipse(D2D1::Point2F(cx,cy),5*scale,5*scale);d2->FillEllipse(circle,brush.Get());brush->SetColor(D2D1::ColorF(D2D1::ColorF::Black));d2->DrawEllipse(circle,brush.Get(),scale);}
                            else {d2->DrawLine(D2D1::Point2F(cx-9*scale,cy),D2D1::Point2F(cx+9*scale,cy),brush.Get(),3*scale);d2->DrawLine(D2D1::Point2F(cx,cy-9*scale),D2D1::Point2F(cx,cy+9*scale),brush.Get(),3*scale);brush->SetColor(D2D1::ColorF(D2D1::ColorF::Black));d2->DrawLine(D2D1::Point2F(cx-9*scale,cy),D2D1::Point2F(cx+9*scale,cy),brush.Get(),scale);d2->DrawLine(D2D1::Point2F(cx,cy-9*scale),D2D1::Point2F(cx,cy+9*scale),brush.Get(),scale);}
                        } else if (style=="recorded" && found != track.shapes.end()) {
                            auto rel = winrt::to_string((std::filesystem::path(winrt::to_hstring(m.path).c_str()).parent_path() / std::filesystem::path(winrt::to_hstring(found->second.file).c_str())).generic_wstring());
                            auto icon = image(safe_path(root, rel)); auto size = icon->GetSize(); auto shape = found->second;
                            auto dest = D2D1::RectF(cx - shape.x * scale, cy - shape.y * scale, cx + (size.width - shape.x) * scale, cy + (size.height - shape.y) * scale);
                            d2->DrawBitmap(icon.Get(), dest, alpha_val);
                        } else {
                            // Explicit default pointer when capture could not persist a shape.
                            ComPtr<ID2D1PathGeometry> arrow; FT_MEDIA_CHECK(factory->CreatePathGeometry(&arrow)); ComPtr<ID2D1GeometrySink> sink; FT_MEDIA_CHECK(arrow->Open(&sink));
                            sink->BeginFigure(D2D1::Point2F(cx, cy), D2D1_FIGURE_BEGIN_FILLED);
                            sink->AddLine(D2D1::Point2F(cx + 2 * scale, cy + 20 * scale)); sink->AddLine(D2D1::Point2F(cx + 7 * scale, cy + 15 * scale)); sink->AddLine(D2D1::Point2F(cx + 14 * scale, cy + 15 * scale)); sink->EndFigure(D2D1_FIGURE_END_CLOSED); FT_MEDIA_CHECK(sink->Close());
                            brush->SetOpacity(alpha_val); brush->SetColor(D2D1::ColorF(D2D1::ColorF::White)); d2->FillGeometry(arrow.Get(), brush.Get()); brush->SetColor(D2D1::ColorF(D2D1::ColorF::Black)); d2->DrawGeometry(arrow.Get(), brush.Get(), scale);
                        }
                    };
                    if (const double mb = std::clamp(settings.value("motionBlur", 0.0), 0.0, 1.0); mb > 0.01) {
                        if (const auto prev_window = pointer_window(p, (std::max<int64_t>)(0, source_ticks - time_scale / 60), offset)) {
                            if (const auto prev_sampled = sample_pointer_window(*prev_window, load, smoothing, hide_idle, idle_delay)) {
                                const double dx = sampled->x - prev_sampled->x;
                                const double dy = sampled->y - prev_sampled->y;
                                if (std::hypot(dx, dy) > 0.008) {
                                    constexpr std::array<std::pair<double, float>, 2> ghosts{{{0.70, 0.16f}, {0.35, 0.28f}}};
                                    for (const auto [frac, ghost_alpha] : ghosts) {
                                        const float gx = rect.left + (float(sampled->x - dx * frac * mb) * asset->width - src.left) / (src.right - src.left) * (rect.right - rect.left);
                                        const float gy = rect.top + (float(sampled->y - dy * frac * mb) * asset->height - src.top) / (src.bottom - src.top) * (rect.bottom - rect.top);
                                        draw_cursor(gx, gy, opacity * ghost_alpha * static_cast<float>(mb));
                                    }
                                }
                            }
                        }
                    }
                    draw_cursor(x, y, opacity);
                }
            }
            if (show_keystroke_hud) {
                for (const auto* media : window->media) (void)load(*media);
                const int64_t hud_window_ticks = 14'000'000;
                const int64_t min_key_ticks = std::max(window->begin, cursor_ticks - hud_window_ticks);
                int64_t best_ticks = -1;
                std::string best_label;
                for (const auto* media : window->media) {
                    const auto found_keys = pointer_keys.find(media->id);
                    if (found_keys == pointer_keys.end()) continue;
                    const auto m_start = normalized_ticks(media->start_offset);
                    const auto m_dur = normalized_ticks(media->duration);
                    for (auto it = found_keys->second.rbegin(); it != found_keys->second.rend(); ++it) {
                        if (it->ticks < 0 || it->ticks >= m_dur) continue;
                        const int64_t abs_ticks = m_start + it->ticks;
                        if (abs_ticks > cursor_ticks) continue;
                        if (abs_ticks < min_key_ticks) break;
                        if (abs_ticks > best_ticks) {
                            best_ticks = abs_ticks;
                            best_label = it->label;
                        }
                        break;
                    }
                }
                if (best_ticks >= 0 && !best_label.empty()) {
                    const double age_sec = static_cast<double>(cursor_ticks - best_ticks) / static_cast<double>(time_scale);
                    float hud_alpha = 1.0f;
                    if (age_sec < 0.12) hud_alpha = static_cast<float>(age_sec / 0.12);
                    else if (age_sec > 1.4 - 0.28) hud_alpha = static_cast<float>((1.4 - age_sec) / 0.28);
                    hud_alpha = std::clamp(hud_alpha, 0.0f, 1.0f);
                    if (hud_alpha > 0.005f) {
                        const float rect_w = rect.right - rect.left;
                        const float rect_h = rect.bottom - rect.top;
                        const float font_size = std::clamp(rect_h * 0.032f, 13.0f, 34.0f);
                        const auto wlabel = winrt::to_hstring(best_label);
                        DWRITE_TEXT_METRICS metrics{};
                        auto layout = create_text_layout(
                            text_factory.Get(), std::wstring(wlabel.c_str()), L"Segoe UI", DWRITE_FONT_WEIGHT_SEMI_BOLD,
                            font_size, std::max(80.0f, rect_w * 0.72f), font_size * 2.0f,
                            DWRITE_TEXT_ALIGNMENT_LEADING, DWRITE_PARAGRAPH_ALIGNMENT_NEAR, metrics);
                        const float pad_x = font_size * 0.85f;
                        const float pad_y = font_size * 0.40f;
                        const float cap_w = metrics.width + pad_x * 2.0f;
                        const float cap_h = metrics.height + pad_y * 2.0f;
                        const float cx = (rect.left + rect.right) * 0.5f;
                        const float bottom = rect.bottom - std::max(20.0f, rect_h * 0.055f);
                        const auto cap_rect = D2D1::RectF(cx - cap_w * 0.5f, bottom - cap_h, cx + cap_w * 0.5f, bottom);
                        const float cap_radius = std::min(14.0f, cap_h * 0.35f);
                        brush->SetOpacity(1.0f);
                        brush->SetColor(D2D1::ColorF(0.06f, 0.07f, 0.10f, 0.88f * hud_alpha));
                        d2->FillRoundedRectangle(D2D1::RoundedRect(cap_rect, cap_radius, cap_radius), brush.Get());
                        brush->SetColor(D2D1::ColorF(1.0f, 1.0f, 1.0f, 0.22f * hud_alpha));
                        d2->DrawRoundedRectangle(D2D1::RoundedRect(cap_rect, cap_radius, cap_radius), brush.Get(), std::max(1.0f, font_size * 0.075f));
                        brush->SetColor(D2D1::ColorF(1.0f, 1.0f, 1.0f, hud_alpha));
                        d2->DrawTextLayout(D2D1::Point2F(cap_rect.left + pad_x - metrics.left, cap_rect.top + pad_y - metrics.top), layout.Get(), brush.Get());
                    }
                }
            }
        }
    }
    d2->PopLayer();
}
void Renderer::ensure_surface(ComPtr<ID3D11Texture2D>& texture,uint32_t w,uint32_t h) {
    D3D11_TEXTURE2D_DESC desc{};if(texture){texture->GetDesc(&desc);if(desc.Width==w && desc.Height==h)return;texture.Reset();}
    desc={};desc.Width=w;desc.Height=h;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
    FT_MEDIA_CHECK(device->CreateTexture2D(&desc,nullptr,&texture));
}
void Renderer::background(const Project&,const FrameScene& scene) {
    auto b=cached_settings.value("backdrop",Json::object());auto kind=b.value("kind",std::string("solid"));
    if(kind=="solid")return;
    auto rect=D2D1::RectF(0,0,float(scene.width),float(scene.height));
    if(kind=="gradient") {
        auto end=b.value("secondary",Json{{"r",0.16},{"g",0.25},{"b",0.42},{"a",1}});
        D2D1_GRADIENT_STOP stops[]{{0,D2D1::ColorF(float(scene.background.r),float(scene.background.g),float(scene.background.b),1)},{1,D2D1::ColorF(end.value("r",0.16f),end.value("g",0.25f),end.value("b",0.42f),1)}};
        ComPtr<ID2D1GradientStopCollection> collection;FT_MEDIA_CHECK(d2->CreateGradientStopCollection(stops,2,&collection));
        ComPtr<ID2D1LinearGradientBrush> brush;FT_MEDIA_CHECK(d2->CreateLinearGradientBrush(D2D1::LinearGradientBrushProperties(D2D1::Point2F(0,0),D2D1::Point2F(float(scene.width),float(scene.height))),collection.Get(),&brush));d2->FillRectangle(rect,brush.Get());return;
    }
    auto path=b.at("path").get<std::string>();ComPtr<ID2D1Bitmap1> source;
    if(kind=="image")source=image(safe_path(root,path));
    else {
        if(!backdrop_reader || backdrop_path!=path){backdrop_reader=std::make_unique<VideoReader>(safe_path(root,path),manager.Get());backdrop_path=path;}
        auto duration=std::max(int64_t(1),backdrop_reader->duration);auto texture=backdrop_reader->frame(scene.output_ticks%duration,device.Get(),context.Get());source=bitmap(texture.Get());
    }
    auto size=source->GetSize();float scale=std::max(scene.width/size.width,scene.height/size.height);
    auto crop=D2D1::RectF((size.width-scene.width/scale)/2,(size.height-scene.height/scale)/2,(size.width+scene.width/scale)/2,(size.height+scene.height/scale)/2);d2->DrawBitmap(source.Get(),rect,1,D2D1_INTERPOLATION_MODE_LINEAR,crop);
}
void Renderer::stickers(const FrameScene& scene,bool attached) {
    const float min_dim = static_cast<float>(std::min(scene.width, scene.height));
    for(auto&e:scene.visual.stickers) {
        if((e.attachment=="screen")!=attached)continue;
        auto base=attached?scene.screen.output_rect:Rect{0,0,double(scene.width),double(scene.height)};
        auto rect=D2D1::RectF(float(base.x+e.rect.x*base.width),float(base.y+e.rect.y*base.height),float(base.x+(e.rect.x+e.rect.width)*base.width),float(base.y+(e.rect.y+e.rect.height)*base.height));
        D2D1_MATRIX_3X2_F original;d2->GetTransform(&original);d2->SetTransform(D2D1::Matrix3x2F::Rotation(float(e.rotation),D2D1::Point2F((rect.left+rect.right)/2,(rect.top+rect.bottom)/2))*original);
        if(!e.path.empty()) {auto picture=image(safe_path(root,e.path));d2->DrawBitmap(picture.Get(),rect,float(e.opacity));}
        else {
            const float w = rect.right - rect.left;
            const float h = rect.bottom - rect.top;
            const float alpha = static_cast<float>(e.opacity);
            const auto accent = parse_hex_color(e.color, alpha);
            ComPtr<ID2D1SolidColorBrush> brush;
            FT_MEDIA_CHECK(d2->CreateSolidColorBrush(accent, &brush));

            auto draw_centered_text = [&](D2D1_RECT_F box, float font_size, const wchar_t* font_family, DWRITE_FONT_WEIGHT weight, D2D1_COLOR_F color) {
                if (e.text.empty()) return;
                ComPtr<IDWriteTextFormat> format;
                FT_MEDIA_CHECK(text_factory->CreateTextFormat(font_family, nullptr, weight, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, std::max(8.0f, font_size), L"zh-CN", &format));
                format->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
                format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
                brush->SetColor(color);
                auto text = winrt::to_hstring(e.text);
                d2->DrawText(text.c_str(), text.size(), format.Get(), box, brush.Get());
            };

            auto draw_tag_pill = [&](float anchor_x, float anchor_y, float font_size) {
                if (e.text.empty()) return;
                const auto wtext = winrt::to_hstring(e.text);
                DWRITE_TEXT_METRICS metrics{};
                auto layout = create_text_layout(
                    text_factory.Get(), std::wstring(wtext.c_str()), L"Alibaba PuHuiTi 3.0", DWRITE_FONT_WEIGHT_SEMI_BOLD,
                    font_size, std::max(80.0f, min_dim * 0.45f), font_size * 2.0f,
                    DWRITE_TEXT_ALIGNMENT_LEADING, DWRITE_PARAGRAPH_ALIGNMENT_NEAR, metrics);
                const float pad_x = font_size * 0.6f;
                const float pad_y = font_size * 0.3f;
                const float pw = metrics.width + pad_x * 2.0f;
                const float ph = metrics.height + pad_y * 2.0f;
                const auto pill_rect = D2D1::RectF(anchor_x, anchor_y, anchor_x + pw, anchor_y + ph);
                const float pr = std::min(10.0f, ph * 0.35f);
                brush->SetColor(D2D1::ColorF(0.06f, 0.07f, 0.09f, 0.90f * alpha));
                d2->FillRoundedRectangle(D2D1::RoundedRect(pill_rect, pr, pr), brush.Get());
                brush->SetColor(accent);
                d2->DrawRoundedRectangle(D2D1::RoundedRect(pill_rect, pr, pr), brush.Get(), std::max(1.5f, min_dim * 0.002f));
                brush->SetColor(D2D1::ColorF(1.0f, 1.0f, 1.0f, alpha));
                d2->DrawTextLayout(D2D1::Point2F(pill_rect.left + pad_x - metrics.left, pill_rect.top + pad_y - metrics.top), layout.Get(), brush.Get());
            };

            if (e.shape == "arrow") {
                const float stroke = std::max(3.0f, min_dim * 0.0045f);
                const float dx = rect.right - rect.left;
                const float dy = rect.bottom - rect.top;
                const float len = std::hypot(dx, dy);
                if (len > 1.0f) {
                    const float ux = dx / len, uy = dy / len;
                    const float px = -uy, py = ux;
                    const float head_len = std::min(len * 0.45f, std::clamp(len * 0.25f, stroke * 3.5f, stroke * 8.0f));
                    const float head_half = head_len * 0.56f;
                    const auto shaft_end = D2D1::Point2F(rect.right - ux * head_len * 0.72f, rect.bottom - uy * head_len * 0.72f);
                    ComPtr<ID2D1StrokeStyle> round_cap;
                    auto props = D2D1::StrokeStyleProperties(D2D1_CAP_STYLE_ROUND, D2D1_CAP_STYLE_FLAT, D2D1_CAP_STYLE_ROUND, D2D1_LINE_JOIN_ROUND);
                    FT_MEDIA_CHECK(factory->CreateStrokeStyle(props, nullptr, 0, &round_cap));
                    brush->SetColor(accent);
                    d2->DrawLine(D2D1::Point2F(rect.left, rect.top), shaft_end, brush.Get(), stroke, round_cap.Get());
                    ComPtr<ID2D1PathGeometry> head;
                    FT_MEDIA_CHECK(factory->CreatePathGeometry(&head));
                    ComPtr<ID2D1GeometrySink> sink;
                    FT_MEDIA_CHECK(head->Open(&sink));
                    sink->BeginFigure(D2D1::Point2F(rect.right, rect.bottom), D2D1_FIGURE_BEGIN_FILLED);
                    sink->AddLine(D2D1::Point2F(rect.right - ux * head_len + px * head_half, rect.bottom - uy * head_len + py * head_half));
                    sink->AddLine(D2D1::Point2F(rect.right - ux * head_len - px * head_half, rect.bottom - uy * head_len - py * head_half));
                    sink->EndFigure(D2D1_FIGURE_END_CLOSED);
                    FT_MEDIA_CHECK(sink->Close());
                    d2->FillGeometry(head.Get(), brush.Get());
                }
                if (!e.text.empty()) {
                    const float tag_fs = std::clamp(min_dim * 0.022f, 12.0f, 28.0f);
                    draw_tag_pill(rect.left, rect.top - tag_fs * 1.1f, tag_fs);
                }
            } else if (e.shape == "rect") {
                const float stroke = std::max(3.0f, min_dim * 0.004f);
                const float corner = std::min(12.0f, std::min(w, h) * 0.14f);
                const auto rr = D2D1::RoundedRect(rect, corner, corner);
                brush->SetColor(parse_hex_color(e.color, 0.12f * alpha));
                d2->FillRoundedRectangle(rr, brush.Get());
                brush->SetColor(accent);
                d2->DrawRoundedRectangle(rr, brush.Get(), stroke);
                if (!e.text.empty()) {
                    const float tag_fs = std::clamp(min_dim * 0.020f, 11.0f, 24.0f);
                    draw_tag_pill(rect.left + stroke * 0.5f, rect.top + stroke * 0.5f, tag_fs);
                }
            } else if (e.shape == "circle") {
                const float stroke = std::max(3.5f, min_dim * 0.0045f);
                const auto ellipse = D2D1::Ellipse(
                    D2D1::Point2F((rect.left + rect.right) * 0.5f, (rect.top + rect.bottom) * 0.5f),
                    std::max(1.0f, w * 0.5f), std::max(1.0f, h * 0.5f));
                brush->SetColor(parse_hex_color(e.color, 0.10f * alpha));
                d2->FillEllipse(ellipse, brush.Get());
                brush->SetColor(accent);
                d2->DrawEllipse(ellipse, brush.Get(), stroke);
            } else if (e.shape == "badge") {
                const float radius = std::max(4.0f, std::min(w, h) * 0.5f);
                const auto center = D2D1::Point2F((rect.left + rect.right) * 0.5f, (rect.top + rect.bottom) * 0.5f);
                const auto ellipse = D2D1::Ellipse(center, radius, radius);
                brush->SetColor(accent);
                d2->FillEllipse(ellipse, brush.Get());
                brush->SetColor(D2D1::ColorF(1.0f, 1.0f, 1.0f, alpha));
                d2->DrawEllipse(ellipse, brush.Get(), std::max(2.0f, radius * 0.11f));
                const auto badge_box = D2D1::RectF(center.x - radius, center.y - radius, center.x + radius, center.y + radius);
                draw_centered_text(badge_box, std::max(10.0f, radius * 0.92f), L"Alibaba PuHuiTi 3.0", DWRITE_FONT_WEIGHT_BOLD, D2D1::ColorF(1.0f, 1.0f, 1.0f, alpha));
            } else if (e.shape == "keycap") {
                const float outer_r = std::min(14.0f, std::min(w, h) * 0.24f);
                brush->SetColor(D2D1::ColorF(0.06f, 0.07f, 0.10f, 0.90f * alpha));
                d2->FillRoundedRectangle(D2D1::RoundedRect(rect, outer_r, outer_r), brush.Get());
                brush->SetColor(D2D1::ColorF(1.0f, 1.0f, 1.0f, 0.18f * alpha));
                d2->DrawRoundedRectangle(D2D1::RoundedRect(rect, outer_r, outer_r), brush.Get(), 1.2f);
                const float ix = std::min(6.0f, w * 0.08f);
                const float iy = std::min(5.0f, h * 0.10f);
                const auto inner = D2D1::RectF(rect.left + ix, rect.top + iy * 0.7f, rect.right - ix, rect.bottom - iy * 1.3f);
                const float inner_r = std::max(3.0f, outer_r - 3.0f);
                brush->SetColor(D2D1::ColorF(0.14f, 0.16f, 0.21f, 0.96f * alpha));
                d2->FillRoundedRectangle(D2D1::RoundedRect(inner, inner_r, inner_r), brush.Get());
                brush->SetColor(parse_hex_color(e.color, 0.70f * alpha));
                d2->DrawRoundedRectangle(D2D1::RoundedRect(inner, inner_r, inner_r), brush.Get(), 1.5f);
                draw_centered_text(inner, std::max(9.0f, (inner.bottom - inner.top) * 0.46f), L"Cascadia Mono", DWRITE_FONT_WEIGHT_SEMI_BOLD, D2D1::ColorF(1.0f, 1.0f, 1.0f, alpha));
            } else {
                // "pill" (default)
                const float pill_r = std::min(12.0f, std::min(w, h) * 0.35f);
                brush->SetColor(D2D1::ColorF(0.06f, 0.07f, 0.09f, alpha * 0.85f));
                d2->FillRoundedRectangle(D2D1::RoundedRect(rect, pill_r, pill_r), brush.Get());
                brush->SetColor(parse_hex_color(e.color, alpha * 0.85f));
                d2->DrawRoundedRectangle(D2D1::RoundedRect(rect, pill_r, pill_r), brush.Get(), std::max(1.5f, min_dim * 0.002f));
                draw_centered_text(rect, std::max(8.0f, h * 0.42f), L"Alibaba PuHuiTi 3.0", DWRITE_FONT_WEIGHT_SEMI_BOLD, D2D1::ColorF(1.0f, 1.0f, 1.0f, alpha));
            }
        }
        d2->SetTransform(original);
    }
}
void Renderer::subtitles(const Project&, const FrameScene& scene, ID3D11Texture2D* target, uint32_t w, uint32_t h, bool render_subtitles) {
    if (!render_subtitles || scene.visual.subtitles.empty() || !target) return;
    auto surface = bitmap(target, true, false);
    d2->SetTarget(surface.Get());
    d2->SetTransform(D2D1::Matrix3x2F::Scale(static_cast<float>(w) / scene.width, static_cast<float>(h) / scene.height));
    d2->BeginDraw();
    bool drawing = true;
    try {
        draw_subtitles(factory.Get(), d2.Get(), text_factory.Get(), cached_settings, scene);
        const HRESULT hr = d2->EndDraw();
        drawing = false;
        d2->SetTarget(nullptr);
        FT_MEDIA_CHECK(hr);
    } catch (...) {
        if (drawing) d2->EndDraw();
        d2->SetTarget(nullptr);
        throw;
    }
}
ID3D11Texture2D* Renderer::render(const Project& p,const FrameScene& scene,uint32_t w,uint32_t h,bool render_subtitles) {
    d2->SetTarget(nullptr);ensure_surface(composition,w,h);width=w;height=h;
    if(settings_source!=&p){cached_settings=Json::parse(p.editor_json);settings_source=&p;}
    const auto radius=float(cached_settings.value("cornerRadius",16.0));
    auto begin=[&](ID3D11Texture2D* texture,bool alpha) {auto target=bitmap(texture,true,alpha);d2->SetTarget(target.Get());d2->SetTransform(D2D1::Matrix3x2F::Scale(float(w)/scene.width,float(h)/scene.height));d2->BeginDraw();};
    auto end=[&] {auto hr=d2->EndDraw();d2->SetTarget(nullptr);FT_MEDIA_CHECK(hr);};
    const bool has_visual_effects = std::any_of(p.effects.begin(), p.effects.end(), [](const Effect& e) { return e.kind != "subtitle"; });
    bool drawing=false;
    try {
        begin(composition.Get(),false);drawing=true;d2->Clear(D2D1::ColorF(float(scene.background.r),float(scene.background.g),float(scene.background.b),1));background(p,scene);
        if(!has_visual_effects) {
            layer(p,scene.screen,radius,true,cached_settings,true,scene.visual);if(scene.camera)layer(p,*scene.camera,radius,true,cached_settings,false,scene.visual);
            end();drawing=false;
        } else {
            end();drawing=false;if(!visuals)visuals=std::make_unique<VisualGpu>(*this);
            if (scene.transition.has_value()) {
                ensure_surface(screen_plane,w,h);begin(screen_plane.Get(),true);drawing=true;d2->Clear(D2D1::ColorF(0,0,0,0));
                layer(p,scene.transition->outgoing_screen,radius,true,cached_settings,true,scene.visual);stickers(scene,true);end();drawing=false;
                ensure_surface(transition_plane,w,h);begin(transition_plane.Get(),true);drawing=true;d2->Clear(D2D1::ColorF(0,0,0,0));
                layer(p,scene.transition->incoming_screen,radius,true,cached_settings,true,scene.visual);stickers(scene,true);end();drawing=false;
                ensure_surface(transition_result_plane,w,h);
                ComPtr<ID3D11RenderTargetView> transition_rtv;
                FT_MEDIA_CHECK(device->CreateRenderTargetView(transition_result_plane.Get(),nullptr,&transition_rtv));
                const float transparent[4]{0.f,0.f,0.f,0.f};
                context->ClearRenderTargetView(transition_rtv.Get(),transparent);
                visuals->transition(screen_plane.Get(),transition_plane.Get(),transition_result_plane.Get(),scene.transition->style,scene.transition->progress);
                visuals->project(transition_result_plane.Get(),composition.Get(),scene.visual.screen,scene.width,scene.height);
            } else {
                ensure_surface(screen_plane,w,h);begin(screen_plane.Get(),true);drawing=true;d2->Clear(D2D1::ColorF(0,0,0,0));
                layer(p,scene.screen,radius,true,cached_settings,true,scene.visual);stickers(scene,true);end();drawing=false;
                visuals->project(screen_plane.Get(),composition.Get(),scene.visual.screen,scene.width,scene.height);
            }
            if(scene.camera && scene.visual.camera.opacity>0) {
                ensure_surface(camera_plane,w,h);begin(camera_plane.Get(),true);drawing=true;d2->Clear(D2D1::ColorF(0,0,0,0));layer(p,*scene.camera,radius,true,cached_settings,false,scene.visual);end();drawing=false;
                visuals->project(camera_plane.Get(),composition.Get(),scene.visual.camera,scene.width,scene.height);
            }
            begin(composition.Get(),false);drawing=true;stickers(scene,false);end();drawing=false;
        }
        output=composition;std::vector<VisualEffect> regions;for(auto&e:scene.visual.regions)if(e.attachment=="canvas")regions.push_back(e);
        if(!regions.empty())output=visuals->process(composition.Get(),regions,Rect{},true);
        subtitles(p,scene,output.Get(),w,h,render_subtitles);
    } catch(...) {if(drawing)d2->EndDraw();d2->SetTarget(nullptr);throw;}
    return output.Get();
}
void Renderer::present() {
    ComPtr<ID3D11Texture2D> back; FT_MEDIA_CHECK(swapchain->GetBuffer(0, IID_PPV_ARGS(&back))); auto target = bitmap(back.Get(), true); auto source = bitmap(output.Get());
    d2->SetTarget(target.Get()); d2->SetTransform(D2D1::Matrix3x2F::Identity()); d2->BeginDraw();
    auto size = target->GetSize(); d2->Clear(D2D1::ColorF(0.055f, 0.06f, 0.07f)); auto src = source->GetSize(); const auto scale = std::min(size.width / src.width, size.height / src.height);
    auto rect = D2D1::RectF((size.width - src.width * scale) / 2, (size.height - src.height * scale) / 2, (size.width + src.width * scale) / 2, (size.height + src.height * scale) / 2);
    // Prefilter when fitting a UHD composition into a smaller editor viewport.
    d2->DrawBitmap(source.Get(), rect, 1.0f, D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC); FT_MEDIA_CHECK(d2->EndDraw()); d2->SetTarget(nullptr); FT_MEDIA_CHECK(swapchain->Present(0, 0));
}
}
