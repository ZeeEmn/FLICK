using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Rewind;

// Ordinary desktop overlay: never hooks/injects into a game's renderer or input.
internal static class OverlayWindow
{
    private const int ExtendedStyle=-20;
    internal const long NoActivate=0x08000000;
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetStyle(IntPtr window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetStyle(IntPtr window,int index,IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left,int top,int right,int bottom,int width,int height);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window,IntPtr region,bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc,int index);
    internal static int PrimaryRefreshRate(){IntPtr dc=GetDC(IntPtr.Zero);if(dc==IntPtr.Zero)return 60;try{return Math.Clamp(GetDeviceCaps(dc,116),60,240);}finally{ReleaseDC(IntPtr.Zero,dc);}}
    internal static void Round(Window window){
        IntPtr handle=new WindowInteropHelper(window).Handle;if(handle==IntPtr.Zero)return;
        var dpi=VisualTreeHelper.GetDpi(window);int width=(int)Math.Ceiling(window.ActualWidth*dpi.DpiScaleX),height=(int)Math.Ceiling(window.ActualHeight*dpi.DpiScaleY);if(width<=0||height<=0)return;
        IntPtr region=CreateRoundRectRgn(0,0,width+1,height+1,(int)(24*dpi.DpiScaleX),(int)(24*dpi.DpiScaleY));
        if(region!=IntPtr.Zero&&SetWindowRgn(handle,region,true)==0)DeleteObject(region); // USER owns it on success.
    }

    internal static void SetMouseOnly(Window window,bool mouseOnly)
    {
        var handle=new WindowInteropHelper(window).Handle;
        long style=GetStyle(handle,ExtendedStyle).ToInt64();
        SetStyle(handle,ExtendedStyle,new IntPtr(mouseOnly?style|NoActivate:style&~NoActivate));
        SetWindowPos(handle,IntPtr.Zero,0,0,0,0,0x0010|0x0001|0x0002|0x0004|0x0020);
    }
    internal static bool IsMouseOnly(Window window)=>(GetStyle(new WindowInteropHelper(window).Handle,ExtendedStyle).ToInt64()&NoActivate)!=0;
    internal static Rect ScreenBounds(Window window)
    {
        var bounds=(System.Windows.Forms.Screen.PrimaryScreen??System.Windows.Forms.Screen.AllScreens[0]).Bounds;
        var dpi=VisualTreeHelper.GetDpi(window);
        return new Rect(bounds.Left/dpi.DpiScaleX,bounds.Top/dpi.DpiScaleY,bounds.Width/dpi.DpiScaleX,bounds.Height/dpi.DpiScaleY);
    }
    internal static void RestoreHome(Window window){ShowWindow(new WindowInteropHelper(window).Handle,9);window.Activate();}
    internal static void ShowWithoutActivation(Window window)
    {
        var handle=new WindowInteropHelper(window).Handle;
        // Also handles a process launched with STARTUPINFO's initial SW_HIDE.
        ShowWindow(handle,8); // SW_SHOWNA
        SetWindowPos(handle,new IntPtr(-1),0,0,0,0,0x0010|0x0001|0x0002); // TOPMOST, NOACTIVATE
    }
}
