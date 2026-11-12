using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Rewind;

internal sealed class CaptureSettingsHover : Grid
{
    private readonly Border notice;
    private bool locked;

    internal CaptureSettingsHover(UIElement controls)
    {
        Background=Brushes.Transparent;
        Children.Add(controls);
        notice=new Border
        {
            Background=ThemePalette.Secondary,
            CornerRadius=new CornerRadius(6),
            Opacity=.97,
            IsHitTestVisible=false,
            Visibility=Visibility.Collapsed,
            Child=new TextBlock
            {
                Text="Pause replay to change capture settings",
                Foreground=ThemePalette.Foreground,
                FontWeight=FontWeights.SemiBold,
                TextWrapping=TextWrapping.Wrap,
                TextAlignment=TextAlignment.Center,
                HorizontalAlignment=HorizontalAlignment.Center,
                VerticalAlignment=VerticalAlignment.Center,
                Margin=new Thickness(12,4,12,4)
            }
        };
        Children.Add(notice);
        MouseEnter+=(_,_)=>RefreshNotice();
        MouseLeave+=(_,_)=>RefreshNotice();
    }

    internal void SetLocked(bool value)
    {
        locked=value;
        RefreshNotice();
    }

    private void RefreshNotice()
        =>notice.Visibility=locked&&IsMouseOver?Visibility.Visible:Visibility.Collapsed;
}
