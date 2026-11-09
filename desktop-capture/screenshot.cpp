// SPDX-License-Identifier: GPL-3.0-only
// One-shot display capture in its own process. Never touches replay surfaces/queues.
#define NOMINMAX
#include <windows.h>
#include <initguid.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wincodec.h>
#include <wrl.h>
#include <cstdio>
#include <stdexcept>
using Microsoft::WRL::ComPtr;
static void check(HRESULT hr){if(FAILED(hr))throw hr;}
int wmain(int argc,wchar_t** argv){if(argc!=2)return 2;bool co=false,held=false,mapped=false;ComPtr<IDXGIOutputDuplication> dupl;ComPtr<ID3D11DeviceContext> context;ComPtr<ID3D11Texture2D> staging;
 try{check(CoInitializeEx(nullptr,COINIT_MULTITHREADED));co=true;ComPtr<IDXGIFactory1> factory;check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));ComPtr<IDXGIAdapter1> selected;ComPtr<IDXGIOutput> output;DXGI_OUTPUT_DESC desc{};HMONITOR primary=MonitorFromPoint(POINT{0,0},MONITOR_DEFAULTTOPRIMARY);
 for(UINT a=0;!selected;a++){ComPtr<IDXGIAdapter1> adapter;if(factory->EnumAdapters1(a,&adapter)==DXGI_ERROR_NOT_FOUND)break;for(UINT o=0;;o++){ComPtr<IDXGIOutput> candidate;if(adapter->EnumOutputs(o,&candidate)==DXGI_ERROR_NOT_FOUND)break;check(candidate->GetDesc(&desc));if(desc.AttachedToDesktop&&desc.Monitor==primary){selected=adapter;output=candidate;break;}}}
 if(!selected)throw E_FAIL;check(output->GetDesc(&desc));if(desc.Rotation!=DXGI_MODE_ROTATION_IDENTITY)throw DXGI_ERROR_UNSUPPORTED;ComPtr<ID3D11Device> device;D3D_FEATURE_LEVEL level;check(D3D11CreateDevice(selected.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,&level,&context));ComPtr<IDXGIOutput1> output1;check(output.As(&output1));check(output1->DuplicateOutput(device.Get(),&dupl));
 DXGI_OUTDUPL_FRAME_INFO info{};ComPtr<IDXGIResource> resource;HRESULT acquired=DXGI_ERROR_WAIT_TIMEOUT;for(int i=0;i<8&&acquired==DXGI_ERROR_WAIT_TIMEOUT;i++)acquired=dupl->AcquireNextFrame(250,&info,&resource);check(acquired);held=true;ComPtr<ID3D11Texture2D> texture;check(resource.As(&texture));D3D11_TEXTURE2D_DESC td{};texture->GetDesc(&td);td.Usage=D3D11_USAGE_STAGING;td.BindFlags=0;td.MiscFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;check(device->CreateTexture2D(&td,nullptr,&staging));context->CopyResource(staging.Get(),texture.Get());check(dupl->ReleaseFrame());held=false;resource.Reset();texture.Reset();D3D11_MAPPED_SUBRESOURCE pixels{};check(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&pixels));mapped=true;
 ComPtr<IWICImagingFactory> wic;check(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&wic)));ComPtr<IWICStream> stream;check(wic->CreateStream(&stream));check(stream->InitializeFromFilename(argv[1],GENERIC_WRITE));ComPtr<IWICBitmapEncoder> encoder;check(wic->CreateEncoder(GUID_ContainerFormatPng,nullptr,&encoder));check(encoder->Initialize(stream.Get(),WICBitmapEncoderNoCache));ComPtr<IWICBitmapFrameEncode> frame;check(encoder->CreateNewFrame(&frame,nullptr));check(frame->Initialize(nullptr));check(frame->SetSize(td.Width,td.Height));WICPixelFormatGUID format=GUID_WICPixelFormat32bppBGR;check(frame->SetPixelFormat(&format));check(frame->WritePixels(td.Height,pixels.RowPitch,pixels.RowPitch*td.Height,static_cast<BYTE*>(pixels.pData)));check(frame->Commit());check(encoder->Commit());context->Unmap(staging.Get(),0);mapped=false;
 // Release COM objects before uninitializing the apartment.
 frame.Reset();encoder.Reset();stream.Reset();wic.Reset();dupl.Reset();context.Reset();staging.Reset();device.Reset();output1.Reset();output.Reset();selected.Reset();factory.Reset();CoUninitialize();return 0;
 }catch(HRESULT hr){if(mapped)context->Unmap(staging.Get(),0);if(held)dupl->ReleaseFrame();dupl.Reset();context.Reset();staging.Reset();if(co)CoUninitialize();std::fprintf(stderr,"Primary display screenshot failed (HRESULT 0x%08lx). Unlock the desktop and retry.\n",static_cast<unsigned long>(hr));return 1;}
}
