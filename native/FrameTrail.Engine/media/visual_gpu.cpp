#include "visual_gpu.hpp"
#include "frametrail/core/plane_projection.hpp"
#include <d3dcompiler.h>
#include <cmath>
#include <numbers>

namespace frametrail::media {
namespace {
constexpr char shader[] = R"hlsl(
Texture2D picture : register(t0);
SamplerState linearClamp : register(s0);
cbuffer Settings : register(b0) {
    float4 info; // source pixel dimensions, opacity, region count
    float4 crop;
    float4 rectangles[32];
    float4 modes[32]; // solid=1, pixelate=2, blur=3, focus=4; radius; opacity
};
struct Vertex { float4 position:POSITION; float2 uv:TEXCOORD0; };
struct Pixel { float4 position:SV_POSITION; float2 uv:TEXCOORD0; };
Pixel vs(Vertex v) { Pixel p; p.position=v.position; p.uv=v.uv; return p; }
float4 blur(float2 uv,float radius) {
    float2 step=radius / info.xy;
    float4 color=picture.Sample(linearClamp,uv)*0.25;
    color+=(picture.Sample(linearClamp,uv+float2(step.x,0))+picture.Sample(linearClamp,uv-float2(step.x,0))
           +picture.Sample(linearClamp,uv+float2(0,step.y))+picture.Sample(linearClamp,uv-float2(0,step.y)))*0.125;
    color+=(picture.Sample(linearClamp,uv+step)+picture.Sample(linearClamp,uv-step)
           +picture.Sample(linearClamp,uv+float2(step.x,-step.y))+picture.Sample(linearClamp,uv+float2(-step.x,step.y)))*0.0625;
    return color;
}
float4 ps(Pixel p):SV_TARGET {
    float2 uv=p.uv; float4 color=picture.Sample(linearClamp,uv);
    float2 local=(uv-crop.xy)/crop.zw;
    [loop] for(int i=0;i<(int)info.w;i++) {
        float4 r=rectangles[i], m=modes[i];
        bool inside=all(local>=r.xy)&&all(local<r.xy+r.zw);
        if(m.x==4)inside=!inside;
        if(!inside)continue;
        float4 replaced=color;
        if(m.x==1)replaced=float4(0,0,0,1);
        else if(m.x==2) {
            float2 origin=crop.xy+r.xy*crop.zw;
            float2 quant=(floor((uv-origin)*info.xy/m.y)+0.5)*m.y/info.xy+origin;
            float2 end=crop.xy+(r.xy+r.zw)*crop.zw;
            replaced=picture.Sample(linearClamp,clamp(quant,origin+0.5/info.xy,end-0.5/info.xy));
        } else replaced=blur(uv,m.y);
        color=lerp(color,replaced,m.z);
    }
    return color*info.z; // premultiplied alpha throughout projected layers
}
)hlsl";
struct Vertex { float x,y,z,w,u,v; };
struct alignas(16) Constants { float info[4], crop[4], rectangles[32][4], modes[32][4]; };
ComPtr<ID3DBlob> compile(const char* entry,const char* target) {
    ComPtr<ID3DBlob> code,error;
    auto hr=D3DCompile(shader,sizeof(shader)-1,"FrameTrail.Visual",nullptr,nullptr,entry,target,D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&error);
    if(FAILED(hr))throw winrt::hresult_error(hr,error?winrt::to_hstring(std::string(static_cast<const char*>(error->GetBufferPointer()),error->GetBufferSize())):L"Visual shader compilation failed");
    return code;
}
}
VisualGpu::VisualGpu(Renderer& gpu):gpu_(gpu) {
    auto vs=compile("vs","vs_4_0"),ps=compile("ps","ps_4_0");
    FT_MEDIA_CHECK(gpu.device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex_));
    FT_MEDIA_CHECK(gpu.device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel_));
    D3D11_INPUT_ELEMENT_DESC elements[]{{"POSITION",0,DXGI_FORMAT_R32G32B32A32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0},{"TEXCOORD",0,DXGI_FORMAT_R32G32_FLOAT,0,16,D3D11_INPUT_PER_VERTEX_DATA,0}};
    FT_MEDIA_CHECK(gpu.device->CreateInputLayout(elements,2,vs->GetBufferPointer(),vs->GetBufferSize(),&layout_));
    D3D11_BUFFER_DESC buffer{};buffer.ByteWidth=sizeof(Vertex)*6;buffer.Usage=D3D11_USAGE_DYNAMIC;buffer.BindFlags=D3D11_BIND_VERTEX_BUFFER;buffer.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;
    FT_MEDIA_CHECK(gpu.device->CreateBuffer(&buffer,nullptr,&vertices_));buffer.ByteWidth=sizeof(Constants);buffer.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
    FT_MEDIA_CHECK(gpu.device->CreateBuffer(&buffer,nullptr,&constants_));
    D3D11_SAMPLER_DESC sampler{};sampler.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sampler.AddressU=sampler.AddressV=sampler.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sampler.MaxLOD=D3D11_FLOAT32_MAX;
    FT_MEDIA_CHECK(gpu.device->CreateSamplerState(&sampler,&sampler_));
    D3D11_BLEND_DESC blend{};auto& b=blend.RenderTarget[0];b.BlendEnable=TRUE;b.SrcBlend=D3D11_BLEND_ONE;b.DestBlend=D3D11_BLEND_INV_SRC_ALPHA;b.BlendOp=D3D11_BLEND_OP_ADD;b.SrcBlendAlpha=D3D11_BLEND_ONE;b.DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA;b.BlendOpAlpha=D3D11_BLEND_OP_ADD;b.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
    FT_MEDIA_CHECK(gpu.device->CreateBlendState(&blend,&blend_));
    D3D11_RASTERIZER_DESC raster{};raster.FillMode=D3D11_FILL_SOLID;raster.CullMode=D3D11_CULL_NONE;raster.DepthClipEnable=TRUE;
    FT_MEDIA_CHECK(gpu.device->CreateRasterizerState(&raster,&raster_));
    D3D11_DEPTH_STENCIL_DESC depth{};depth.DepthEnable=FALSE;depth.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ZERO;depth.DepthFunc=D3D11_COMPARISON_ALWAYS;
    FT_MEDIA_CHECK(gpu.device->CreateDepthStencilState(&depth,&depth_));
}
void VisualGpu::draw(ID3D11Texture2D* input,ID3D11Texture2D* target,const PlaneMotion& motion,double cw,double ch,const std::vector<VisualEffect>& regions,Rect crop,bool clear) {
    if(regions.size()>32)throw ValidationError("effects","at most 32 regions can overlap on one attachment");
    D3D11_TEXTURE2D_DESC in{},out{};input->GetDesc(&in);target->GetDesc(&out);
    ComPtr<ID3D11ShaderResourceView> source;FT_MEDIA_CHECK(gpu_.device->CreateShaderResourceView(input,nullptr,&source));
    ComPtr<ID3D11RenderTargetView> result;FT_MEDIA_CHECK(gpu_.device->CreateRenderTargetView(target,nullptr,&result));
    auto vertex=[&](double u,double v) { auto p=plane_vertex(u,v,motion,cw,ch);return Vertex{float(p[0]),float(p[1]),0,float(p[2]),float(u),float(v)}; };
    Vertex quad[]{vertex(0,0),vertex(1,0),vertex(0,1),vertex(0,1),vertex(1,0),vertex(1,1)};
    D3D11_MAPPED_SUBRESOURCE mapped{}; FT_MEDIA_CHECK(gpu_.context->Map(vertices_.Get(),0,D3D11_MAP_WRITE_DISCARD,0,&mapped));memcpy(mapped.pData,quad,sizeof(quad));gpu_.context->Unmap(vertices_.Get(),0);
    Constants constants{};constants.info[0]=float(in.Width);constants.info[1]=float(in.Height);constants.info[2]=float(motion.opacity);constants.info[3]=float(regions.size());
    constants.crop[0]=float(crop.x);constants.crop[1]=float(crop.y);constants.crop[2]=float(crop.width);constants.crop[3]=float(crop.height);
    std::vector<const VisualEffect*> ordered;for(auto&e:regions)ordered.push_back(&e);
    auto priority=[](auto e){return e->kind=="focus"?0:e->mode=="blur"?1:e->mode=="pixelate"?2:3;};
    // A later blur must never sample original pixels over an opaque privacy mask.
    std::stable_sort(ordered.begin(),ordered.end(),[&](auto a,auto b){return priority(a)<priority(b);});
    for(size_t i=0;i<ordered.size();++i) {auto&e=*ordered[i];float mode=e.kind=="focus"?4.f:e.mode=="pixelate"?2.f:e.mode=="blur"?3.f:1.f;
        constants.rectangles[i][0]=float(e.rect.x);constants.rectangles[i][1]=float(e.rect.y);constants.rectangles[i][2]=float(e.rect.width);constants.rectangles[i][3]=float(e.rect.height);
        constants.modes[i][0]=mode;constants.modes[i][1]=float(e.strength);constants.modes[i][2]=float(e.opacity);
    }
    FT_MEDIA_CHECK(gpu_.context->Map(constants_.Get(),0,D3D11_MAP_WRITE_DISCARD,0,&mapped));memcpy(mapped.pData,&constants,sizeof(constants));gpu_.context->Unmap(constants_.Get(),0);
    if(clear){float zero[4]{};gpu_.context->ClearRenderTargetView(result.Get(),zero);}
    auto rtv=result.Get();gpu_.context->OMSetRenderTargets(1,&rtv,nullptr);gpu_.context->OMSetBlendState(blend_.Get(),nullptr,0xffffffff);gpu_.context->OMSetDepthStencilState(depth_.Get(),0);
    D3D11_VIEWPORT viewport{0,0,float(out.Width),float(out.Height),0,1};gpu_.context->RSSetViewports(1,&viewport);gpu_.context->RSSetState(raster_.Get());
    UINT stride=sizeof(Vertex),offset=0;auto buffer=vertices_.Get();gpu_.context->IASetVertexBuffers(0,1,&buffer,&stride,&offset);gpu_.context->IASetInputLayout(layout_.Get());gpu_.context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    gpu_.context->VSSetShader(vertex_.Get(),nullptr,0);gpu_.context->PSSetShader(pixel_.Get(),nullptr,0);auto constant=constants_.Get();gpu_.context->PSSetConstantBuffers(0,1,&constant);
    auto sampler=sampler_.Get();gpu_.context->PSSetSamplers(0,1,&sampler);auto srv=source.Get();gpu_.context->PSSetShaderResources(0,1,&srv);gpu_.context->Draw(6,0);
    ID3D11ShaderResourceView* empty{};gpu_.context->PSSetShaderResources(0,1,&empty);gpu_.context->OMSetRenderTargets(0,nullptr,nullptr);
}
ComPtr<ID3D11Texture2D> VisualGpu::process(ID3D11Texture2D* input,const std::vector<VisualEffect>& regions,Rect crop,bool canvas) {
    if(regions.empty())return input;
    auto& texture=canvas?canvas_effects_:source_effects_;D3D11_TEXTURE2D_DESC desc{},prior{};input->GetDesc(&desc);if(texture)texture->GetDesc(&prior);
    if(!texture || desc.Width!=prior.Width || desc.Height!=prior.Height) {texture.Reset();desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;desc.CPUAccessFlags=0;desc.MiscFlags=0;desc.ArraySize=desc.MipLevels=1;FT_MEDIA_CHECK(gpu_.device->CreateTexture2D(&desc,nullptr,&texture));}
    draw(input,texture.Get(),{},desc.Width,desc.Height,regions,crop,true);return texture;
}
void VisualGpu::project(ID3D11Texture2D* input,ID3D11Texture2D* target,const PlaneMotion& motion,double width,double height) {draw(input,target,motion,width,height,{},Rect{},false);}
} // namespace frametrail::media
