using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
namespace Rewind;
// One settings model, shared by the home canvas and overlay. No polling or owned timer.
internal sealed class QualityControls : StackPanel
{
    private readonly Func<bool> canChange;
    private readonly ProductSettings preferences;
    private readonly ComboBox selector;
    private readonly Dictionary<int,Button> fpsChoices=new();
    private readonly CaptureSettingsHover settingsHover;
    private bool refreshing;
    internal QualityControls(ProductSettings preferences,Func<bool> canChange,Func<Task> apply)
    {
        this.preferences=preferences;this.canChange=canChange;
        var settings=new StackPanel();
        settings.Children.Add(new TextBlock{Text="QUALITY PROFILE",FontSize=10,Foreground=ThemePalette.Muted,Margin=new Thickness(0,0,0,7)});
        selector=new ComboBox{ItemsSource=QualityProfiles.All,SelectedValuePath="Name",HorizontalContentAlignment=HorizontalAlignment.Stretch,MinWidth=120};selector.SetResourceReference(StyleProperty,"ProfileSelector");
        var template=new DataTemplate(typeof(QualityProfile));var item=new FrameworkElementFactory(typeof(StackPanel));
        var name=new FrameworkElementFactory(typeof(TextBlock));name.SetBinding(TextBlock.TextProperty,new Binding("Name"));name.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);item.AppendChild(name);
        var parameters=new FrameworkElementFactory(typeof(TextBlock));parameters.SetBinding(TextBlock.TextProperty,new Binding("Parameters"));parameters.SetValue(TextBlock.FontSizeProperty,10d);parameters.SetValue(TextBlock.ForegroundProperty,new SolidColorBrush(Color.FromRgb(92,99,112)));parameters.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);parameters.SetValue(MaxWidthProperty,390d);item.AppendChild(parameters);template.VisualTree=item;selector.ItemTemplate=template;
        var row=new Grid();row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.Children.Add(selector);
        var fpsRow=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(8,0,0,0)};
        foreach(int fps in new[]{30,60}){int target=fps;var button=new Button{Content=fps+" FPS",Padding=new Thickness(8,7,8,7),FontWeight=FontWeights.SemiBold};button.Click+=async(_,_)=>{if(!canChange()){Refresh();return;}if(preferences.FrameRate==target)return;preferences.FrameRate=target;Refresh();await apply();};fpsChoices.Add(fps,button);fpsRow.Children.Add(button);}
        Grid.SetColumn(fpsRow,1);row.Children.Add(fpsRow);settings.Children.Add(row);
        settingsHover=new CaptureSettingsHover(settings);
        Children.Add(settingsHover);
        selector.SelectionChanged+=async(_,_)=>{if(refreshing)return;if(!canChange()){Refresh();return;}if(selector.SelectedValue is not string value||preferences.Profile==value)return;preferences.Profile=value;Refresh();await apply();};Refresh();
    }
    internal void Refresh(){refreshing=true;try{bool editable=canChange();selector.IsEnabled=editable;if(!editable)selector.IsDropDownOpen=false;selector.SelectedValue=QualityProfiles.Normalize(preferences.Profile);settingsHover.SetLocked(!editable);foreach(var choice in fpsChoices){choice.Value.IsEnabled=editable;bool selected=choice.Key==preferences.FrameRate;choice.Value.BorderBrush=selected?ThemePalette.Accent:ThemePalette.Line;choice.Value.Foreground=selected?ThemePalette.Foreground:ThemePalette.Muted;}}finally{refreshing=false;}}
}
