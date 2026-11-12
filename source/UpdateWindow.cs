using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;

namespace Rewind;

internal sealed class UpdateWindow : Window
{
    internal UpdateWindow(Window owner,UpdateResult result,Func<string,UpdateAsset,Task> install,Action exit)
    {
        Owner=owner;Title="FLICK Updates";Width=460;SizeToContent=SizeToContent.Height;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        ShowInTaskbar=false;Background=ThemePalette.Surface;Foreground=ThemePalette.Foreground;FontFamily=owner.FontFamily;Icon=owner.Icon;
        WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=0,ResizeBorderThickness=new Thickness(0),GlassFrameThickness=new Thickness(0)});
        SourceInitialized+=(_,_)=>OverlayWindow.Round(this);
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/FLICK;component/Theme.xaml",UriKind.Relative)});
        var root=new StackPanel();
        root.Children.Add(Shell.DialogHeader(this,"Updates"));
        var body=new StackPanel{Margin=new Thickness(20,15,20,18)};
        root.Children.Add(body);Content=root;
        body.Children.Add(new TextBlock{Text=result.Message,FontSize=19,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        var detail=new TextBlock{Foreground=ThemePalette.Muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,8)};
        body.Children.Add(detail);
        var progressBar=new ProgressBar{Height=5,Maximum=1,Foreground=ThemePalette.Accent,Background=ThemePalette.Secondary,Visibility=Visibility.Collapsed};
        progressBar.SetResourceReference(StyleProperty,"AudioPeakMeter");
        body.Children.Add(progressBar);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        body.Children.Add(actions);
        var close=new Button{Content="Close"};close.Click+=(_,_)=>Close();actions.Children.Add(close);
        var primary=new Button{IsDefault=true,Visibility=Visibility.Collapsed};actions.Children.Add(primary);
        CancellationTokenSource? download=null;
        string? file=null;
        bool closed=false,installing=false;
        Closing+=(_,e)=>e.Cancel=installing;
        Closed+=(_,_)=>{
            closed=true;download?.Cancel();
            if(file is not null)UpdateInstaller.RemoveDownload(file);
        };
        PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape){Close();e.Handled=true;}};

        if(result.Installer is not {} asset)
        {
            if(result.ReleaseUrl is not {} url)return;
            detail.Text="This release has no installer FLICK can verify. Open GitHub to view the release.";
            primary.Content="Open GitHub";primary.Visibility=Visibility.Visible;
            primary.Click+=(_,_)=>{try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}
                catch(System.ComponentModel.Win32Exception){detail.Text="Could not open GitHub.";}};return;
        }

        double size=asset.Size/(1024d*1024d);
        detail.Text=$"Download the verified installer ({size:0.#} MB). Replay can stay on while it downloads.";
        primary.Content="Download update";primary.Visibility=Visibility.Visible;
        primary.Click+=async(_,_)=>{
            primary.IsEnabled=false;
            if(file is not null)
            {
                installing=true;close.IsEnabled=false;
                detail.Text="Finishing pending clip work before closing FLICK…";
                try
                {
                    await install(file,asset);
                    file=null;
                    exit();
                }
                catch(Exception e)
                {
                    installing=false;close.IsEnabled=primary.IsEnabled=true;
                    detail.Text="Could not prepare the update. "+e.Message;
                }
                return;
            }

            using var cancellation=new CancellationTokenSource();
            download=cancellation;
            close.Content="Cancel";progressBar.Visibility=Visibility.Visible;progressBar.Value=0;
            detail.Text="Downloading update…";
            try
            {
                var progress=new Progress<double>(fraction=>{
                    if(closed||download!=cancellation)return;
                    progressBar.Value=fraction;
                    detail.Text=$"Downloading update… {fraction:P0}";
                });
                string ready=await UpdateInstaller.DownloadAsync(asset,progress,cancellation.Token);
                if(closed){UpdateInstaller.RemoveDownload(ready);return;}
                file=ready;progressBar.Value=1;primary.Content="Install update";
                detail.Text="Verified and ready. Installing closes FLICK, then reopens it when setup finishes.";
            }
            catch(Exception e) when(e is IOException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException or InvalidDataException)
            {
                if(!closed){progressBar.Visibility=Visibility.Collapsed;detail.Text=e is OperationCanceledException?"Download timed out. Try again.":"Download failed. "+e.Message;}
            }
            finally
            {
                download=null;
                if(!closed){close.Content="Close";primary.IsEnabled=true;}
            }
        };
    }
}
