#include "internal.hpp"
#include "frametrail/core/mockup.hpp"

namespace frametrail::media {
// DogSC FrameScreenChromeScene / SharedFrameRenderer.screenChromeLayer (MIT).
void Renderer::frame_chrome(D2D1_RECT_F content, std::string_view style, double canvas_short, const Json& settings) {
    const auto f=frametrail::core::frame_decoration(style,settings.value("frameScale",1.0),settings.value("frameToolbarScale",1.0),
        settings.value("frameOuterCornerRadius",-1.0),settings.value("cornerRadius",0.0),canvas_short,content.right-content.left,content.bottom-content.top);
    const auto outer=D2D1::RectF(content.left-float(f.inset),content.top-float(f.top),content.right+float(f.inset),content.bottom+float(f.inset));
    const auto radius=float(f.outer_radius), scale=float(f.scale);
    const auto shape=D2D1::RoundedRect(outer,radius,radius);
    ComPtr<ID2D1SolidColorBrush> brush;FT_MEDIA_CHECK(d2->CreateSolidColorBrush(D2D1::ColorF(f.dark?0x222327u:0xECEDF0u),&brush));
    d2->FillRoundedRectangle(shape,brush.Get());
    ComPtr<ID2D1RoundedRectangleGeometry> clip;FT_MEDIA_CHECK(factory->CreateRoundedRectangleGeometry(shape,&clip));
    d2->PushLayer(D2D1::LayerParameters(D2D1::InfiniteRect(),clip.Get()),nullptr);
    if(f.device) {
        const D2D1_GRADIENT_STOP stops[]={{0,D2D1::ColorF(.52f,.54f,.56f)},{1,D2D1::ColorF(.17f,.18f,.20f)}};
        ComPtr<ID2D1GradientStopCollection> collection; FT_MEDIA_CHECK(d2->CreateGradientStopCollection(stops,2,&collection));
        ComPtr<ID2D1LinearGradientBrush> metal; FT_MEDIA_CHECK(d2->CreateLinearGradientBrush(D2D1::LinearGradientBrushProperties(D2D1::Point2F(outer.left,outer.top),D2D1::Point2F(outer.right,outer.bottom)),collection.Get(),&metal));
        d2->FillRoundedRectangle(shape,metal.Get());
        auto rim=std::max(std::min(outer.right-outer.left,outer.bottom-outer.top)*.006f,.8f);
        brush->SetColor(D2D1::ColorF(.055f,.06f,.065f));
        auto inner=D2D1::RoundedRect(D2D1::RectF(outer.left+rim,outer.top+rim,outer.right-rim,outer.bottom-rim),std::max(0.f,radius-rim),std::max(0.f,radius-rim));
        d2->FillRoundedRectangle(inner,brush.Get());
        brush->SetColor(D2D1::ColorF(0x4B4D55u,.42f)); d2->DrawRoundedRectangle(shape,brush.Get(),rim*.7f);
        d2->PopLayer();return;
    }
    brush->SetColor(D2D1::ColorF(f.dark?0x313238u:0xF4F4F6u));
    d2->FillRectangle(D2D1::RectF(outer.left,outer.top,outer.right,content.top+std::min(2.f*scale,float(f.top)*0.08f)),brush.Get());
    brush->SetColor(D2D1::ColorF(f.dark?0x4B4D55u:0xC9CACEu));
    const auto bar_height=float(f.top), middle=outer.top+bar_height/2;
    const auto separator_height=std::max(bar_height*.035f,.75f);
    d2->FillRectangle(D2D1::RectF(outer.left,content.top-separator_height,outer.right,content.top),brush.Get());
    const auto diameter=std::min(std::max(bar_height*.23f,1.5f),bar_height*.32f), margin=std::max(bar_height*.31f,diameter), spacing=diameter*1.62f;
    for(int i=0;i<3;i++) {
        const uint32_t colours[]={0xFF5F57,0xFEBC2E,0x28C840};brush->SetColor(D2D1::ColorF(colours[i]));
        d2->FillEllipse(D2D1::Ellipse(D2D1::Point2F(outer.left+margin+spacing*i+diameter/2,middle),diameter/2,diameter/2),brush.Get());
    }
    const auto center=(outer.left+outer.right)/2;
    auto text_rect=D2D1::RectF(center-(outer.right-outer.left)*.23f,outer.top,center+(outer.right-outer.left)*.23f,content.top);
    auto text=settings.value(f.browser?"frameBrowserAddress":"frameTitle",std::string{});
    float font_size=std::max(bar_height*.30f,5.f);
    ComPtr<IDWriteTextFormat> format;
    auto make_format=[&](float size) { FT_MEDIA_CHECK(text_factory->CreateTextFormat(L"Segoe UI",nullptr,f.browser?DWRITE_FONT_WEIGHT_NORMAL:DWRITE_FONT_WEIGHT_MEDIUM,DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,size,L"zh-CN",format.ReleaseAndGetAddressOf()));
        format->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);format->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP); };
    auto value=winrt::to_hstring(text);
    if(f.browser) {
        auto field_margin=std::max(bar_height*.28f,2.f), available_left=outer.left+margin+spacing*2+diameter+field_margin, available_right=outer.right-field_margin;
        auto available=std::max(available_right-available_left,2.f), field_height=std::max(bar_height*.44f,2.f);
        font_size=std::max(field_height*.38f,5.f); make_format(font_size);
        float text_width=0;
        if(!text.empty()) { ComPtr<IDWriteTextLayout> layout; FT_MEDIA_CHECK(text_factory->CreateTextLayout(value.c_str(),static_cast<UINT32>(value.size()),format.Get(),100000,bar_height,&layout)); DWRITE_TEXT_METRICS metrics{}; FT_MEDIA_CHECK(layout->GetMetrics(&metrics)); text_width=metrics.width; }
        auto field_width=std::min(std::max(text_width+field_height*1.7f,field_height*3),available);
        auto field_center=std::min(std::max(center,available_left+field_width/2),available_right-field_width/2);
        auto field=D2D1::RectF(field_center-field_width/2,middle-field_height/2,field_center+field_width/2,middle+field_height/2);
        brush->SetColor(D2D1::ColorF(f.dark?0x1B1C20u:0xFFFFFFu));
        d2->FillRoundedRectangle(D2D1::RoundedRect(field,field_height/2,field_height/2),brush.Get());
        auto glyph=std::max(field_height*.22f,1.f); brush->SetColor(D2D1::ColorF(f.dark?0xA9ABB3u:0x7A7C84u,.62f));
        d2->FillEllipse(D2D1::Ellipse(D2D1::Point2F(field.left+field_height*.34f+glyph/2,middle),glyph/2,glyph/2),brush.Get());
        text_rect=D2D1::RectF(field.left+field_height*.675f,field.top,field.right-field_height*.675f,field.bottom);
        // The URL is authored text; it is never opened or requested.
    } else make_format(font_size);
    if(!text.empty() && text_rect.right>text_rect.left) {
        brush->SetColor(D2D1::ColorF(f.dark?0xA9ABB3u:0x7A7C84u));
        d2->DrawText(value.c_str(),static_cast<uint32_t>(value.size()),format.Get(),text_rect,brush.Get(),D2D1_DRAW_TEXT_OPTIONS_CLIP);
    } else if(!f.browser) {
        auto placeholder_width=std::min((outer.right-outer.left)*.22f,bar_height*3.2f), placeholder_height=std::max(bar_height*.11f,1.f);
        brush->SetColor(D2D1::ColorF(f.dark?0xA9ABB3u:0x7A7C84u,.38f));
        d2->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(center-placeholder_width/2,middle-placeholder_height/2,center+placeholder_width/2,middle+placeholder_height/2),placeholder_height/2,placeholder_height/2),brush.Get());
    }
    d2->PopLayer();
}
}
