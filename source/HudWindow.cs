using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;

namespace Rewind;
// No polling, render subscription or animation clock: only explicit state/geometry updates.
internal sealed class HudWindow : Window
{
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    [StructLayout(LayoutKind.Sequential)]internal struct WorkRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SystemParametersInfo(uint action,uint parameter,out WorkRect area,uint flags);
    private static WorkRect? cachedArea;
    internal static long GeometryQueries {get;private set;}
    internal static WorkRect WorkArea(){if(cachedArea is null)RefreshWorkArea();return cachedArea!.Value;}
    private static void RefreshWorkArea(){GeometryQueries++;cachedArea=SystemParametersInfo(0x30,0,out var area,0)?area:new WorkRect{Right=GetSystemMetrics(0),Bottom=GetSystemMetrics(1)};}
    private bool geometryPending,closed;
    private readonly record struct HudState(bool Enabled,string Position,bool Active,bool Microphone);
    private HudState? requested;
    private (int X,int Y,int Width,int Height,bool Visible)? placement;
    internal long VisualUpdates {get;private set;}
    internal long PlacementUpdates {get;private set;}
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetStyle(IntPtr handle,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern IntPtr SetStyle(IntPtr handle,int index,IntPtr value);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr handle,IntPtr after,int x,int y,int w,int h,uint flags);
    private readonly System.Windows.Shapes.Path replay=new(){Data=Geometry.Parse("M20,8 A9,9 0 1 0 21,16 M20,2 L20,9 L13,9 M8,8 L8,16 L15,12 Z"),StrokeThickness=1.5,Width=23,Height=23,Stretch=Stretch.Uniform},mic=new(){Data=Geometry.Parse("M8,5 C8,0 16,0 16,5 L16,12 C16,18 8,18 8,12 Z M4,11 L4,13 C4,23 20,23 20,13 L20,11 M12,22 L12,26"),StrokeThickness=1.5,Width=18,Height=23,Margin=new Thickness(10,0,0,0),Stretch=Stretch.Uniform};
    private string corner="Top-Right";private bool? lastReplay,lastMic;
    internal HudWindow(){Width=84;Height=40;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=false;ShowActivated=false;Topmost=true;IsHitTestVisible=false;Focusable=false;Opacity=1;
        var row=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};row.Children.Add(replay);row.Children.Add(mic);Content=new Border{Background=new SolidColorBrush(Color.FromArgb(217,13,15,18)),CornerRadius=new CornerRadius(6),Padding=new Thickness(10,6,10,6),CacheMode=new BitmapCache{SnapsToDevicePixels=true},Child=row};
        SourceInitialized+=(_,_)=>{RefreshWorkArea();var handle=new WindowInteropHelper(this).Handle;long bits=GetStyle(handle,-20).ToInt64();SetStyle(handle,-20,new IntPtr(bits|0x8|0x20|0x80000|0x8000000|0x80));HwndSource.FromHwnd(handle)?.AddHook(Hook);Anchor();};Closed+=(_,_)=>closed=true;
    }
    private IntPtr Hook(IntPtr h,int m,IntPtr w,IntPtr l,ref bool handled){
        // Coalesce native broadcasts; ordinary HUD state/corner changes read the cache.
        if(m is 0x7e or 0x1a)QueueGeometryRefresh();else if(m==0x2e0)Dispatcher.BeginInvoke(Anchor);
        return IntPtr.Zero;
    }
    private void QueueGeometryRefresh(){if(geometryPending||closed)return;geometryPending=true;Dispatcher.BeginInvoke(()=>{geometryPending=false;if(closed)return;RefreshWorkArea();Anchor();});}
    private void Anchor(){IntPtr handle=new WindowInteropHelper(this).Handle;if(handle==IntPtr.Zero||closed)return;var dpi=VisualTreeHelper.GetDpi(this);int w=(int)(Width*dpi.DpiScaleX),h=(int)(Height*dpi.DpiScaleY),pad=16;var area=WorkArea();int x=corner.EndsWith("Right",StringComparison.Ordinal)?area.Right-w-pad:area.Left+pad,y=corner.StartsWith("Bottom",StringComparison.Ordinal)?area.Bottom-h-pad:area.Top+pad;var next=(x,y,w,h,IsVisible);if(placement==next)return;if(SetWindowPos(handle,new IntPtr(-1),x,y,w,h,0x10|(IsVisible?0x40u:0u))){placement=next;PlacementUpdates++;}}
    internal void Update(bool enabled,string position,bool active,bool microphone){if(closed)return;var next=new HudState(enabled,position,active,microphone);if(requested==next)return;requested=next;if(corner!=position){corner=position;if(IsVisible)Anchor();}bool changed=false;if(lastReplay!=active){lastReplay=active;replay.Stroke=active?ThemePalette.Accent:ThemePalette.Muted;changed=true;}if(lastMic!=microphone){lastMic=microphone;mic.Stroke=microphone?ThemePalette.Accent:ThemePalette.Muted;changed=true;}if(enabled&&!IsVisible){Show();Anchor();changed=true;}else if(!enabled&&IsVisible){Hide();changed=true;}if(changed)VisualUpdates++;}
}
