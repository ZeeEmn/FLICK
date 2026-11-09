// SPDX-License-Identifier: LGPL-2.1-or-later
// Primary-display DXGI acquisition only: no WinRT, injection, game process handles, or CPU pixel maps.
#define NOMINMAX
#include <windows.h>
#include <initguid.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <wrl.h>
#include <atomic>
#include <array>
#include <memory>
#include <stdexcept>
#include <algorithm>
#include <cstdio>
#include "bridge_api.h"
using Microsoft::WRL::ComPtr;
static void checked(HRESULT hr,const char* op){if(FAILED(hr)){char message[512];snprintf(message,sizeof(message),"%s: HRESULT 0x%08lx",op,(unsigned long)hr);throw std::runtime_error(message);}}
#define check(op) checked((op),#op)
struct Bridge {
    enum{Free,Writing,Ready,Leased};
    struct Slot{std::atomic<int> state{Free};ComPtr<ID3D11Texture2D> texture;ComPtr<ID3D11RenderTargetView> view;uint64_t sequence=0;int64_t qpc=0;};
    std::array<Slot,8> slots;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext4> context;
    ComPtr<ID3D11Fence> fence;
    ComPtr<IDXGIAdapter> adapter;
    ComPtr<IDXGIOutputDuplication> duplication;
    HANDLE ready=CreateEventW(nullptr,FALSE,FALSE,nullptr),stopped=nullptr,completion=CreateEventW(nullptr,FALSE,FALSE,nullptr),timer=nullptr,worker=nullptr,reset=nullptr,fault=nullptr;
    std::atomic<bool> stopping{false},closed{false},error{false};
    uint64_t fence_wait=0,sequence=0;
    unsigned width=0,height=0,fps=0,opened=0,immediate_context=0;
    LARGE_INTEGER frequency{};
    std::atomic<uint64_t> arrived{0},copies{0},busy{0},errors{0},resized{0},limited{0},idle{0};
    uint64_t full_copies=0,region_copies=0,clears=0,signals=0,flushes=0,descriptor_queries=0,releases=0,timeouts=0,recoveries=0,max_hold_us=0,protected_frames=0;
    D3D11_TEXTURE2D_DESC source_desc{};bool desc_valid=false;
    D3D11_BOX copy_box{};unsigned destination_x=0,destination_y=0,generation=0;bool full_copy=false,needs_clear=false;
    std::array<unsigned,8> cleared_generation{};
    uint64_t accumulated_batches=0,coalesced_updates=0,max_accumulated=0;
    int64_t previous_present_qpc=0;
    ~Bridge(){shutdown();for(HANDLE h:{ready,stopped,completion,timer,reset,fault})if(h)CloseHandle(h);}
    int64_t now() const {LARGE_INTEGER value;QueryPerformanceCounter(&value);return value.QuadPart;}
    int64_t units(int64_t qpc) const{return(qpc/frequency.QuadPart)*10000000+(qpc%frequency.QuadPart)*10000000/frequency.QuadPart;}
    bool wait_for(int64_t deadline){
        int64_t remaining=deadline-now();if(remaining<=0)return !stopping;
        LARGE_INTEGER due;due.QuadPart=-std::max<int64_t>(1,units(remaining));
        if(!SetWaitableTimer(timer,&due,0,nullptr,nullptr,FALSE))throw std::runtime_error("Capture timer failed");
        HANDLE events[]={stopped,timer};DWORD result=WaitForMultipleObjects(2,events,FALSE,INFINITE);
        if(result==WAIT_FAILED)throw std::runtime_error("Capture timer wait failed");
        return result!=WAIT_OBJECT_0&&!stopping;
    }
    HRESULT duplicate(){
        duplication.Reset();desc_valid=false;
        HMONITOR primary=MonitorFromPoint(POINT{0,0},MONITOR_DEFAULTTOPRIMARY);
        for(unsigned i=0;;++i){
            ComPtr<IDXGIOutput> output;HRESULT hr=adapter->EnumOutputs(i,output.GetAddressOf());if(hr==DXGI_ERROR_NOT_FOUND)return DXGI_ERROR_NOT_FOUND;if(FAILED(hr))return hr;
            DXGI_OUTPUT_DESC description{};hr=output->GetDesc(&description);if(FAILED(hr))return hr;
            if(!description.AttachedToDesktop||description.Monitor!=primary)continue;
            if(description.Rotation!=DXGI_MODE_ROTATION_IDENTITY&&description.Rotation!=DXGI_MODE_ROTATION_UNSPECIFIED)return DXGI_ERROR_UNSUPPORTED;
            ComPtr<IDXGIOutput1> output1;hr=output.As(&output1);if(FAILED(hr))return hr;
            hr=output1->DuplicateOutput(device.Get(),duplication.GetAddressOf());if(FAILED(hr))return hr;
            DXGI_OUTDUPL_DESC desc{};duplication->GetDesc(&desc);
            if(desc.ModeDesc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM)return DXGI_ERROR_UNSUPPORTED;
            source_desc.Width=desc.ModeDesc.Width;source_desc.Height=desc.ModeDesc.Height;source_desc.Format=desc.ModeDesc.Format;desc_valid=true;++descriptor_queries;++generation;
            unsigned cw=std::min(width,source_desc.Width),ch=std::min(height,source_desc.Height),sx=(source_desc.Width-cw)/2,sy=(source_desc.Height-ch)/2;
            copy_box={sx,sy,0,sx+cw,sy+ch,1};destination_x=(width-cw)/2;destination_y=(height-ch)/2;
            full_copy=source_desc.Width==width&&source_desc.Height==height;needs_clear=cw<width||ch<height;
            return S_OK;
        }
    }
    void shutdown(){if(stopping.exchange(true))return;SetEvent(stopped);SetEvent(ready);if(worker){WaitForSingleObject(worker,INFINITE);CloseHandle(worker);worker=nullptr;}}
    void run() noexcept {
        int claimed=-1;bool held=false;[[maybe_unused]] int64_t acquired=0;int64_t next=now();const int64_t interval=frequency.QuadPart/fps;
        // QPC starts before Acquire; the reported hold duration is a conservative upper bound.
        auto release=[&](){if(!held)return;HRESULT hr=duplication->ReleaseFrame();held=false;++releases;
#ifndef REWIND_RELEASE_QUIET
            max_hold_us=std::max<uint64_t>(max_hold_us,(uint64_t)((now()-acquired)*1000000/frequency.QuadPart));
#endif
            if(FAILED(hr)&&hr!=DXGI_ERROR_ACCESS_LOST)check(hr);};
        try{
            while(!stopping&&WaitForSingleObject(stopped,0)!=WAIT_OBJECT_0){
                if(!wait_for(next))break;
                if(!duplication){
                    HRESULT retry=duplicate();if(retry==E_ACCESSDENIED||retry==DXGI_ERROR_ACCESS_DENIED||retry==DXGI_ERROR_NOT_CURRENTLY_AVAILABLE||retry==DXGI_ERROR_UNSUPPORTED){if(!wait_for(now()+frequency.QuadPart/4))break;continue;}check(retry);++resized;
                }
                // Reserve a writable backend texture and prepare padding before owning a DXGI frame.
                for(unsigned i=0;i<slots.size();++i){int expected=Free;if(slots[i].state.compare_exchange_strong(expected,Writing,std::memory_order_acquire)){claimed=(int)i;break;}}
                if(claimed<0){++busy;if(!wait_for(now()+frequency.QuadPart/1000))break;continue;}
                auto& slot=slots[claimed];
                if(needs_clear&&cleared_generation[claimed]!=generation){const float black[4]={0,0,0,1};context->ClearRenderTargetView(slot.view.Get(),black);cleared_generation[claimed]=generation;++clears;}
                DXGI_OUTDUPL_FRAME_INFO info{};ComPtr<IDXGIResource> resource;ComPtr<ID3D11Texture2D> texture;
                // Bounded kernel wait: wake as soon as a frame arrives, without zero-timeout retries.
#ifndef REWIND_RELEASE_QUIET
                acquired=now();
#endif
                HRESULT hr=duplication->AcquireNextFrame(5,&info,resource.GetAddressOf());
                if(hr==DXGI_ERROR_WAIT_TIMEOUT){slot.state.store(Free,std::memory_order_release);claimed=-1;++timeouts;SwitchToThread();if(!wait_for(now()+frequency.QuadPart/1000))break;continue;}
                if(hr==DXGI_ERROR_ACCESS_LOST||hr==DXGI_ERROR_DEVICE_REMOVED||hr==DXGI_ERROR_DEVICE_RESET){slot.state.store(Free,std::memory_order_release);claimed=-1;
#ifndef REWIND_RELEASE_QUIET
                    fprintf(stderr,"rewind_recovery_requested=0x%08lx\n",(unsigned long)hr);
#endif
                    error=true;++recoveries;break;}
                check(hr);held=true;
                if(info.ProtectedContentMaskedOut||!info.LastPresentTime.QuadPart||info.LastPresentTime.QuadPart<=previous_present_qpc){
                    release();++arrived;if(info.ProtectedContentMaskedOut)++protected_frames;else ++idle;slot.state.store(Free,std::memory_order_release);claimed=-1;if(!wait_for(now()+frequency.QuadPart/1000))break;continue;
                }
                check(resource.As(&texture));
                if(full_copy)context->CopyResource(slot.texture.Get(),texture.Get());
                else context->CopySubresourceRegion(slot.texture.Get(),0,destination_x,destination_y,0,texture.Get(),0,&copy_box);
                // Invariant: same-thread ReleaseFrame immediately after the copy, before fence/queue work.
                release();texture.Reset();resource.Reset();
                ++arrived;if(full_copy)++full_copies;else ++region_copies;
                // AccumulatedFrames are updates already coalesced into THIS surface, not queued textures.
                if(info.AccumulatedFrames>1){++accumulated_batches;coalesced_updates+=info.AccumulatedFrames-1;}
                max_accumulated=std::max<uint64_t>(max_accumulated,info.AccumulatedFrames);
                slot.sequence=++sequence;slot.qpc=units(info.LastPresentTime.QuadPart);
                check(context->Signal(fence.Get(),sequence));++signals;context->Flush();++flushes;
                previous_present_qpc=info.LastPresentTime.QuadPart;++copies;slot.state.store(Ready,std::memory_order_release);claimed=-1;SetEvent(ready);
                int64_t current=now();next+=((std::max(current,next)-next)/interval+1)*interval;
            }
        }catch(const std::exception& e){
            try{release();}catch(...){}fprintf(stderr,"rewind desktop acquisition failed: %s\n",e.what());++errors;error=true;
        }
        if(claimed>=0)slots[claimed].state.store(Free,std::memory_order_release);
        duplication.Reset();SetEvent(ready);
    }
};
struct RwDesktopBridge{std::shared_ptr<Bridge> impl;};
extern "C" __declspec(dllexport) int WINAPI RwDesktopBridgeStart(ID3D11Device* owner,ID3D11Texture2D* const* textures,unsigned width,unsigned height,unsigned fps,RwDesktopBridge** output){
    if(!owner||!textures||!output||!width||!height||fps<1||fps>120)return -1;
    *output=nullptr;
    try{
        auto b=std::make_shared<Bridge>();b->width=width;b->height=height;b->fps=fps;if(!QueryPerformanceFrequency(&b->frequency))throw std::runtime_error("QPC unavailable");
        wchar_t name[96];swprintf(name,96,L"Local\\REWIND.DesktopStop.%lu",GetCurrentProcessId());b->stopped=CreateEventW(nullptr,TRUE,FALSE,name);
        b->timer=CreateWaitableTimerExW(nullptr,nullptr,0x2,TIMER_ALL_ACCESS);if(!b->timer)b->timer=CreateWaitableTimerW(nullptr,FALSE,nullptr);
        if(!b->ready||!b->stopped||!b->completion||!b->timer)throw std::runtime_error("Capture event creation failed");
        ComPtr<IDXGIDevice> dxgi;check(owner->QueryInterface(IID_PPV_ARGS(dxgi.GetAddressOf())));check(dxgi->GetAdapter(b->adapter.GetAddressOf()));
        ComPtr<ID3D11DeviceContext> context;check(D3D11CreateDevice(b->adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,b->device.GetAddressOf(),nullptr,context.GetAddressOf()));
        b->immediate_context=context->GetType()==D3D11_DEVICE_CONTEXT_IMMEDIATE;check(context.As(&b->context));
        ComPtr<ID3D11Device5> device5;ComPtr<ID3D11Device1> device1;check(b->device.As(&device5));check(b->device.As(&device1));check(device5->CreateFence(0,D3D11_FENCE_FLAG_NONE,IID_PPV_ARGS(b->fence.GetAddressOf())));
        for(unsigned i=0;i<8;++i){
            D3D11_TEXTURE2D_DESC desc{};textures[i]->GetDesc(&desc);if(desc.Width!=width||desc.Height!=height||desc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM||desc.ArraySize!=1||desc.SampleDesc.Count!=1)throw std::runtime_error("Incompatible backend pool");
            ComPtr<IDXGIResource1> resource;check(textures[i]->QueryInterface(IID_PPV_ARGS(resource.GetAddressOf())));HANDLE handle=nullptr;check(resource->CreateSharedHandle(nullptr,DXGI_SHARED_RESOURCE_READ|DXGI_SHARED_RESOURCE_WRITE,nullptr,&handle));
            HRESULT opened=device1->OpenSharedResource1(handle,IID_PPV_ARGS(b->slots[i].texture.GetAddressOf()));CloseHandle(handle);check(opened);check(b->device->CreateRenderTargetView(b->slots[i].texture.Get(),nullptr,b->slots[i].view.GetAddressOf()));++b->opened;
        }
        check(b->duplicate());b->worker=CreateThread(nullptr,0,[](void* opaque)->DWORD{static_cast<Bridge*>(opaque)->run();return 0;},b.get(),0,nullptr);if(!b->worker)throw std::runtime_error("Capture worker creation failed");
        *output=new RwDesktopBridge{b};return 0;
    }catch(const std::exception& e){fprintf(stderr,"rewind desktop initialization failed: %s\n",e.what());return -1;}
}
extern "C" __declspec(dllexport) int WINAPI RwDesktopBridgeNext(RwDesktopBridge* wrapper,RwDesktopFrame* frame){
    if(!wrapper||!frame)return -1;
    auto b=wrapper->impl;
    if(b->error)return -1;
    if(b->stopping||WaitForSingleObject(b->stopped,0)==WAIT_OBJECT_0)return 2;
    int index=-1;uint64_t oldest=UINT64_MAX;
    for(unsigned i=0;i<8;++i)if(b->slots[i].state.load(std::memory_order_acquire)==Bridge::Ready&&b->slots[i].sequence<oldest){oldest=b->slots[i].sequence;index=(int)i;}
    if(index<0){WaitForSingleObject(b->ready,2);return 0;}
    uint64_t done=b->fence->GetCompletedValue();if(done==UINT64_MAX)return -1;
    if(done<oldest){if(b->fence_wait!=oldest){if(FAILED(b->fence->SetEventOnCompletion(oldest,b->completion))){++b->errors;b->error=true;return -1;}b->fence_wait=oldest;}HANDLE signals[]={b->stopped,b->completion,b->ready};if(WaitForMultipleObjects(3,signals,FALSE,2)==WAIT_FAILED){++b->errors;b->error=true;return -1;}return 0;}
    auto& slot=b->slots[index];int expected=Bridge::Ready;if(!slot.state.compare_exchange_strong(expected,Bridge::Leased,std::memory_order_acquire))return 0;*frame={(unsigned)index,slot.sequence,slot.qpc};return 1;
}
extern "C" __declspec(dllexport) int WINAPI RwDesktopBridgeRelease(RwDesktopBridge* w,unsigned i){if(!w||i>=8)return -1;int state=Bridge::Leased;return w->impl->slots[i].state.compare_exchange_strong(state,Bridge::Free,std::memory_order_release)?0:-1;}
extern "C" __declspec(dllexport) void WINAPI RwDesktopBridgeWake(RwDesktopBridge* w){if(w)SetEvent(w->impl->ready);}
extern "C" __declspec(dllexport) void WINAPI RwDesktopBridgeStop(RwDesktopBridge* w){if(w)w->impl->shutdown();}
extern "C" __declspec(dllexport) int WINAPI RwDesktopBridgeClose(RwDesktopBridge* w,RwDesktopStats* stats){
    if(!w||!stats)return -1;
    auto b=w->impl;b->shutdown();
    *stats={b->arrived.load(),b->copies.load(),b->busy.load(),b->errors.load(),b->resized.load(),b->limited.load(),b->idle.load(),b->opened,1,(unsigned)b->closed.load(),10000000,b->immediate_context,b->full_copies,b->region_copies,b->clears,b->signals,b->flushes,b->descriptor_queries,b->releases,b->timeouts,b->recoveries,b->max_hold_us,b->protected_frames,b->accumulated_batches,b->coalesced_updates,b->max_accumulated};
    int result=b->error?-1:0;delete w;return result;
}
