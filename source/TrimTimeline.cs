using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Rewind;
// A single range timeline with two draggable markers and a passive playback indicator.
internal sealed class TrimTimeline : FrameworkElement
{
    private double duration=1,start,end=1,position;
    private bool dragging,left;
    private static readonly Brush Accent=ThemePalette.Accent;
    internal event Action<double,bool>? Changed;
    internal event Action? DragStarted,DragCompleted;
    internal double Duration{get=>duration;set{duration=Math.Max(.1,value);InvalidateVisual();}}
    internal double Start{get=>start;set{start=value;InvalidateVisual();}}
    internal double End{get=>end;set{end=value;InvalidateVisual();}}
    internal double Position{get=>position;set{position=value;InvalidateVisual();}}
    internal TrimTimeline(){HoverMotion.Attach(this);Cursor=Cursors.SizeWE;Focusable=true;ToolTip="Drag the left or right marker to select the trim range";}
    private double X(double value)=>8+Math.Clamp(value/duration,0,1)*Math.Max(1,ActualWidth-16);
    protected override void OnRender(DrawingContext dc){
        dc.DrawRectangle(Brushes.Transparent,null,new Rect(RenderSize));
        double y=15;dc.DrawRoundedRectangle(ThemePalette.Secondary,null,new Rect(8,y,Math.Max(1,ActualWidth-16),5),6,6);
        dc.DrawRoundedRectangle(Accent,null,new Rect(X(start),y,Math.Max(1,X(end)-X(start)),5),6,6);
        foreach(double value in new[]{start,end}){dc.DrawRoundedRectangle(Accent,null,new Rect(X(value)-4,8,8,19),6,6);}
        dc.DrawLine(new Pen(ThemePalette.Foreground,1),new Point(X(position),4),new Point(X(position),30));
        foreach(bool first in new[]{true,false}){var label=new FormattedText(TimeSpan.FromSeconds(first?start:end).ToString(@"mm\:ss\.f",CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(Typography.Inter,FontStyles.Normal,FontWeights.Medium,FontStretches.Normal),11,ThemePalette.Muted,VisualTreeHelper.GetDpi(this).PixelsPerDip);dc.DrawText(label,new Point(first?8:Math.Max(8,ActualWidth-8-label.Width),33));}
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e){base.OnMouseLeftButtonDown(e);double x=e.GetPosition(this).X;left=Math.Abs(x-X(start))<=Math.Abs(x-X(end));dragging=true;DragStarted?.Invoke();CaptureMouse();Move(x);e.Handled=true;}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragging)Move(e.GetPosition(this).X);}
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e){base.OnMouseLeftButtonUp(e);if(!dragging)return;Move(e.GetPosition(this).X);dragging=false;ReleaseMouseCapture();DragCompleted?.Invoke();e.Handled=true;}
    protected override void OnLostMouseCapture(MouseEventArgs e){bool wasDragging=dragging;dragging=false;base.OnLostMouseCapture(e);if(wasDragging)DragCompleted?.Invoke();}
    private void Move(double x){double value=Math.Clamp((x-8)/Math.Max(1,ActualWidth-16)*duration,0,duration);if(left)start=Math.Min(value,Math.Max(0,end-.1));else end=Math.Max(value,Math.Min(duration,start+.1));InvalidateVisual();Changed?.Invoke(left?start:end,left);}
}
