using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Rewind;

internal sealed class EditTrack : FrameworkElement
{
    internal sealed record Span(double Start,double End,bool Kept,bool Cropped);
    private IReadOnlyList<Span> spans=Array.Empty<Span>();
    private double duration,position;
    private int selected;
    private const double Edge=8, Ruler=22;
    internal event Action<double>? Seek;
    internal event Action? ScrubStarted,ScrubEnded;

    internal EditTrack()
    {
        Height=81;
        Cursor=Cursors.Hand;
        Focusable=true;
        System.Windows.Automation.AutomationProperties.SetName(this,"Editing timeline");
    }

    internal void ShowSegments(IReadOnlyList<Span> value,int active)
    {
        spans=value;
        duration=value.Count>0?value[^1].End:0;
        selected=active;
        InvalidateVisual();
    }

    internal void SetPosition(double value,int active)
    {
        if(position==value&&selected==active)return;
        position=value;selected=active;
        InvalidateVisual();
    }

    private double TrackWidth=>Math.Max(1,ActualWidth-2*Edge);
    private double X(double time)=>Edge+TrackWidth*Math.Clamp(time/duration,0,1);
    private double Time(Point point)=>duration*Math.Clamp((point.X-Edge)/TrackWidth,0,1);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent,null,new Rect(RenderSize));
        if(duration<=0||ActualWidth<=2*Edge)return;
        var dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var type=new Typeface(Typography.Inter,FontStyles.Normal,FontWeights.Medium,FontStretches.Normal);
        var line=new Pen(ThemePalette.Line,1);
        dc.DrawLine(line,new Point(Edge,Ruler),new Point(ActualWidth-Edge,Ruler));
        int major=Math.Clamp((int)(ActualWidth/105),2,10);
        for(int tick=0;tick<=major*4;tick++)
        {
            double x=X(duration*tick/(major*4));
            bool labeled=tick%4==0;
            dc.DrawLine(line,new Point(x,Ruler),new Point(x,Ruler-(labeled?6:3)));
            if(!labeled)continue;
            var value=TimeSpan.FromSeconds(duration*tick/(major*4));
            var label=new FormattedText($"{(int)value.TotalMinutes}:{value.Seconds:00}",CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,type,10,ThemePalette.Muted,dpi);
            dc.DrawText(label,new Point(Math.Clamp(x-label.Width/2,0,Math.Max(0,ActualWidth-label.Width)),0));
        }
        for(int i=0;i<spans.Count;i++)
        {
            var span=spans[i];
            double left=X(span.Start), width=Math.Max(1,X(span.End)-left-1);
            var block=new Rect(left,29,width,40);
            dc.DrawRoundedRectangle(span.Kept?ThemePalette.Secondary:ThemePalette.Surface,
                new Pen(i==selected?ThemePalette.Accent:ThemePalette.Line,i==selected?2:1),block,4,4);
            if(width>27)
            {
                string title=span.Kept?(span.Cropped&&width>85?$"{i+1} · Cropped":$"{i+1}"):(width>80?$"{i+1} · Removed":"×");
                var text=new FormattedText(title,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,type,11,
                    span.Kept?ThemePalette.Foreground:ThemePalette.Muted,dpi);
                dc.DrawText(text,new Point(left+6,42));
            }
        }
        double cursor=X(position);
        dc.DrawLine(new Pen(ThemePalette.Foreground,1.5),new Point(cursor,Ruler),new Point(cursor,76));
        dc.DrawRoundedRectangle(ThemePalette.Foreground,null,new Rect(cursor-3,Ruler-2,6,7),2,2);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if(duration<=0)return;
        Focus();
        if(!CaptureMouse())return;
        ScrubStarted?.Invoke();
        Seek?.Invoke(Time(e.GetPosition(this)));
        e.Handled=true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if(IsMouseCaptured&&e.LeftButton==MouseButtonState.Pressed)Seek?.Invoke(Time(e.GetPosition(this)));
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if(!IsMouseCaptured)return;
        Seek?.Invoke(Time(e.GetPosition(this)));
        ReleaseMouseCapture();
        e.Handled=true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)=>ScrubEnded?.Invoke();
}
