#pragma once
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <winrt/base.h>
#include <string_view>
#include <vector>

namespace frametrail::codecs {
// Stable IDs used by the additive capture API; legacy capture stays H.264.
inline const GUID& subtype(uint32_t codec) {
    switch (codec) {
    case 0: return MFVideoFormat_H264;
    case 1: return MFVideoFormat_HEVC;
    case 2: return MFVideoFormat_AV1;
    default: winrt::throw_hresult(E_INVALIDARG);
    }
}
inline uint32_t id(std::string_view codec) {
    if (codec == "h264") return 0;
    if (codec == "hevc") return 1;
    if (codec == "av1") return 2;
    winrt::throw_hresult(E_INVALIDARG);
}
inline uint32_t count(uint32_t codec, UINT32 flags, std::vector<GUID>* classes = nullptr, bool decoder = false) {
    MFT_REGISTER_TYPE_INFO type{ MFMediaType_Video, subtype(codec) };
    IMFActivate** activations{}; UINT32 total{};
    winrt::check_hresult(MFTEnumEx(decoder ? MFT_CATEGORY_VIDEO_DECODER : MFT_CATEGORY_VIDEO_ENCODER,
        flags | MFT_ENUM_FLAG_SORTANDFILTER, decoder ? &type : nullptr, decoder ? nullptr : &type, &activations, &total));
    // Release every activation even if collecting class IDs runs out of memory.
    try {
        if (classes) for (UINT32 i = 0; i < total; ++i) {
            GUID clsid{};
            if (SUCCEEDED(activations[i]->GetGUID(MFT_TRANSFORM_CLSID_Attribute, &clsid))) classes->push_back(clsid);
        }
    } catch (...) {
        for (UINT32 i = 0; i < total; ++i) activations[i]->Release();
        CoTaskMemFree(activations); throw;
    }
    for (UINT32 i = 0; i < total; ++i) activations[i]->Release();
    CoTaskMemFree(activations); return total;
}
}
