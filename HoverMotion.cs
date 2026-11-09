using System.Windows;
using System.Windows.Media.Animation;
namespace Rewind;
// Finite clocks only; no timers or permanent render subscription for hover feedback.
internal static class HoverMotion
{
    internal static readonly DependencyProperty ProgressProperty=DependencyProperty.RegisterAttached("Progress",typeof(double),typeof(HoverMotion),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
    internal static double Progress(FrameworkElement element)=>(double)element.GetValue(ProgressProperty);
    internal static void Attach(FrameworkElement element){
        void Animate(double to){var motion=new DoubleAnimation(to,TimeSpan.FromMilliseconds(150));Timeline.SetDesiredFrameRate(motion,60);element.BeginAnimation(ProgressProperty,motion);}
        element.MouseEnter+=(_,_)=>Animate(1);element.MouseLeave+=(_,_)=>Animate(0);
        element.Unloaded+=(_,_)=>{element.BeginAnimation(ProgressProperty,null);element.SetValue(ProgressProperty,0d);};
    }
}
