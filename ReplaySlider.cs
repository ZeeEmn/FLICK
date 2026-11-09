using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Rewind;
internal sealed class ReplaySlider : FrameworkElement
{
    internal static readonly DependencyProperty SecondsProperty=DependencyProperty.Register(nameof(Seconds),typeof(int),typeof(ReplaySlider),new FrameworkPropertyMetadata(60,FrameworkPropertyMetadataOptions.AffectsRender,(d,e)=>((ReplaySlider)d).Changed?.Invoke((int)e.NewValue)));
    public int Seconds {get=>(int)GetValue(SecondsProperty);set=>SetValue(SecondsProperty,Math.Clamp(value,1,300));}
    internal event Action<int>? Changed;
    internal ReplaySlider(){HoverMotion.Attach(this);Height=30;MinWidth=150;Focusable=true;Cursor=Cursors.Hand;MouseLeftButtonDown+=(_,e)=>{CaptureMouse();Update(e.GetPosition(this).X);e.Handled=true;};MouseMove+=(_,e)=>{if(IsMouseCaptured)Update(e.GetPosition(this).X);};MouseLeftButtonUp+=(_,_)=>ReleaseMouseCapture();PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Left){Seconds--;e.Handled=true;}if(e.Key==Key.Right){Seconds++;e.Handled=true;}};}
    private void Update(double x)=>Seconds=1+(int)Math.Round(Math.Clamp((x-9)/Math.Max(1,ActualWidth-18),0,1)*299);
    protected override void OnRender(DrawingContext dc){double width=Math.Max(1,ActualWidth-18),x=9+width*(Seconds-1)/299;
        dc.DrawRoundedRectangle(ThemePalette.Line,null,new Rect(9,13,width,4),6,6);
        dc.DrawRoundedRectangle(ThemePalette.Accent,null,new Rect(9,13,Math.Max(1,x-9),4),6,6);
        dc.DrawEllipse(ThemePalette.Accent,null,new Point(x,15),6,6);
    }
}
