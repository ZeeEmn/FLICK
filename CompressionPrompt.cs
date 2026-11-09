using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace Rewind;
internal sealed class CompressionPrompt : Border
{
    private readonly CancellationTokenSource cancellation=new();private bool busy,closed;
    internal CompressionPrompt(string path,bool external,ReplayEngine engine,Action dismiss,Action<string> saved){Background=ThemePalette.Surface;BorderBrush=ThemePalette.Divider;BorderThickness=new Thickness(1);CornerRadius=new CornerRadius(12);Padding=new Thickness(28);MaxWidth=460;HorizontalAlignment=HorizontalAlignment.Center;VerticalAlignment=VerticalAlignment.Center;
        var root=new StackPanel();Child=root;root.Children.Add(new TextBlock{Text="Compress clip for Discord Share? Target size: <50MB",FontSize=18,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Foreground=ThemePalette.Foreground});root.Children.Add(new TextBlock{Text=Path.GetFileName(path),Foreground=ThemePalette.Muted,Margin=new Thickness(0,12,0,8),TextTrimming=TextTrimming.CharacterEllipsis});var state=new TextBlock{Text=external?"A new MP4 copy will be saved in your clips folder. Your original stays unchanged.":"Creates a new MP4 copy. Your original stays unchanged.",Foreground=ThemePalette.Muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)};root.Children.Add(state);var row=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};var cancel=new Button{Content="Cancel"};row.Children.Add(cancel);var confirm=new Button{Content="Confirm Compression"};row.Children.Add(confirm);root.Children.Add(row);
        cancel.Click+=(_,_)=>{cancellation.Cancel();closed=true;dismiss();};Unloaded+=(_,_)=>{closed=true;cancellation.Cancel();};
        confirm.Click+=async(_,_)=>{if(busy)return;busy=true;confirm.IsEnabled=false;confirm.Content="Compressing...";try{string output=await DiscordExport.ExportAsync(path,null,new Progress<string>(message=>{if(!closed)state.Text=message;}),cancellation.Token,external:external);engine.ScheduleValidation(output);await engine.RefreshGalleryAsync();if(!closed){saved(output);closed=true;dismiss();}}catch(OperationCanceledException){if(!closed)state.Text="Compression cancelled.";}catch(Exception e){if(!closed)state.Text=e.Message;}finally{busy=false;if(!closed){confirm.IsEnabled=true;confirm.Content="Confirm Compression";}}};
    }
}
