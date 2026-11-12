// SPDX-License-Identifier: LGPL-2.1-or-later
// Out-of-process window acquisition. No target process handles or injection.
#define NOMINMAX
#define WINDOWS_FOUNDATION_UNIVERSALAPICONTRACT_VERSION 0x130000
#define ____FIReference_1_boolean_INTERFACE_DEFINED__
#include <windows.h>
#include <initguid.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <wrl.h>
#include <roapi.h>
#include <windows.foundation.h>
#include <windows.graphics.capture.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.h>
#if __has_include(<windows.graphics.directx.direct3d11.interop.h>)
#include <windows.graphics.directx.direct3d11.interop.h>
#else
namespace Windows::Graphics::DirectX::Direct3D11 {
    MIDL_INTERFACE("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1") IDirect3DDxgiInterfaceAccess : public IUnknown {
        virtual HRESULT STDMETHODCALLTYPE GetInterface(REFIID,void**) = 0;
    };
}
__CRT_UUID_DECL(Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess,0xa9b3d012,0x3df2,0x4ee3,0xb8,0xd1,0x86,0x95,0xf4,0x57,0xd3,0xc1)
extern "C" HRESULT WINAPI CreateDirect3D11DeviceFromDXGIDevice(IDXGIDevice*,IInspectable**);
#endif
#include <atomic>
#include <array>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <cstdio>
#include "bridge_api.h"
using Microsoft::WRL::ComPtr;
using namespace ABI::Windows::Foundation;
using namespace ABI::Windows::Graphics;
using namespace ABI::Windows::Graphics::Capture;
using namespace ABI::Windows::Graphics::DirectX::Direct3D11;
using namespace Windows::Graphics::DirectX::Direct3D11;
using namespace ABI::Windows::Graphics::DirectX;
static void checked(HRESULT hr,const char* operation) { if (FAILED(hr)) { char msg[1024]; snprintf(msg,sizeof(msg),"%s: HRESULT 0x%08lx",operation,(unsigned long)hr); throw std::runtime_error(msg); } }
#define check(operation) checked((operation),#operation)
template<class T> static ComPtr<T> factory(const wchar_t* name) {
    HSTRING_HEADER header{}; HSTRING string{}; ComPtr<T> result;
    check(WindowsCreateStringReference(name,(UINT32)wcslen(name),&header,&string));
    check(RoGetActivationFactory(string,IID_PPV_ARGS(result.GetAddressOf()))); return result;
}
// Agile ABI callbacks, following the WinRT typed-event contract.
template<class I, class Sender, class F> class Callback final : public I, public IAgileObject {
    std::atomic<ULONG> refs{1}; F function;
public:
    explicit Callback(F fn):function(std::move(fn)){}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** p) override {
        if (!p) return E_POINTER; *p=nullptr;
        if (iid==__uuidof(IUnknown)||iid==__uuidof(I)) *p=static_cast<I*>(this);
        else if (iid==__uuidof(IAgileObject)) *p=static_cast<IAgileObject*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override {return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override {auto n=--refs;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE Invoke(Sender* sender,IInspectable* args) override {return function(sender,args);}
};
template<class I,class Sender,class F> static ComPtr<I> callback(F fn) {
    ComPtr<I> result;result.Attach(new Callback<I,Sender,F>(std::move(fn)));return result;
}
// Session5 ABI fallback for compilers whose WinRT headers predate MinUpdateInterval.
struct RwNativePacing : IInspectable {
    virtual HRESULT STDMETHODCALLTYPE get_MinUpdateInterval(TimeSpan*)=0;
    virtual HRESULT STDMETHODCALLTYPE put_MinUpdateInterval(TimeSpan)=0;
};
__CRT_UUID_DECL(RwNativePacing,0x67c0ea62,0x1f85,0x5061,0x92,0x5a,0x23,0x9b,0xe0,0xac,0x09,0xcb)
static const char* pacing_status(const RwPacingStats& p){return !p.requested?"Disabled":!p.supported?"Unavailable":p.applied?"Applied":"RejectedOrUnreadable";}
struct Bridge : std::enable_shared_from_this<Bridge> {
    enum {Free,Writing,Ready,Leased};
    struct Slot {std::atomic<int> state{Free};ComPtr<ID3D11Texture2D> texture;ComPtr<ID3D11RenderTargetView> view;uint64_t sequence=0;int64_t qpc=0;};
    std::array<Slot,8> slots;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext4> context;
    ComPtr<ID3D11Fence> fence;
    ComPtr<IDirect3DDevice> winrt_device;
    ComPtr<IGraphicsCaptureItem> item;
    ComPtr<IDirect3D11CaptureFramePool> pool;
    ComPtr<IGraphicsCaptureSession> session;
    EventRegistrationToken arrived_token{}, closed_token{};
    HANDLE ready=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    HANDLE stopped=nullptr;
    HANDLE completion=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    uint64_t fence_wait=0; // Acquisition-thread only.
    std::mutex callbacks;
    std::atomic<bool> stopping{false},closed{false},error{false};
    std::atomic<uint64_t> arrived{0},copies{0},busy{0},errors{0},resized{0},limited{0},idle{0};
    HWND hwnd=nullptr; HMONITOR monitor=nullptr; SizeInt32 size{};unsigned width=0,height=0,fps=0,opened=0;
    bool source_alive() const {if(monitor){MONITORINFO info{};info.cbSize=sizeof(info);return GetMonitorInfoW(monitor,&info)!=FALSE;}return IsWindow(hwnd)!=FALSE;}
    bool discard_only=false;
    std::atomic<uint64_t> events{0},discarded{0},empty_events{0},callback_skips{0};
    uint64_t sequence=0;int64_t previous_time=0,next_time=0;
    RwPacingStats pacing{};
    HRESULT border_query=E_NOINTERFACE,border_set=E_NOINTERFACE,border_read=E_NOINTERFACE;
    boolean border_required=true;
    unsigned immediate_context=0;
    uint64_t full_copies=0,region_copies=0,clears=0,signals=0,flushes=0,descriptor_queries=0;
    D3D11_TEXTURE2D_DESC source_description{};
    bool source_description_valid=false;
    CO_MTA_USAGE_COOKIE mta_cookie=nullptr;
    ~Bridge(){shutdown();if(ready)CloseHandle(ready);if(stopped)CloseHandle(stopped);if(completion)CloseHandle(completion);if(mta_cookie)CoDecrementMTAUsage(mta_cookie);}
    static void close_object(IUnknown* object) {if(!object)return;ComPtr<IClosable> closable;if(SUCCEEDED(object->QueryInterface(IID_PPV_ARGS(closable.GetAddressOf()))))closable->Close();}
    void shutdown() {
        if(stopping.exchange(true))return;
        SetEvent(stopped);SetEvent(ready);
        if(pool && arrived_token.value)pool->remove_FrameArrived(arrived_token);
        if(item && closed_token.value)item->remove_Closed(closed_token);
        // Do not hold the callback mutex while Close waits for a callback.
        close_object(session.Get());close_object(pool.Get());
        std::lock_guard<std::mutex> guard(callbacks);
        session.Reset();pool.Reset();item.Reset();
    }
    void receive_discard(IDirect3D11CaptureFramePool* sender) noexcept {
        ++events;
        std::unique_lock<std::mutex> guard(callbacks,std::try_to_lock);
        if(!guard.owns_lock()||stopping.load()){++callback_skips;return;}
        ComPtr<IDirect3D11CaptureFrame> frame;
        HRESULT hr=sender->TryGetNextFrame(frame.GetAddressOf());
        if(FAILED(hr)){++errors;error=true;SetEvent(ready);return;}
        if(!frame){++empty_events;return;}
        // K's entire frame path: acquire, Close, release. No surface/size/time inspection.
        ComPtr<IClosable> closable;
        hr=frame.As(&closable);
        if(SUCCEEDED(hr))hr=closable->Close();
        frame.Reset();closable.Reset();
        if(FAILED(hr)){++errors;error=true;SetEvent(ready);return;}
        ++arrived;++discarded;
    }
    void receive(IDirect3D11CaptureFramePool* sender) noexcept {
        std::unique_lock<std::mutex> guard(callbacks,std::try_to_lock);
        if(!guard.owns_lock()||stopping.load())return;
        ComPtr<IDirect3D11CaptureFrame> frame;
        int claimed=-1;
        try {
            check(sender->TryGetNextFrame(frame.GetAddressOf()));if(!frame)return;
            ++arrived;
            TimeSpan timestamp{};check(frame->get_SystemRelativeTime(&timestamp));
            if(timestamp.Duration<=previous_time){++idle;close_object(frame.Get());return;}
            previous_time=timestamp.Duration;
            if(timestamp.Duration<next_time){++limited;close_object(frame.Get());return;}
            // Skip kernel/metadata calls for frames the 60Hz application limiter will discard.
            SizeInt32 content{};check(frame->get_ContentSize(&content));
            if(content.Width<=0||content.Height<=0||(!monitor&&IsIconic(hwnd))){++idle;close_object(frame.Get());return;}
            if(content.Width!=size.Width||content.Height!=size.Height) {
                // Release all outstanding WGC frames before Recreate. Backend dimensions stay fixed.
                close_object(frame.Get());frame.Reset();
                ComPtr<IDirect3D11CaptureFrame> pending;
                while(SUCCEEDED(sender->TryGetNextFrame(pending.ReleaseAndGetAddressOf())) && pending)close_object(pending.Get());
                check(sender->Recreate(winrt_device.Get(),DirectXPixelFormat_B8G8R8A8UIntNormalized,2,content));
                size=content;source_description_valid=false;++resized;return;
            }
            for(unsigned i=0;i<slots.size();++i){int expected=Free;if(slots[i].state.compare_exchange_strong(expected,Writing,std::memory_order_acquire)){claimed=(int)i;break;}}
            if(claimed<0){++busy;close_object(frame.Get());return;}
            ComPtr<IDirect3DSurface> surface;ComPtr<IDirect3DDxgiInterfaceAccess> access;ComPtr<ID3D11Texture2D> texture;
            check(frame->get_Surface(surface.GetAddressOf()));check(surface.As(&access));check(access->GetInterface(IID_PPV_ARGS(texture.GetAddressOf())));
            // All buffers in this WGC pool generation share format/dimensions. Refresh on Recreate.
            if(!source_description_valid){texture->GetDesc(&source_description);source_description_valid=true;++descriptor_queries;}
            const D3D11_TEXTURE2D_DESC& desc=source_description;
            if(desc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM)throw std::runtime_error("Unsupported WGC texture format");
            auto& slot=slots[claimed];
            if((unsigned)content.Width==width && (unsigned)content.Height==height && desc.Width==width && desc.Height==height)
            {context->CopyResource(slot.texture.Get(),texture.Get());++full_copies;}
            else {
                // GPU-only centered crop/pad; never resize the eight leased backend textures.
                const float black[4]={0,0,0,1};context->ClearRenderTargetView(slot.view.Get(),black);++clears;
                unsigned cw=std::min(width,std::min(desc.Width,(unsigned)content.Width)),ch=std::min(height,std::min(desc.Height,(unsigned)content.Height));
                unsigned sx=((unsigned)content.Width-cw)/2,sy=((unsigned)content.Height-ch)/2;
                D3D11_BOX box{sx,sy,0,sx+cw,sy+ch,1};
                context->CopySubresourceRegion(slot.texture.Get(),0,(width-cw)/2,(height-ch)/2,0,texture.Get(),0,&box);++region_copies;
            }
            slot.sequence=++sequence;slot.qpc=timestamp.Duration;
            check(context->Signal(fence.Get(),sequence));++signals;
            // One submission per accepted frame; shared-device visibility requires a flush.
            // No deferred command lists, state rebinding, CPU readback, or wait in this callback.
            context->Flush();++flushes;
            // Close WGC's two-buffer lease immediately after GPU submission, never after encoding.
            close_object(frame.Get());frame.Reset();
            ++copies;
            slot.state.store(Ready,std::memory_order_release);claimed=-1;
            const int64_t interval=10000000/fps;
            if(!next_time)next_time=timestamp.Duration;
            next_time+=((timestamp.Duration-next_time)/interval+1)*interval;
            SetEvent(ready);
        }catch(const std::exception& e){
            close_object(frame.Get());if(claimed>=0)slots[claimed].state.store(Free,std::memory_order_release);
            fprintf(stderr,"rewind WGC frame failure: %s\n",e.what());++errors;error=true;SetEvent(ready);
        }
    }
};
struct RwWgcBridge {std::shared_ptr<Bridge> impl;};
static int start_bridge(ID3D11Device* owner,ID3D11Texture2D* const* textures,unsigned width,unsigned height,unsigned fps,HWND hwnd,RwWgcBridge** output,bool discard_only=false,unsigned adapter_index=0,bool primary_monitor=false) {
    HMONITOR monitor=primary_monitor?MonitorFromPoint(POINT{0,0},MONITOR_DEFAULTTOPRIMARY):nullptr;
    if((!discard_only&&(!owner||!textures))||!output||(primary_monitor?!monitor:!IsWindow(hwnd))||!width||!height||fps<1||fps>120)return -1;
    *output=nullptr;
    try {
        auto b=std::make_shared<Bridge>();b->hwnd=hwnd;b->monitor=monitor;b->width=width;b->height=height;b->fps=fps;b->discard_only=discard_only;
        // Keep the process MTA alive for agile callbacks; balance apartment initialization on this thread.
        check(CoIncrementMTAUsage(&b->mta_cookie));
        struct Apartment {HRESULT hr=RoInitialize(RO_INIT_MULTITHREADED);~Apartment(){if(SUCCEEDED(hr))RoUninitialize();}} apartment;
        check(apartment.hr);
        wchar_t stop_name[96];swprintf(stop_name,96,L"Local\\REWIND.WgcStop.%lu",GetCurrentProcessId());
        b->stopped=CreateEventW(nullptr,TRUE,FALSE,stop_name);
        if(!b->ready||!b->stopped||!b->completion)throw std::runtime_error("WGC event creation failed");
        auto supported=factory<IGraphicsCaptureSessionStatics>(RuntimeClass_Windows_Graphics_Capture_GraphicsCaptureSession);
        boolean yes=false;check(supported->IsSupported(&yes));if(!yes)throw std::runtime_error("WGC unsupported or disabled by policy");
        ComPtr<IDXGIDevice> dxgi;ComPtr<IDXGIAdapter> adapter;
        if(owner){check(owner->QueryInterface(IID_PPV_ARGS(dxgi.GetAddressOf())));check(dxgi->GetAdapter(adapter.GetAddressOf()));}
        else{ComPtr<IDXGIFactory1> factory;ComPtr<IDXGIAdapter1> selected;check(CreateDXGIFactory1(IID_PPV_ARGS(factory.GetAddressOf())));check(factory->EnumAdapters1(adapter_index,selected.GetAddressOf()));check(selected.As(&adapter));}
        ComPtr<ID3D11DeviceContext> context;
        // A separate immediate context prevents callback/encoder context races. Same adapter, shared VRAM.
        check(D3D11CreateDevice(adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,b->device.GetAddressOf(),nullptr,context.GetAddressOf()));
        b->immediate_context=context->GetType()==D3D11_DEVICE_CONTEXT_IMMEDIATE;
        if(!b->immediate_context)throw std::runtime_error("WGC acquisition must use an immediate context");
        if(!discard_only){
        check(context.As(&b->context));ComPtr<ID3D11Device5> device5;ComPtr<ID3D11Device1> device1;check(b->device.As(&device5));check(b->device.As(&device1));
        check(device5->CreateFence(0,D3D11_FENCE_FLAG_NONE,IID_PPV_ARGS(b->fence.GetAddressOf())));
        for(unsigned i=0;i<8;++i){
            D3D11_TEXTURE2D_DESC desc{};textures[i]->GetDesc(&desc);
            if(desc.Width!=width||desc.Height!=height||desc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM||desc.ArraySize!=1||desc.SampleDesc.Count!=1)throw std::runtime_error("Incompatible backend pool");
            ComPtr<IDXGIResource1> resource;check(textures[i]->QueryInterface(IID_PPV_ARGS(resource.GetAddressOf())));
            HANDLE handle=nullptr;check(resource->CreateSharedHandle(nullptr,DXGI_SHARED_RESOURCE_READ|DXGI_SHARED_RESOURCE_WRITE,nullptr,&handle));
            HRESULT opened=device1->OpenSharedResource1(handle,IID_PPV_ARGS(b->slots[i].texture.GetAddressOf()));CloseHandle(handle);check(opened);
            check(b->device->CreateRenderTargetView(b->slots[i].texture.Get(),nullptr,b->slots[i].view.GetAddressOf()));++b->opened;
        }
        } // Discard mode never creates a fence, backend texture, view or shared handle.
        dxgi.Reset();check(b->device.As(&dxgi));ComPtr<IInspectable> inspectable;
        check(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(),inspectable.GetAddressOf()));check(inspectable.As(&b->winrt_device));
        auto interop=factory<IGraphicsCaptureItemInterop>(RuntimeClass_Windows_Graphics_Capture_GraphicsCaptureItem);
        HRESULT item_result=monitor?interop->CreateForMonitor(monitor,IID_PPV_ARGS(b->item.GetAddressOf())):interop->CreateForWindow(hwnd,IID_PPV_ARGS(b->item.GetAddressOf()));
        check(item_result);check(b->item->get_Size(&b->size));
        auto statics=factory<IDirect3D11CaptureFramePoolStatics2>(RuntimeClass_Windows_Graphics_Capture_Direct3D11CaptureFramePool);
        check(statics->CreateFreeThreaded(b->winrt_device.Get(),DirectXPixelFormat_B8G8R8A8UIntNormalized,2,b->size,b->pool.GetAddressOf()));
        check(b->pool->CreateCaptureSession(b->item.Get(),b->session.GetAddressOf()));
        // Report the Windows indicator state. The beta leaves border consent to Windows.
        ComPtr<IGraphicsCaptureSession3> border;
        b->border_query=b->session.As(&border);
        if(SUCCEEDED(b->border_query)){
            b->border_read=border->get_IsBorderRequired(&b->border_required);
        }
        wchar_t requested[8]{};
        DWORD request_length=GetEnvironmentVariableW(L"REWIND_WGC_NATIVE_PACING",requested,8);
        b->pacing.requested=!(request_length==1 && requested[0]==L'0');
        b->pacing.requested_interval=(10000000+fps-1)/fps; // ceil: never request faster than target.
        b->pacing.set_hr=b->pacing.read_hr=S_FALSE;
        ComPtr<RwNativePacing> native_pacing;
        b->pacing.query_hr=b->session.As(&native_pacing);
        b->pacing.supported=SUCCEEDED(b->pacing.query_hr);
        if(b->pacing.requested && native_pacing){
            TimeSpan interval{b->pacing.requested_interval},effective{};
            b->pacing.set_hr=native_pacing->put_MinUpdateInterval(interval);
            if(SUCCEEDED(b->pacing.set_hr)){
                b->pacing.read_hr=native_pacing->get_MinUpdateInterval(&effective);
                if(SUCCEEDED(b->pacing.read_hr))b->pacing.effective_interval=effective.Duration;
                b->pacing.applied=SUCCEEDED(b->pacing.read_hr) && effective.Duration==interval.Duration;
            }
        }
        ComPtr<IGraphicsCaptureSession2> session2;if(SUCCEEDED(b->session.As(&session2)))check(session2->put_IsCursorCaptureEnabled(false));
        std::weak_ptr<Bridge> weak=b;
        check(b->pool->add_FrameArrived(callback<ITypedEventHandler<Direct3D11CaptureFramePool*,IInspectable*>,IDirect3D11CaptureFramePool>([weak](auto sender,auto){if(auto p=weak.lock()){if(p->discard_only)p->receive_discard(sender);else p->receive(sender);}return S_OK;}).Get(),&b->arrived_token));
        check(b->item->add_Closed(callback<ITypedEventHandler<GraphicsCaptureItem*,IInspectable*>,IGraphicsCaptureItem>([weak](auto,auto){if(auto p=weak.lock()){p->closed=true;SetEvent(p->ready);}return S_OK;}).Get(),&b->closed_token));
        check(b->session->StartCapture());
        *output=new RwWgcBridge{b};return 0;
    }catch(const std::exception& e){fprintf(stderr,"rewind WGC initialization failed: %s\n",e.what());return -1;}
}
extern "C" __declspec(dllexport) int WINAPI RwWgcBridgeBorderInfo(RwWgcBridge* wrapper,char* output,unsigned capacity){
    if(!wrapper||!output||!capacity)return -1;auto b=wrapper->impl;
    const char* state=FAILED(b->border_query)?"Unavailable":FAILED(b->border_read)?"ReadbackUnavailable":b->border_required?"BorderRequired":"BorderNotRequired";
    int count=snprintf(output,capacity,"{\"HideRequested\":false,\"InterfaceSupported\":%s,\"QueryHResult\":%ld,\"SetHResult\":%ld,\"ReadHResult\":%ld,\"BorderRequiredReadback\":%s,\"Status\":\"%s\"}",
        SUCCEEDED(b->border_query)?"true":"false",(long)b->border_query,(long)b->border_set,(long)b->border_read,FAILED(b->border_read)?"null":b->border_required?"true":"false",state);
    return count>0&&(unsigned)count<capacity?0:-1;
}
extern "C" __declspec(dllexport) int WINAPI RwWgcBridgeStart(ID3D11Device* owner,ID3D11Texture2D* const* textures,unsigned width,unsigned height,unsigned fps,HWND hwnd,RwWgcBridge** output) {
    return start_bridge(owner,textures,width,height,fps,hwnd,output);
}
// Normal product entry: only the primary display; window capture is retained for isolated integrity tests.
extern "C" __declspec(dllexport) int WINAPI RwWgcBridgeStartPrimary(ID3D11Device* owner,ID3D11Texture2D* const* textures,unsigned width,unsigned height,unsigned fps,HWND,RwWgcBridge** output) {
    return start_bridge(owner,textures,width,height,fps,nullptr,output,false,0,true);
}
// Standalone diagnostic entry. FFmpeg/queue/NVENC are never loaded by this worker.
extern "C" __declspec(dllexport) int WINAPI RwWgcDiscardRun(HWND hwnd,unsigned seconds,unsigned adapter_index) {
    if(seconds<1||seconds>3600)return 1;
    RwWgcBridge* wrapper=nullptr;
    if(start_bridge(nullptr,nullptr,1,1,60,hwnd,&wrapper,true,adapter_index)<0)return 1;
    auto b=wrapper->impl;
    char border_json[768];if(RwWgcBridgeBorderInfo(wrapper,border_json,sizeof(border_json))==0)printf("rewind_wgc_border=%s\n",border_json);
    LARGE_INTEGER frequency,start,now;QueryPerformanceFrequency(&frequency);QueryPerformanceCounter(&start);
    int64_t next_sample=start.QuadPart+frequency.QuadPart;
    uint64_t previous_events=0,previous_frames=0;double previous_seconds=0;
    bool interrupted=false;int result=0;
    for(;;){
        QueryPerformanceCounter(&now);
        double elapsed=double(now.QuadPart-start.QuadPart)/frequency.QuadPart;
        if(now.QuadPart>=next_sample||elapsed>=seconds){
            uint64_t events=b->events.load(),frames=b->discarded.load();double interval=elapsed-previous_seconds;
            printf("rewind_wgc_discard_sample={\"ElapsedSeconds\":%.6f,\"FrameEvents\":%llu,\"DiscardedFrames\":%llu,\"EventRate\":%.6f,\"DiscardRate\":%.6f}\n",elapsed,(unsigned long long)events,(unsigned long long)frames,(events-previous_events)/interval,(frames-previous_frames)/interval);
            fflush(stdout);previous_events=events;previous_frames=frames;previous_seconds=elapsed;
            next_sample=now.QuadPart+frequency.QuadPart;
        }
        if(b->error){result=1;break;}
        if(b->closed||!IsWindow(hwnd)){b->closed=true;break;}
        if(elapsed>=seconds)break;
        HANDLE signals[]={b->stopped,b->ready};
        DWORD wait=WaitForMultipleObjects(2,signals,FALSE,(DWORD)std::max(1.0,std::min(1000.0,(seconds-elapsed)*1000.0)));
        if(wait==WAIT_OBJECT_0){interrupted=true;break;}
        if(wait==WAIT_FAILED){result=1;break;}
    }
    QueryPerformanceCounter(&now);
    b->shutdown();
    if(b->opened || b->fence || b->context || b->copies.load())result=1;
    double elapsed=double(now.QuadPart-start.QuadPart)/frequency.QuadPart;
    printf("rewind_wgc_discard_stats={\"Api\":\"WGC\",\"Mode\":\"RawDiscard\",\"FramePoolCapacity\":2,\"BackendPoolCapacity\":0,\"FrameEvents\":%llu,\"ArrivedFrames\":%llu,\"DiscardedFrames\":%llu,\"EmptyEvents\":%llu,\"CallbackSkips\":%llu,\"AcquisitionErrors\":%llu,\"GpuCopySubmissions\":0,\"PublishedFrames\":0,\"EncodedFrames\":0,\"OpenedNtTextures\":0,\"OpenedHardwareFence\":0,\"ElapsedSeconds\":%.6f,\"AverageEventRate\":%.6f,\"AverageDiscardRate\":%.6f,\"TargetClosed\":%s,\"StopRequested\":%s,\"CleanShutdown\":%s}\n",
        (unsigned long long)b->events.load(),(unsigned long long)b->arrived.load(),(unsigned long long)b->discarded.load(),(unsigned long long)b->empty_events.load(),(unsigned long long)b->callback_skips.load(),(unsigned long long)b->errors.load(),elapsed,b->events.load()/elapsed,b->discarded.load()/elapsed,b->closed?"true":"false",interrupted?"true":"false",result==0&&!b->error?"true":"false");
    printf("rewind_wgc_pacing={\"Requested\":%s,\"Supported\":%s,\"Applied\":%s,\"Status\":\"%s\",\"QueryHResult\":%ld,\"SetHResult\":%ld,\"ReadHResult\":%ld,\"RequestedInterval100ns\":%lld,\"EffectiveInterval100ns\":%lld,\"ImmediateContext\":%s}\n",
        b->pacing.requested?"true":"false",b->pacing.supported?"true":"false",b->pacing.applied?"true":"false",pacing_status(b->pacing),(long)b->pacing.query_hr,(long)b->pacing.set_hr,(long)b->pacing.read_hr,(long long)b->pacing.requested_interval,(long long)b->pacing.effective_interval,b->immediate_context?"true":"false");
    fflush(stdout);if(b->error)result=1;delete wrapper;return result;
}
extern "C" __declspec(dllexport) int WINAPI RwWgcBridgeNext(RwWgcBridge* wrapper,RwWgcFrame* frame) {
    if(!wrapper||!frame)return -1;auto b=wrapper->impl;
    if(b->error)return -1;if(b->stopping || WaitForSingleObject(b->stopped,0)==WAIT_OBJECT_0)return 2;
    int index=-1;uint64_t oldest=UINT64_MAX;
    for(unsigned i=0;i<8;++i)if(b->slots[i].state.load(std::memory_order_acquire)==Bridge::Ready && b->slots[i].sequence<oldest){oldest=b->slots[i].sequence;index=(int)i;}
    if(index<0){if(b->closed||!b->source_alive()){b->closed=true;return 2;}WaitForSingleObject(b->ready,2);return 0;}
    // Never wait for GPU completion on the WGC dispatcher. Only expose completed copies.
    uint64_t done=b->fence->GetCompletedValue();if(done==UINT64_MAX)return -1;
    if(done<oldest){
        // Hardware completion wakes immediately: no 1ms timer rounding on a sub-ms GPU copy.
        if(b->fence_wait!=oldest){
            if(FAILED(b->fence->SetEventOnCompletion(oldest,b->completion))){++b->errors;b->error=true;return -1;}
            b->fence_wait=oldest;
        }
        HANDLE signals[]={b->stopped,b->completion,b->ready};
        DWORD wait=WaitForMultipleObjects(3,signals,FALSE,2);
        if(wait==WAIT_FAILED){++b->errors;b->error=true;return -1;}
        return 0; // Recheck fence and surface ownership; never expose an incomplete GPU copy.
    }
    auto& slot=b->slots[index];int expected=Bridge::Ready;
    if(!slot.state.compare_exchange_strong(expected,Bridge::Leased,std::memory_order_acquire))return 0;
    *frame={(unsigned)index,slot.sequence,slot.qpc};return 1;
}
extern "C" __declspec(dllexport) int WINAPI RwWgcBridgeRelease(RwWgcBridge* w,unsigned i){if(!w||i>=8)return -1;int state=Bridge::Leased;return w->impl->slots[i].state.compare_exchange_strong(state,Bridge::Free,std::memory_order_release)?0:-1;}
extern "C" __declspec(dllexport) void WINAPI RwWgcBridgeWake(RwWgcBridge* w){if(w)SetEvent(w->impl->ready);}
extern "C" __declspec(dllexport) void WINAPI RwWgcBridgeStop(RwWgcBridge* w){if(w)w->impl->shutdown();}
extern "C" __declspec(dllexport) int WINAPI RwWgcBridgeClose(RwWgcBridge* w,RwWgcStats* stats){
    if(!w||!stats)return -1;auto b=w->impl;b->shutdown();
    *stats={b->arrived.load(),b->copies.load(),b->busy.load(),b->errors.load(),b->resized.load(),b->limited.load(),b->idle.load(),b->opened,1,(unsigned)b->closed.load(),10000000,{},0,0,0,0,0,0,0};
    stats->pacing=b->pacing;stats->immediate_context=b->immediate_context;
    stats->full_copies=b->full_copies;stats->region_copies=b->region_copies;stats->clears=b->clears;
    stats->signals=b->signals;stats->flushes=b->flushes;stats->descriptor_queries=b->descriptor_queries;
    int result=b->error?-1:0;delete w;return result;
}
