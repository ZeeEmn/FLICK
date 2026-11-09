using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace Rewind;
// Pixel scrolling on the UI render dispatcher; subscribed only while interpolation is active.
internal sealed class SmoothScrollViewer : ScrollViewer
{
    private bool animating;
    private double target,current,lastWheel,acceleration=1;
    private long lastFrame;
    internal bool IsAnimating=>animating;
    internal SmoothScrollViewer(){CanContentScroll=false;PanningMode=PanningMode.VerticalOnly;Unloaded+=(_,_)=>Stop();IsVisibleChanged+=(_,_)=>{if(!IsVisible)Stop();};ScrollChanged+=(_,e)=>{if(e.ExtentHeightChange!=0||e.ViewportHeightChange!=0)target=Math.Clamp(target,0,ScrollableHeight);};}
    protected override void OnMouseWheel(MouseWheelEventArgs e){if(e.Handled||ScrollableHeight<=0)return;ScrollBy(-e.Delta/120.0);e.Handled=true;}
    internal void ScrollBy(double notches){
        if(ScrollableHeight<=0||!IsVisible)return;double now=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
        acceleration=now-lastWheel<.12?Math.Min(2.25,acceleration+.2):1;lastWheel=now;
        if(!animating)current=target=VerticalOffset;
        double step=SystemParameters.WheelScrollLines<0?ViewportHeight*.9:Math.Max(1,SystemParameters.WheelScrollLines)*20;
        target=Math.Clamp(target+notches*step*acceleration,0,ScrollableHeight);
        if(!animating){animating=true;lastFrame=Stopwatch.GetTimestamp();CompositionTarget.Rendering+=Advance;}
    }
    private void Advance(object? sender,EventArgs e){
        if(!IsVisible||Window.GetWindow(this)?.WindowState==WindowState.Minimized){Stop();return;}
        long now=Stopwatch.GetTimestamp();double dt=Math.Min(.25,(now-lastFrame)/(double)Stopwatch.Frequency);lastFrame=now;target=Math.Clamp(target,0,ScrollableHeight);
        double remaining=target-current;if(Math.Abs(remaining)<.35){ScrollToVerticalOffset(target);Stop();return;}
        current+=remaining*(1-Math.Exp(-18*dt));ScrollToVerticalOffset(current);
    }
    private void Stop(){if(animating){CompositionTarget.Rendering-=Advance;animating=false;}target=VerticalOffset;}
}
