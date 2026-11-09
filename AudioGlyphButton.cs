using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace Rewind;
internal sealed class AudioGlyphButton : Button
{
    private readonly System.Windows.Shapes.Path glyph=new(){Width=17,Height=17,Stretch=Stretch.Uniform,StrokeThickness=1.7,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round};
    private readonly SolidColorBrush color=new();private bool? muted;private Color? requested;
    internal AudioGlyphButton(){SetResourceReference(StyleProperty,typeof(Button));Width=30;Height=28;Padding=new Thickness(6);Margin=new Thickness(0,0,0,3);Background=Brushes.Transparent;BorderThickness=new Thickness(0);glyph.Stroke=color;Content=glyph;Unloaded+=(_,_)=>color.BeginAnimation(SolidColorBrush.ColorProperty,null);}
    internal void SetMuted(bool value){Color target=(value?ThemePalette.Muted:ThemePalette.Accent).Color;if(muted==value&&requested==target)return;bool initial=muted is null;muted=value;requested=target;glyph.Data=Geometry.Parse(value?"M2,8 L6,8 L11,3 L11,21 L6,16 L2,16 Z M15,7 L22,17 M22,7 L15,17":"M2,8 L6,8 L11,3 L11,21 L6,16 L2,16 Z M15,8 Q20,12 15,16 M18,4 Q27,12 18,20");ToolTip=value?"Unmute this audio lane":"Mute this audio lane";if(initial)color.Color=target;else color.BeginAnimation(SolidColorBrush.ColorProperty,new ColorAnimation(target,TimeSpan.FromMilliseconds(150)));}
}
