// SPDX-License-Identifier: GPL-3.0-only
// Capture the composed desktop independently of the replay duplication session.
#define NOMINMAX
#include <windows.h>
#include <initguid.h>
#include <wincodec.h>
#include <wrl.h>
#include <cstdio>

using Microsoft::WRL::ComPtr;

static void check(HRESULT result) { if (FAILED(result)) throw result; }

struct MonitorSearch {
    const wchar_t* name;
    RECT bounds{};
    bool found = false;
};

static BOOL CALLBACK find_monitor(HMONITOR monitor, HDC, LPRECT, LPARAM parameter) {
    auto& search = *reinterpret_cast<MonitorSearch*>(parameter);
    MONITORINFOEXW info{};
    info.cbSize = sizeof(info);
    if (!GetMonitorInfoW(monitor, &info)) return TRUE;
    if (search.name ? lstrcmpiW(search.name, info.szDevice) != 0 : !(info.dwFlags & MONITORINFOF_PRIMARY))
        return TRUE;
    search.bounds = info.rcMonitor;
    search.found = true;
    return FALSE;
}

static void save_png(const wchar_t* path, int width, int height, UINT stride, BYTE* pixels) {
    ComPtr<IWICImagingFactory> wic;
    ComPtr<IWICStream> stream;
    ComPtr<IWICBitmapEncoder> encoder;
    ComPtr<IWICBitmapFrameEncode> frame;
    check(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic)));
    check(wic->CreateStream(&stream));
    check(stream->InitializeFromFilename(path, GENERIC_WRITE));
    check(wic->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder));
    check(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache));
    check(encoder->CreateNewFrame(&frame, nullptr));
    check(frame->Initialize(nullptr));
    check(frame->SetSize(width, height));
    WICPixelFormatGUID format = GUID_WICPixelFormat24bppBGR;
    check(frame->SetPixelFormat(&format));
    if (!IsEqualGUID(format, GUID_WICPixelFormat24bppBGR)) throw WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT;
    check(frame->WritePixels(height, stride, stride * height, pixels));
    check(frame->Commit());
    check(encoder->Commit());
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 2 || argc > 3) return 2;
    using SetDpiContext = BOOL (WINAPI*)(DPI_AWARENESS_CONTEXT);
    auto set_dpi_context = reinterpret_cast<SetDpiContext>(
        GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetProcessDpiAwarenessContext"));
    if (!set_dpi_context || !set_dpi_context(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) SetProcessDPIAware();
    MonitorSearch monitor{argc == 3 ? argv[2] : nullptr};
    EnumDisplayMonitors(nullptr, nullptr, find_monitor, reinterpret_cast<LPARAM>(&monitor));
    if (!monitor.found) {
        std::fputs("The screenshot display is no longer available.\n", stderr);
        return 1;
    }
    const int width = monitor.bounds.right - monitor.bounds.left;
    const int height = monitor.bounds.bottom - monitor.bounds.top;
    if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return 1;

    HDC desktop = GetDC(nullptr);
    HDC memory = desktop ? CreateCompatibleDC(desktop) : nullptr;
    BITMAPINFO dib{};
    dib.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    dib.bmiHeader.biWidth = width;
    dib.bmiHeader.biHeight = -height;
    dib.bmiHeader.biPlanes = 1;
    dib.bmiHeader.biBitCount = 24;
    dib.bmiHeader.biCompression = BI_RGB;
    void* pixels = nullptr;
    HBITMAP bitmap = memory ? CreateDIBSection(memory, &dib, DIB_RGB_COLORS, &pixels, nullptr, 0) : nullptr;
    HGDIOBJ previous = bitmap ? SelectObject(memory, bitmap) : nullptr;
    HRESULT result = E_FAIL;
    if (previous && previous != HGDI_ERROR &&
        BitBlt(memory, 0, 0, width, height, desktop, monitor.bounds.left, monitor.bounds.top, SRCCOPY | CAPTUREBLT)) {
        GdiFlush();
        result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (SUCCEEDED(result)) {
            try {
                save_png(argv[1], width, height, (width * 3 + 3) & ~3, static_cast<BYTE*>(pixels));
                result = S_OK;
            } catch (HRESULT error) { result = error; }
            CoUninitialize();
        }
    }
    if (previous && previous != HGDI_ERROR) SelectObject(memory, previous);
    if (bitmap) DeleteObject(bitmap);
    if (memory) DeleteDC(memory);
    if (desktop) ReleaseDC(nullptr, desktop);
    if (FAILED(result)) std::fprintf(stderr, "Screenshot failed (HRESULT 0x%08lx).\n", static_cast<unsigned long>(result));
    return FAILED(result) ? 1 : 0;
}
