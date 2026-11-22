#pragma once
#include "internal.hpp"

namespace frametrail::media {
// One bounded set of shader resources per renderer; no image pixels cross the ABI.
class VisualGpu {
    Renderer& gpu_;
    ComPtr<ID3D11VertexShader> vertex_;
    ComPtr<ID3D11PixelShader> pixel_, transition_pixel_;
    ComPtr<ID3D11InputLayout> layout_;
    ComPtr<ID3D11Buffer> vertices_, constants_, transition_constants_;
    ComPtr<ID3D11SamplerState> sampler_;
    ComPtr<ID3D11BlendState> blend_;
    ComPtr<ID3D11RasterizerState> raster_;
    ComPtr<ID3D11DepthStencilState> depth_;
    ComPtr<ID3D11Texture2D> source_effects_, canvas_effects_;
    void draw(ID3D11Texture2D* input, ID3D11Texture2D* target, const PlaneMotion& motion, double canvas_width, double canvas_height,
        const std::vector<VisualEffect>& regions, Rect crop, bool clear);
public:
    explicit VisualGpu(Renderer& gpu);
    ComPtr<ID3D11Texture2D> process(ID3D11Texture2D* input, const std::vector<VisualEffect>& regions, Rect crop, bool canvas);
    void project(ID3D11Texture2D* input, ID3D11Texture2D* target, const PlaneMotion& motion, double canvas_width, double canvas_height);
    void transition(ID3D11Texture2D* outgoing, ID3D11Texture2D* incoming, ID3D11Texture2D* target, std::string_view style, double progress);
};
}
