#include "internal.hpp"
#include "frametrail/core/mockup.hpp"
#include "visual_gpu.hpp"
#include <d2d1_1helper.h>
#include <algorithm>
#include <cmath>

namespace frametrail::media {
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
    auto key = path.wstring(); if (images.contains(key)) return images.at(key);
    ComPtr<IWICImagingFactory> wic; FT_MEDIA_CHECK(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic)));
    ComPtr<IWICBitmapDecoder> decoder; FT_MEDIA_CHECK(wic->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad, &decoder));
    ComPtr<IWICBitmapFrameDecode> frame; FT_MEDIA_CHECK(decoder->GetFrame(0, &frame));
    UINT w{}, h{}; FT_MEDIA_CHECK(frame->GetSize(&w, &h)); if (w > 8192 || h > 8192 || uint64_t(w)*h > 16'777'216) throw ValidationError("image", "image exceeds 16 megapixel decode limit");
    ComPtr<IWICFormatConverter> converter; FT_MEDIA_CHECK(wic->CreateFormatConverter(&converter)); FT_MEDIA_CHECK(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom));
    ComPtr<ID2D1Bitmap1> result; FT_MEDIA_CHECK(d2->CreateBitmapFromWicBitmap(converter.Get(), nullptr, &result));
    if (images.size() >= 4) images.clear(); images[key] = result; return result;
}
void Renderer::layer(const Project& p, const MediaFrame& frame, float radius, bool shadow, const Json& settings, bool screen, const VisualScene& visual) {
    const auto asset = std::find_if(p.media.begin(), p.media.end(), [&](auto& m) { return m.id == frame.media_id; });
    if (asset == p.media.end()) throw ValidationError("scene", "missing media ID");
    if (!readers.contains(asset->id)) {
        if (readers.size() >= 4) readers.clear();
        readers[asset->id] = std::make_unique<VideoReader>(safe_path(root, asset->path), manager.Get());
    }
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
    if (screen && settings.value("cursorScale", 1.0) > 0) {
        for (auto& m : p.media) if (m.kind == MediaKind::pointer_events) {
            const auto local = normalized_ticks(m.start_offset); const auto global = frame.local_ticks + normalized_ticks(asset->start_offset);
            if (global < local || global >= local + normalized_ticks(m.duration)) continue;
            if (!pointers.contains(m.id)) {
                if (pointers.size() >= 2) pointers.clear(); PointerTrack track; std::ifstream input(safe_path(root, m.path)); std::string line;
                if (!input) throw ValidationError("pointer", "pointer file unavailable");
                while (std::getline(input, line)) {
                    if (line.size() > 32768 || track.points.size() >= 2000000) throw ValidationError("pointer", "pointer cache limit exceeded");
                    auto event = Json::parse(line);
                    if (event.value("type", "") == "shape") track.shapes[event.at("id").get<uint32_t>()] = {event.at("file").get<std::string>(), event.value("hotspotX", 0.0f), event.value("hotspotY", 0.0f)};
                    if (event.value("type", "") == "pointer") {
                        auto t = normalized_ticks({event.at("ticks").get<int64_t>(), event.at("timescale").get<int32_t>()});
                        float x = event.at("x").get<float>(), y = event.at("y").get<float>();
                        if (!std::isfinite(x) || !std::isfinite(y) || x < 0 || x > 1 || y < 0 || y > 1) throw ValidationError("pointer", "invalid coordinates");
                        track.points.push_back({t, x, y, event.value("shape", 0u), event.value("visible", false) && event.value("inside", false)});
                    }
                }
                std::stable_sort(track.points.begin(), track.points.end(), [](auto& a, auto& b) { return a.ticks < b.ticks; }); pointers[m.id] = std::move(track);
            }
            auto& track = pointers.at(m.id); auto it = std::upper_bound(track.points.begin(), track.points.end(), global - local, [](auto t, auto& e) { return t < e.ticks; });
            if (it == track.points.begin()) continue; --it; if (!it->visible) continue;
            if(track.smooth_points.empty())for(auto& point:track.points)track.smooth_points.push_back({point.ticks,point.x,point.y});
            auto point=smooth_pointer(track.smooth_points,global-local,settings.value("cursorSmoothing",0.08));
            const float x = rect.left + (float(point[0]) * asset->width - src.left) / (src.right - src.left) * (rect.right - rect.left);
            const float y = rect.top + (float(point[1]) * asset->height - src.top) / (src.bottom - src.top) * (rect.bottom - rect.top);
            auto found = track.shapes.find(it->shape); const auto scale = static_cast<float>(settings.value("cursorScale", 1.0)) * (rect.right - rect.left) / (src.right - src.left);
            if (found != track.shapes.end()) {
                auto rel = winrt::to_string((std::filesystem::path(winrt::to_hstring(m.path).c_str()).parent_path() / std::filesystem::path(winrt::to_hstring(found->second.file).c_str())).generic_wstring());
                auto icon = image(safe_path(root, rel)); auto size = icon->GetSize(); auto shape = found->second;
                auto dest = D2D1::RectF(x - shape.x * scale, y - shape.y * scale, x + (size.width - shape.x) * scale, y + (size.height - shape.y) * scale);
                d2->DrawBitmap(icon.Get(), dest);
            } else {
                // Explicit default pointer when capture could not persist a shape.
                ComPtr<ID2D1PathGeometry> arrow; FT_MEDIA_CHECK(factory->CreatePathGeometry(&arrow)); ComPtr<ID2D1GeometrySink> sink; FT_MEDIA_CHECK(arrow->Open(&sink));
                sink->BeginFigure(D2D1::Point2F(x, y), D2D1_FIGURE_BEGIN_FILLED);
                sink->AddLine(D2D1::Point2F(x + 2 * scale, y + 20 * scale)); sink->AddLine(D2D1::Point2F(x + 7 * scale, y + 15 * scale)); sink->AddLine(D2D1::Point2F(x + 14 * scale, y + 15 * scale)); sink->EndFigure(D2D1_FIGURE_END_CLOSED); FT_MEDIA_CHECK(sink->Close());
                brush->SetOpacity(1); brush->SetColor(D2D1::ColorF(D2D1::ColorF::White)); d2->FillGeometry(arrow.Get(), brush.Get()); brush->SetColor(D2D1::ColorF(D2D1::ColorF::Black)); d2->DrawGeometry(arrow.Get(), brush.Get(), scale);
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
    for(auto&e:scene.visual.stickers) {
        if((e.attachment=="screen")!=attached)continue;
        auto base=attached?scene.screen.output_rect:Rect{0,0,double(scene.width),double(scene.height)};
        auto rect=D2D1::RectF(float(base.x+e.rect.x*base.width),float(base.y+e.rect.y*base.height),float(base.x+(e.rect.x+e.rect.width)*base.width),float(base.y+(e.rect.y+e.rect.height)*base.height));
        D2D1_MATRIX_3X2_F original;d2->GetTransform(&original);d2->SetTransform(D2D1::Matrix3x2F::Rotation(float(e.rotation),D2D1::Point2F((rect.left+rect.right)/2,(rect.top+rect.bottom)/2))*original);
        if(!e.path.empty()) {auto picture=image(safe_path(root,e.path));d2->DrawBitmap(picture.Get(),rect,float(e.opacity));}
        else if(!e.text.empty()) {
            ComPtr<ID2D1SolidColorBrush> brush;FT_MEDIA_CHECK(d2->CreateSolidColorBrush(D2D1::ColorF(0.06f,0.07f,0.09f,float(e.opacity)*0.85f),&brush));d2->FillRoundedRectangle(D2D1::RoundedRect(rect,12,12),brush.Get());
            brush->SetColor(D2D1::ColorF(1,1,1,float(e.opacity)));ComPtr<IDWriteTextFormat> format;FT_MEDIA_CHECK(text_factory->CreateTextFormat(L"Segoe UI",nullptr,DWRITE_FONT_WEIGHT_SEMI_BOLD,DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,std::max(8.f,(rect.bottom-rect.top)*0.42f),L"zh-CN",&format));format->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            auto text=winrt::to_hstring(e.text);d2->DrawText(text.c_str(),text.size(),format.Get(),rect,brush.Get());
        }
        d2->SetTransform(original);
    }
}
ID3D11Texture2D* Renderer::render(const Project& p,const FrameScene& scene,uint32_t w,uint32_t h) {
    d2->SetTarget(nullptr);ensure_surface(composition,w,h);width=w;height=h;
    if(settings_source!=&p){cached_settings=Json::parse(p.editor_json);settings_source=&p;}
    const auto radius=float(cached_settings.value("cornerRadius",16.0));
    auto begin=[&](ID3D11Texture2D* texture,bool alpha) {auto target=bitmap(texture,true,alpha);d2->SetTarget(target.Get());d2->SetTransform(D2D1::Matrix3x2F::Scale(float(w)/scene.width,float(h)/scene.height));d2->BeginDraw();};
    auto end=[&] {auto hr=d2->EndDraw();d2->SetTarget(nullptr);FT_MEDIA_CHECK(hr);};
    bool drawing=false;
    try {
        begin(composition.Get(),false);drawing=true;d2->Clear(D2D1::ColorF(float(scene.background.r),float(scene.background.g),float(scene.background.b),1));background(p,scene);
        if(p.effects.empty()) {
            layer(p,scene.screen,radius,true,cached_settings,true,scene.visual);if(scene.camera)layer(p,*scene.camera,radius,true,cached_settings,false,scene.visual);
            end();drawing=false;
        } else {
            end();drawing=false;if(!visuals)visuals=std::make_unique<VisualGpu>(*this);
            ensure_surface(screen_plane,w,h);begin(screen_plane.Get(),true);drawing=true;d2->Clear(D2D1::ColorF(0,0,0,0));
            layer(p,scene.screen,radius,true,cached_settings,true,scene.visual);stickers(scene,true);end();drawing=false;
            visuals->project(screen_plane.Get(),composition.Get(),scene.visual.screen,scene.width,scene.height);
            if(scene.camera && scene.visual.camera.opacity>0) {
                ensure_surface(camera_plane,w,h);begin(camera_plane.Get(),true);drawing=true;d2->Clear(D2D1::ColorF(0,0,0,0));layer(p,*scene.camera,radius,true,cached_settings,false,scene.visual);end();drawing=false;
                visuals->project(camera_plane.Get(),composition.Get(),scene.visual.camera,scene.width,scene.height);
            }
            begin(composition.Get(),false);drawing=true;stickers(scene,false);end();drawing=false;
        }
        output=composition;std::vector<VisualEffect> regions;for(auto&e:scene.visual.regions)if(e.attachment=="canvas")regions.push_back(e);
        if(!regions.empty())output=visuals->process(composition.Get(),regions,Rect{},true);
    } catch(...) {if(drawing)d2->EndDraw();d2->SetTarget(nullptr);throw;}
    return output.Get();
}
void Renderer::present() {
    ComPtr<ID3D11Texture2D> back; FT_MEDIA_CHECK(swapchain->GetBuffer(0, IID_PPV_ARGS(&back))); auto target = bitmap(back.Get(), true); auto source = bitmap(output.Get());
    d2->SetTarget(target.Get()); d2->SetTransform(D2D1::Matrix3x2F::Identity()); d2->BeginDraw();
    auto size = target->GetSize(); d2->Clear(D2D1::ColorF(0.055f, 0.06f, 0.07f)); auto src = source->GetSize(); const auto scale = std::min(size.width / src.width, size.height / src.height);
    auto rect = D2D1::RectF((size.width - src.width * scale) / 2, (size.height - src.height * scale) / 2, (size.width + src.width * scale) / 2, (size.height + src.height * scale) / 2);
    d2->DrawBitmap(source.Get(), rect); FT_MEDIA_CHECK(d2->EndDraw()); d2->SetTarget(nullptr); FT_MEDIA_CHECK(swapchain->Present(0, 0));
}
}
