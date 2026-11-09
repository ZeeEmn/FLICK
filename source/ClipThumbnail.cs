using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shape=System.Windows.Shapes;
namespace Rewind;
internal sealed class ClipThumbnail : Border
{
    private readonly Button play;
    internal ClipThumbnail(ImageSource image,double height,Action open){
        Height=height;CornerRadius=new CornerRadius(6);Background=ThemePalette.Surface;Cursor=Cursors.Hand;ToolTip="Play clip";
        var grid=new Grid();grid.Children.Add(new Image{Source=image,Stretch=Stretch.UniformToFill});Child=grid;
        play=new Button{Width=52,Height=52,Padding=new Thickness(0),Opacity=0,Background=Brushes.Transparent,BorderThickness=new Thickness(0),ToolTip="Play clip",HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        var art=new Grid{Width=52,Height=52};art.Children.Add(new Shape.Ellipse{Fill=ThemePalette.Surface,Stroke=ThemePalette.Accent,StrokeThickness=1});art.Children.Add(new Shape.Path{Data=Geometry.Parse("M0,0 L0,18 L15,9 Z"),Fill=ThemePalette.Foreground,Width=15,Height=18,Stretch=Stretch.Fill,Margin=new Thickness(4,0,0,0),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});var hover=new Shape.Ellipse{Fill=ThemePalette.Accent,Opacity=0,IsHitTestVisible=false,CacheMode=new BitmapCache()};art.Children.Insert(1,hover);
        play.MouseEnter+=(_,_)=>hover.BeginAnimation(OpacityProperty,new DoubleAnimation(.14,TimeSpan.FromMilliseconds(150)));
        play.MouseLeave+=(_,_)=>hover.BeginAnimation(OpacityProperty,new DoubleAnimation(0,TimeSpan.FromMilliseconds(150)));play.Content=art;
        // A bare presenter keeps the existing rectangular button template from outlining the circle.
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetBinding(ContentPresenter.ContentProperty,new System.Windows.Data.Binding("Content"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)});presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,HorizontalAlignment.Stretch);presenter.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Stretch);play.Template=new ControlTemplate(typeof(Button)){VisualTree=presenter};
        grid.Children.Add(play);play.Click+=(_,e)=>{e.Handled=true;open();};MouseLeftButtonUp+=(_,e)=>{if(!e.Handled){e.Handled=true;open();}};
        MouseEnter+=(_,_)=>Reveal(true);MouseLeave+=(_,_)=>Reveal(play.IsKeyboardFocused);play.GotKeyboardFocus+=(_,_)=>Reveal(true);play.LostKeyboardFocus+=(_,_)=>Reveal(IsMouseOver);
        SizeChanged+=(_,_)=>Clip=new RectangleGeometry(new Rect(0,0,ActualWidth,ActualHeight),6,6);
    }
    internal void Reveal(bool visible)=>play.BeginAnimation(OpacityProperty,new DoubleAnimation(visible?1:0,TimeSpan.FromMilliseconds(150)));
}
