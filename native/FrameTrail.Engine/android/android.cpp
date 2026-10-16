#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <codecapi.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <winrt/base.h>
#include "android.h"
#include "protocol.hpp"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <fstream>
#include <map>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>
using Microsoft::WRL::ComPtr;
using winrt::check_hresult;
using namespace frametrail::android;
namespace {
int64_t qpc() { LARGE_INTEGER v{}; QueryPerformanceCounter(&v); return v.QuadPart; }
int64_t frequency() { LARGE_INTEGER v{}; QueryPerformanceFrequency(&v); return v.QuadPart; }
int64_t device_to_qpc(int64_t us) { return us/1'000'000*frequency()+us%1'000'000*frequency()/1'000'000; }
struct Apartment { HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED); ~Apartment(){if(SUCCEEDED(hr))CoUninitialize();} };
struct Runtime {
    Runtime() { WSADATA w{}; if(WSAStartup(MAKEWORD(2,2),&w)) throw std::runtime_error("Winsock startup failed"); check_hresult(MFStartup(MF_VERSION)); }
    ~Runtime(){MFShutdown(); WSACleanup();}
};
ComPtr<IMFSample> sample(const std::vector<uint8_t>& data, int64_t time, int64_t duration, bool key) {
    ComPtr<IMFMediaBuffer> b; check_hresult(MFCreateMemoryBuffer(static_cast<DWORD>(data.size()),&b)); BYTE* p{};
    check_hresult(b->Lock(&p,nullptr,nullptr)); memcpy(p,data.data(),data.size()); b->Unlock(); check_hresult(b->SetCurrentLength(static_cast<DWORD>(data.size())));
    ComPtr<IMFSample> s; check_hresult(MFCreateSample(&s)); check_hresult(s->AddBuffer(b.Get())); check_hresult(s->SetSampleTime(time));
    check_hresult(s->SetSampleDuration(duration)); check_hresult(s->SetUINT32(MFSampleExtension_CleanPoint,key)); return s;
}
ComPtr<IMFMediaType> video_type(uint32_t w,uint32_t h,uint32_t fps,const GUID& subtype) {
    ComPtr<IMFMediaType> t; check_hresult(MFCreateMediaType(&t)); check_hresult(t->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video));
    check_hresult(t->SetGUID(MF_MT_SUBTYPE,subtype)); check_hresult(MFSetAttributeSize(t.Get(),MF_MT_FRAME_SIZE,w,h));
    check_hresult(MFSetAttributeRatio(t.Get(),MF_MT_FRAME_RATE,fps,1)); check_hresult(MFSetAttributeRatio(t.Get(),MF_MT_PIXEL_ASPECT_RATIO,1,1));
    check_hresult(t->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive)); return t;
}
void wave_header(std::fstream& f,uint32_t bytes) {
    uint32_t size=bytes+36,fmt=16,sr=48000,rate=192000; uint16_t pcm=1,ch=2,block=4,bits=16;
    f.seekp(0); f.write("RIFF",4);f.write(reinterpret_cast<char*>(&size),4);f.write("WAVEfmt ",8);f.write(reinterpret_cast<char*>(&fmt),4);
    f.write(reinterpret_cast<char*>(&pcm),2);f.write(reinterpret_cast<char*>(&ch),2);f.write(reinterpret_cast<char*>(&sr),4);f.write(reinterpret_cast<char*>(&rate),4);
    f.write(reinterpret_cast<char*>(&block),2);f.write(reinterpret_cast<char*>(&bits),2);f.write("data",4);f.write(reinterpret_cast<char*>(&bytes),4);
}
struct Writer {
    std::wstring video,audio; ComPtr<IMFSinkWriter> sink; DWORD stream{}; std::fstream wav;
    FtAndroidSegmentStatus status{sizeof(FtAndroidSegmentStatus)};
    int64_t last_pts{-1}; std::vector<uint8_t> pending, sequence; bool pending_key{};
    void start(uint32_t w,uint32_t h,uint32_t fps,const std::vector<uint8_t>& config,int64_t pts,int64_t epoch,bool has_audio) {
        auto type=video_type(w,h,fps,MFVideoFormat_H264);
        check_hresult(type->SetBlob(MF_MT_MPEG_SEQUENCE_HEADER,config.data(),static_cast<UINT32>(config.size())));
        check_hresult(type->SetUINT32(MF_MT_AVG_BITRATE,8'000'000));
        check_hresult(MFCreateSinkWriterFromURL(video.c_str(),nullptr,nullptr,&sink)); check_hresult(sink->AddStream(type.Get(),&stream));
        check_hresult(sink->SetInputMediaType(stream,type.Get(),nullptr)); check_hresult(sink->BeginWriting());
        if(has_audio && !audio.empty()) { wav.open(audio,std::ios::binary|std::ios::in|std::ios::out|std::ios::trunc); if(!wav)throw std::runtime_error("Cannot create phone audio"); wave_header(wav,0); }
        sequence=config;status.ready=1;status.origin_pts_us=pts;status.epoch_qpc=epoch;
    }
    void video_packet(const std::vector<uint8_t>& data,int64_t pts,bool key) {
        if(last_pts>=0 && pts<=last_pts) throw std::runtime_error("Non-monotonic H.264 PTS; B frames are unsupported");
        if(!pending.empty()) { auto s=sample(pending,(last_pts-status.origin_pts_us)*10,(pts-last_pts)*10,pending_key); check_hresult(sink->WriteSample(stream,s.Get()));status.frames++; }
        if(last_pts<0){pending=sequence;pending.insert(pending.end(),data.begin(),data.end());}else pending=data;
        pending_key=key;last_pts=pts;status.duration_ticks=(pts-status.origin_pts_us)*10;
    }
    void audio_packet(const std::vector<uint8_t>& data,int64_t pts) {
        if(!wav.is_open())return;
        if(data.size()%4)throw std::runtime_error("Unaligned Android PCM packet");
        int64_t first=(pts-status.origin_pts_us)*48000/1'000'000, count=static_cast<int64_t>(data.size()/4);
        int64_t skip=std::max<int64_t>(0,static_cast<int64_t>(status.audio_frames)-first);
        if(skip>=count)return;
        // Never create arbitrarily large silence from a corrupt timestamp.
        auto gap=first-static_cast<int64_t>(status.audio_frames);
        if(gap>48000*5)throw std::runtime_error("Android audio timestamp gap exceeds five seconds");
        static const char silence[4096]{};
        while(gap>0) { auto n=std::min<int64_t>(gap,1024);wav.write(silence,n*4);status.audio_frames+=n;gap-=n; }
        if((status.audio_frames+count-skip)*4>UINT32_MAX-36)throw std::runtime_error("Phone WAV reached RIFF size limit");
        wav.write(reinterpret_cast<const char*>(data.data()+skip*4),(count-skip)*4);status.audio_frames+=count-skip;
        if(!wav)throw std::runtime_error("Phone audio write failed");
    }
    void finish(int64_t boundary,uint32_t fps) {
        if(!sink)return;
        const int64_t boundary_ticks=std::max<int64_t>(0,(boundary-status.epoch_qpc)*10'000'000/frequency());
        if(!pending.empty()) {
            auto time=(last_pts-status.origin_pts_us)*10;
            auto duration=std::min<int64_t>(10'000'000/fps,std::max<int64_t>(1,boundary_ticks-time));
            if(time<boundary_ticks) { auto s=sample(pending,time,duration,pending_key);check_hresult(sink->WriteSample(stream,s.Get()));status.frames++;status.duration_ticks=time+duration; }
        }
        check_hresult(sink->Finalize());sink.Reset();
        status.duration_ticks=std::min(status.duration_ticks,boundary_ticks);
        if(wav.is_open()){wave_header(wav,static_cast<uint32_t>(status.audio_frames*4));wav.flush();if(!wav)throw std::runtime_error("Phone audio finalize failed");wav.close();}
    }
};
// Decoder and D3D resources never expose pixels through the ABI.
struct Preview {
    uint32_t w,h,fps; ComPtr<IMFTransform> decoder; ComPtr<IMFMediaType> output_type;
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11VideoDevice> vd;ComPtr<ID3D11VideoContext> vc;ComPtr<ID3D11VideoProcessorEnumerator> ve;ComPtr<ID3D11VideoProcessor> vp;
    ComPtr<ID3D11Texture2D> nv12;ComPtr<IDXGISwapChain1> swapchain;
    ComPtr<ID3D11Texture2D> last_frame;
    bool has_frame{};
    std::vector<uint8_t> initial_config;bool primed{};
    Preview(uint32_t w,uint32_t h,uint32_t fps,const std::vector<uint8_t>& config):w(w),h(h),fps(fps),initial_config(config) {
        check_hresult(CoCreateInstance(CLSID_CMSH264DecoderMFT,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&decoder)));
        ComPtr<IMFAttributes> attributes;check_hresult(decoder->GetAttributes(&attributes));check_hresult(attributes->SetUINT32(CODECAPI_AVLowLatencyMode,TRUE));
        auto input=video_type(w,h,fps,MFVideoFormat_H264);check_hresult(input->SetBlob(MF_MT_MPEG_SEQUENCE_HEADER,config.data(),static_cast<UINT32>(config.size())));
        check_hresult(decoder->SetInputType(0,input.Get(),0)); choose_output();
        check_hresult(decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,0));check_hresult(decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM,0));
        UINT flags=D3D11_CREATE_DEVICE_BGRA_SUPPORT|D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
        auto hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,flags,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context);
        if(FAILED(hr))check_hresult(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,flags,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
        check_hresult(device.As(&vd));check_hresult(context.As(&vc));
        ComPtr<IDXGIDevice> dx;check_hresult(device.As(&dx));ComPtr<IDXGIAdapter> adapter;check_hresult(dx->GetAdapter(&adapter));ComPtr<IDXGIFactory2> factory;check_hresult(adapter->GetParent(IID_PPV_ARGS(&factory)));
        DXGI_SWAP_CHAIN_DESC1 desc{};desc.Width=w;desc.Height=h;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=2;desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;desc.Scaling=DXGI_SCALING_STRETCH;
        check_hresult(factory->CreateSwapChainForComposition(device.Get(),&desc,nullptr,&swapchain));
        D3D11_TEXTURE2D_DESC td{};td.Width=w;td.Height=h;td.MipLevels=td.ArraySize=1;td.Format=DXGI_FORMAT_NV12;td.SampleDesc.Count=1;td.Usage=D3D11_USAGE_DEFAULT;td.BindFlags=D3D11_BIND_DECODER|D3D11_BIND_SHADER_RESOURCE;
        check_hresult(device->CreateTexture2D(&td,nullptr,&nv12));
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC cd{};cd.InputFrameFormat=D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;cd.InputWidth=cd.OutputWidth=w;cd.InputHeight=cd.OutputHeight=h;cd.InputFrameRate=cd.OutputFrameRate={fps,1};cd.Usage=D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        check_hresult(vd->CreateVideoProcessorEnumerator(&cd,&ve));check_hresult(vd->CreateVideoProcessor(ve.Get(),0,&vp));
        D3D11_TEXTURE2D_DESC rgb=td;rgb.Format=DXGI_FORMAT_B8G8R8A8_UNORM;rgb.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;check_hresult(device->CreateTexture2D(&rgb,nullptr,&last_frame));
        RECT bounds{0,0,static_cast<LONG>(w),static_cast<LONG>(h)};vc->VideoProcessorSetStreamSourceRect(vp.Get(),0,TRUE,&bounds);vc->VideoProcessorSetStreamDestRect(vp.Get(),0,TRUE,&bounds);vc->VideoProcessorSetOutputTargetRect(vp.Get(),TRUE,&bounds);
        colors();vc->VideoProcessorSetStreamAutoProcessingMode(vp.Get(),0,FALSE);vc->VideoProcessorSetStreamFrameFormat(vp.Get(),0,D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
    }
    void colors() {
        if(!vc||!vp)return;UINT32 matrix=MFVideoTransferMatrix_BT709,range=MFNominalRange_16_235,transfer{};
        output_type->GetUINT32(MF_MT_YUV_MATRIX,&matrix);output_type->GetUINT32(MF_MT_VIDEO_NOMINAL_RANGE,&range);
        if(SUCCEEDED(output_type->GetUINT32(MF_MT_TRANSFER_FUNCTION,&transfer))&&(transfer==MFVideoTransFunc_2084||transfer==MFVideoTransFunc_HLG))throw std::runtime_error("HDR phone video requires tone mapping; SDR capture only");
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE yuv{},rgb{};yuv.YCbCr_Matrix=matrix==MFVideoTransferMatrix_BT709?1:0;yuv.Nominal_Range=range==MFNominalRange_0_255?2:1;rgb.Nominal_Range=2;
        vc->VideoProcessorSetStreamColorSpace(vp.Get(),0,&yuv);vc->VideoProcessorSetOutputColorSpace(vp.Get(),&rgb);
    }
    void choose_output() {
        for(DWORD i=0;;++i){ComPtr<IMFMediaType> t;check_hresult(decoder->GetOutputAvailableType(0,i,&t));GUID subtype{};check_hresult(t->GetGUID(MF_MT_SUBTYPE,&subtype));if(subtype==MFVideoFormat_NV12){check_hresult(decoder->SetOutputType(0,t.Get(),0));output_type=t;colors();return;}}
    }
    void drain() {
        for(int i=0;i<32;++i){
            MFT_OUTPUT_STREAM_INFO info{};check_hresult(decoder->GetOutputStreamInfo(0,&info));ComPtr<IMFSample> out;
            if(!(info.dwFlags&MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)){ComPtr<IMFMediaBuffer>b;check_hresult(MFCreateMemoryBuffer(info.cbSize,&b));check_hresult(MFCreateSample(&out));check_hresult(out->AddBuffer(b.Get()));}
            MFT_OUTPUT_DATA_BUFFER value{};value.pSample=out.Get();DWORD flags{};auto hr=decoder->ProcessOutput(0,1,&value,&flags);if(value.pEvents)value.pEvents->Release();
            if(hr==MF_E_TRANSFORM_NEED_MORE_INPUT)return;
            if(hr==MF_E_TRANSFORM_STREAM_CHANGE){choose_output();continue;}
            check_hresult(hr);if(!out)out.Attach(value.pSample);
            ComPtr<IMFMediaBuffer>b;check_hresult(out->ConvertToContiguousBuffer(&b));BYTE* bytes{};DWORD length{};check_hresult(b->Lock(&bytes,nullptr,&length));
            UINT32 stride{};if(FAILED(output_type->GetUINT32(MF_MT_DEFAULT_STRIDE,&stride)))stride=w;
            if(stride<w || uint64_t(stride)*h*3/2>length){b->Unlock();throw std::runtime_error("Unexpected H.264 decoder surface layout");}
            context->UpdateSubresource(nv12.Get(),0,nullptr,bytes,stride,0);b->Unlock();
            ComPtr<ID3D11Texture2D> back;check_hresult(swapchain->GetBuffer(0,IID_PPV_ARGS(&back)));
            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC id{};id.ViewDimension=D3D11_VPIV_DIMENSION_TEXTURE2D;ComPtr<ID3D11VideoProcessorInputView> iv;check_hresult(vd->CreateVideoProcessorInputView(nv12.Get(),ve.Get(),&id,&iv));
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC od{};od.ViewDimension=D3D11_VPOV_DIMENSION_TEXTURE2D;ComPtr<ID3D11VideoProcessorOutputView> ov;check_hresult(vd->CreateVideoProcessorOutputView(last_frame.Get(),ve.Get(),&od,&ov));
            D3D11_VIDEO_PROCESSOR_STREAM stream{};stream.Enable=TRUE;stream.pInputSurface=iv.Get();check_hresult(vc->VideoProcessorBlt(vp.Get(),ov.Get(),0,1,&stream));has_frame=true;context->CopyResource(back.Get(),last_frame.Get());check_hresult(swapchain->Present(0,0));
        }
    }
    void feed(const std::vector<uint8_t>& bytes,int64_t pts,bool key){std::vector<uint8_t> first;if(!primed){first=initial_config;first.insert(first.end(),bytes.begin(),bytes.end());}auto s=sample(primed?bytes:first,pts*10,10'000'000/fps,key);auto hr=decoder->ProcessInput(0,s.Get(),0);if(hr==MF_E_NOTACCEPTING){drain();hr=decoder->ProcessInput(0,s.Get(),0);}check_hresult(hr);primed=true;drain();}
    void snapshot(const wchar_t* path) {
        if(!has_frame)throw std::runtime_error("No decoded preview frame");
        D3D11_TEXTURE2D_DESC desc{};last_frame->GetDesc(&desc);desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;desc.MiscFlags=0;
        ComPtr<ID3D11Texture2D> staging;check_hresult(device->CreateTexture2D(&desc,nullptr,&staging));context->CopyResource(staging.Get(),last_frame.Get());
        ComPtr<IWICImagingFactory> factory;check_hresult(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory)));
        ComPtr<IWICStream> file;check_hresult(factory->CreateStream(&file));check_hresult(file->InitializeFromFilename(path,GENERIC_WRITE));
        ComPtr<IWICBitmapEncoder> encoder;check_hresult(factory->CreateEncoder(GUID_ContainerFormatPng,nullptr,&encoder));check_hresult(encoder->Initialize(file.Get(),WICBitmapEncoderNoCache));
        ComPtr<IWICBitmapFrameEncode> frame;ComPtr<IPropertyBag2> options;check_hresult(encoder->CreateNewFrame(&frame,&options));check_hresult(frame->Initialize(options.Get()));check_hresult(frame->SetSize(w,h));auto format=GUID_WICPixelFormat32bppBGRA;check_hresult(frame->SetPixelFormat(&format));
        D3D11_MAPPED_SUBRESOURCE mapped{};check_hresult(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));auto hr=frame->WritePixels(h,mapped.RowPitch,mapped.RowPitch*h,static_cast<BYTE*>(mapped.pData));context->Unmap(staging.Get(),0);check_hresult(hr);
        check_hresult(frame->Commit());check_hresult(encoder->Commit());
    }
};
struct Session {
    Runtime runtime;std::mutex mutex;std::atomic_bool stopping{};std::atomic<SOCKET> video{INVALID_SOCKET},audio{INVALID_SOCKET};std::thread worker,audio_worker;
    FtAndroidStatus status{sizeof(FtAndroidStatus)};std::map<uint64_t,std::unique_ptr<Writer>> writers;uint64_t next_writer{};
    std::vector<uint8_t> config;std::unique_ptr<Preview> preview;uint32_t port;bool wants_audio;
    Session(uint32_t port,bool audio,uint32_t fps):port(port),wants_audio(audio){status.qpc_frequency=frequency();status.fps_limit=fps;status.audio_state=audio?0:3;worker=std::thread([this]{run();});}
    ~Session(){stopping=true;for(auto* s:{&video,&audio}){auto value=s->load();if(value!=INVALID_SOCKET)shutdown(value,SD_BOTH);}if(worker.joinable())worker.join();if(audio_worker.joinable())audio_worker.join();for(auto* s:{&video,&audio}){auto value=s->exchange(INVALID_SOCKET);if(value!=INVALID_SOCKET)closesocket(value);}Apartment a;for(auto& [id,w]:writers){try{w->finish(qpc(),status.fps_limit);}catch(...){}}}
    void failure(const wchar_t* message,HRESULT hr=E_FAIL){std::scoped_lock lock(mutex);status.state=3;status.error=hr;wcsncpy_s(status.message,message,_TRUNCATE);}
    void read(SOCKET s,uint8_t* p,size_t n){auto deadline=GetTickCount64()+10'000;while(n){if(stopping)throw std::runtime_error("Android media cancelled");int r=recv(s,reinterpret_cast<char*>(p),static_cast<int>(std::min<size_t>(n,INT_MAX)),0);if(r<0&&WSAGetLastError()==WSAETIMEDOUT&&GetTickCount64()<deadline)continue;if(r<=0)throw std::runtime_error("Android media socket ended or timed out");p+=r;n-=r;}}
    SOCKET connect_socket(){SOCKET s=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);if(s==INVALID_SOCKET)throw std::runtime_error("Cannot create Android socket");DWORD timeout=250;setsockopt(s,SOL_SOCKET,SO_RCVTIMEO,reinterpret_cast<char*>(&timeout),sizeof(timeout));sockaddr_in a{};a.sin_family=AF_INET;a.sin_port=htons(static_cast<u_short>(port));a.sin_addr.s_addr=htonl(INADDR_LOOPBACK);if(connect(s,reinterpret_cast<sockaddr*>(&a),sizeof(a))){closesocket(s);throw std::runtime_error("ADB forward is not ready");}return s;}
    PacketHeader packet(SOCKET s,std::vector<uint8_t>& data){uint8_t head[12];read(s,head,12);auto h=parse_header(head);data.resize(h.size);read(s,data.data(),data.size());return h;}
    void run(){Apartment apartment;try{
        auto deadline=GetTickCount64()+15'000;
        while(!stopping){try{video=connect_socket();uint8_t dummy{};read(video,&dummy,1);if(dummy)throw std::runtime_error("Invalid scrcpy handshake");break;}catch(...){auto s=video.exchange(INVALID_SOCKET);if(s!=INVALID_SOCKET)closesocket(s);if(GetTickCount64()>deadline)throw;std::this_thread::sleep_for(std::chrono::milliseconds(100));}}
        if(stopping)return;
        if(wants_audio){audio=connect_socket();audio_worker=std::thread([this]{run_audio();});}
        uint8_t meta[12];read(video,meta,12);if(be32(meta)!=0x68323634)throw std::runtime_error("Expected scrcpy 3.3.4 H.264 video");
        {std::scoped_lock lock(mutex);status.width=be32(meta+4);status.height=be32(meta+8);if(!status.width||!status.height||status.width>8192||status.height>8192||status.width%2||status.height%2)throw std::runtime_error("Invalid phone video dimensions");}
        std::vector<uint8_t> data;int64_t last=-1;
        while(!stopping){auto h=packet(video,data);auto arrival=qpc();std::scoped_lock lock(mutex);
            if(h.config){if(data.size()>65536)throw std::runtime_error("Oversized H.264 configuration");if(!config.empty()&&data!=config)throw std::runtime_error("Phone encoder configuration changed; stop and reconnect");config=data;continue;}
            if(config.empty())throw std::runtime_error("H.264 configuration missing");
            if(last>=0&&h.pts_us<=last)throw std::runtime_error("Non-monotonic phone video timestamp");last=h.pts_us;
            if(!status.first_qpc){if(!h.key)continue;status.first_qpc=arrival;status.first_pts_us=h.pts_us;status.minimum_offset_qpc=arrival;}
            if(h.pts_us-status.first_pts_us>604800LL*1'000'000)throw std::runtime_error("Android session exceeds seven-day limit");
            auto mapped=status.first_qpc+device_to_qpc(h.pts_us-status.first_pts_us);
            auto offset=arrival-device_to_qpc(h.pts_us-status.first_pts_us);
            status.minimum_offset_qpc=std::min(status.minimum_offset_qpc,offset);status.latest_offset_qpc=offset;status.last_pts_us=h.pts_us;status.frames++;status.bytes+=data.size();
            if(!preview)preview=std::make_unique<Preview>(status.width,status.height,status.fps_limit,config);
            preview->feed(data,h.pts_us,h.key);status.state=preview->has_frame?2:1;
            for(auto& [id,w]:writers){if(!w->status.ready){if(!h.key)continue;w->start(status.width,status.height,status.fps_limit,config,h.pts_us,mapped,status.audio_state==1);}w->video_packet(data,h.pts_us,h.key);}
        }
    }catch(const winrt::hresult_error& e){if(!stopping)failure(e.message().c_str(),e.code());}catch(const std::exception& e){if(!stopping)failure(winrt::to_hstring(e.what()).c_str());}}
    void run_audio(){Apartment a;try{uint8_t meta[4];read(audio,meta,4);auto codec=be32(meta);{std::scoped_lock lock(mutex);if(codec==0||codec==1){status.audio_state=codec==0?2:4;return;}if(codec!=0x00726177)throw std::runtime_error("Expected scrcpy raw PCM audio");status.audio_state=5;}
        std::vector<uint8_t> data;int64_t last=-1;
        while(!stopping){auto h=packet(audio,data);if(h.config||h.size>4096||h.size%4||h.pts_us<=last)throw std::runtime_error("Invalid Android PCM packet or timestamp");last=h.pts_us;std::scoped_lock lock(mutex);status.audio_state=1;for(auto& [id,w]:writers)if(w->status.ready)w->audio_packet(data,h.pts_us);}
    }catch(...){if(!stopping){std::scoped_lock lock(mutex);status.audio_state=4;}}}
};
std::mutex sessions_mutex;std::map<uint64_t,std::shared_ptr<Session>> sessions;uint64_t next_handle{};
std::shared_ptr<Session> get(uint64_t id){std::scoped_lock lock(sessions_mutex);return sessions.at(id);}
template<class F> int32_t safe(F f){try{Apartment a;f();return S_OK;}catch(const winrt::hresult_error& e){return e.code();}catch(...){return E_FAIL;}}
}
int32_t ft_android_create(uint32_t port,uint32_t audio,uint32_t fps,uint64_t* handle){if(!handle||port<1||port>65535||audio>1||(fps!=30&&fps!=60))return E_INVALIDARG;return safe([&]{auto s=std::make_shared<Session>(port,audio!=0,fps);std::scoped_lock lock(sessions_mutex);*handle=++next_handle;sessions[*handle]=std::move(s);});}
void ft_android_destroy(uint64_t id){std::shared_ptr<Session>s;{std::scoped_lock lock(sessions_mutex);auto it=sessions.find(id);if(it!=sessions.end()){s=std::move(it->second);sessions.erase(it);}}}
int32_t ft_android_status(uint64_t id,FtAndroidStatus* status){if(!status||status->size!=sizeof(*status))return E_INVALIDARG;return safe([&]{auto s=get(id);std::scoped_lock lock(s->mutex);*status=s->status;});}
int32_t ft_android_begin(uint64_t id,const wchar_t* video,const wchar_t* audio,uint64_t* segment){if(!video||!*video||!segment)return E_INVALIDARG;return safe([&]{auto s=get(id);std::scoped_lock lock(s->mutex);if(s->status.state!=2||s->writers.size()>=2)throw std::runtime_error("Android source not ready or checkpoint limit reached");auto w=std::make_unique<Writer>();w->video=video;if(audio)w->audio=audio;*segment=++s->next_writer;s->writers[*segment]=std::move(w);});}
int32_t ft_android_segment_status(uint64_t id,uint64_t segment,FtAndroidSegmentStatus* status){if(!status||status->size!=sizeof(*status))return E_INVALIDARG;return safe([&]{auto s=get(id);std::scoped_lock lock(s->mutex);*status=s->writers.at(segment)->status;});}
int32_t ft_android_finish(uint64_t id,uint64_t segment,int64_t boundary,FtAndroidSegmentStatus* status){if(!status||status->size!=sizeof(*status))return E_INVALIDARG;return safe([&]{auto s=get(id);std::scoped_lock lock(s->mutex);auto w=std::move(s->writers.at(segment));s->writers.erase(segment);try{w->finish(boundary,s->status.fps_limit);}catch(const winrt::hresult_error& e){w->status.error=e.code();}*status=w->status;});}
int32_t ft_android_swapchain(uint64_t id,void** swapchain){if(!swapchain)return E_INVALIDARG;*swapchain=nullptr;return safe([&]{auto s=get(id);std::scoped_lock lock(s->mutex);if(!s->preview)throw std::runtime_error("Preview not ready");check_hresult(s->preview->swapchain.CopyTo(reinterpret_cast<IDXGISwapChain1**>(swapchain)));});}
int32_t ft_android_snapshot(uint64_t id,const wchar_t* png){if(!png||!*png)return E_INVALIDARG;return safe([&]{auto s=get(id);std::scoped_lock lock(s->mutex);if(!s->preview)throw std::runtime_error("Preview not ready");s->preview->snapshot(png);});}
