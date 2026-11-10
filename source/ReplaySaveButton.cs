using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
namespace Rewind;
internal sealed class ReplaySaveButton : Button
{
    private readonly TextBlock label=new(){VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold};
    private readonly Ellipse ring=new(){Width=16,Height=16,Stroke=ThemePalette.Accent,StrokeThickness=2,StrokeDashArray=new DoubleCollection{8,4},Margin=new Thickness(0,0,9,0),RenderTransformOrigin=new Point(.5,.5),RenderTransform=new RotateTransform(),Visibility=Visibility.Collapsed};
    private int state;
    internal ReplaySaveButton(Action save){SetResourceReference(StyleProperty,typeof(Button));var row=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center};row.Children.Add(ring);row.Children.Add(label);Content=row;Click+=(_,_)=>save();IsVisibleChanged+=(_,_)=>Animate();Unloaded+=(_,_)=>((RotateTransform)ring.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,null);SetState(0);}
    internal void SetState(int value){state=value;IsEnabled=value!=1;label.Text=value==1?"Saving Replay...":value==2?"✓ Saved!":"FLICK It";Background=value==1?ThemePalette.Secondary:ThemePalette.Accent;Foreground=value==1?ThemePalette.Muted:ThemePalette.OnAccent;ring.Visibility=value==1?Visibility.Visible:Visibility.Collapsed;Animate();}
    private void Animate(){var turn=(RotateTransform)ring.RenderTransform;turn.BeginAnimation(RotateTransform.AngleProperty,null);if(state==1&&IsVisible)turn.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromMilliseconds(750)){RepeatBehavior=RepeatBehavior.Forever});}
}
