using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Rewind;

internal sealed class ClipStorageMeter : StackPanel
{
    private readonly TextBlock summary=new(){Foreground=ThemePalette.Muted,FontSize=11,TextWrapping=TextWrapping.Wrap};
    private readonly ProgressBar bar=new(){Height=5,Margin=new Thickness(0,5,0,6),Foreground=ThemePalette.Accent,Background=ThemePalette.Secondary};
    private bool refreshing,pending;
    private int queued;

    internal ClipStorageMeter(ReplayEngine engine)
    {
        Margin=new Thickness(0,0,0,14);
        summary.Text="Measuring storage…";
        bar.SetResourceReference(StyleProperty,"AudioPeakMeter");
        Children.Add(new TextBlock{Text="FLICK storage",FontSize=11,Foreground=ThemePalette.Foreground});
        Children.Add(bar);
        Children.Add(summary);
        Loaded+=(_,_)=>{engine.ClipsChanged+=StorageChanged;Refresh();};
        Unloaded+=(_,_)=>engine.ClipsChanged-=StorageChanged;
        IsVisibleChanged+=(_,_)=>{if(IsVisible&&IsLoaded)Refresh();};
    }

    private void StorageChanged()
    {
        if(Dispatcher.HasShutdownStarted||Interlocked.Exchange(ref queued,1)!=0)return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background,()=>{
            Interlocked.Exchange(ref queued,0);
            if(IsLoaded&&IsVisible)Refresh();
        });
    }

    internal async void Refresh()
    {
        pending=true;
        if(refreshing||!IsLoaded||!IsVisible)return;
        refreshing=true;
        try
        {
            while(pending&&IsLoaded&&IsVisible)
            {
                pending=false;
                string folder=StorageLocations.Current;
                try
                {
                    var (used,total,free)=await Task.Run(()=>Measure(folder));
                    if(!IsLoaded||folder!=StorageLocations.Current)continue;
                    bar.Value=total>0?bar.Maximum*Math.Clamp((double)used/total,0,1):0;
                    summary.Text=$"{Format(used)} in FLICK clips and screenshots · {Format(free)} free of {Format(total)} on this drive";
                }
                catch(Exception e) when(e is IOException or UnauthorizedAccessException)
                {
                    if(!IsLoaded||folder!=StorageLocations.Current)continue;
                    bar.Value=0;
                    summary.Text="Storage usage is unavailable for this folder.";
                }
            }
        }
        finally{refreshing=false;}
    }

    internal static (long Used,long Total,long Free) Measure(string folder)
    {
        long used=0;
        if(Directory.Exists(folder))
            foreach(string path in Directory.EnumerateFiles(folder,"*",new EnumerationOptions{AttributesToSkip=FileAttributes.ReparsePoint}))
            {
                string name=Path.GetFileName(path);
                bool clip=name.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase)&&!name.EndsWith(".saving.mp4",StringComparison.OrdinalIgnoreCase);
                bool screenshot=name.StartsWith("Screenshot_",StringComparison.OrdinalIgnoreCase)&&name.EndsWith(".png",StringComparison.OrdinalIgnoreCase)&&!name.EndsWith(".saving.png",StringComparison.OrdinalIgnoreCase);
                if(clip||screenshot)try{used+=new FileInfo(path).Length;}catch(FileNotFoundException){}
            }
        var drive=new DriveInfo(Path.GetPathRoot(folder)!);
        return (used,drive.TotalSize,drive.AvailableFreeSpace);
    }

    private static string Format(long bytes)=>bytes switch
    {
        >=1L<<30=>$"{bytes/(double)(1L<<30):0.##} GB",
        >=1L<<20=>$"{bytes/(double)(1L<<20):0.##} MB",
        >=1L<<10=>$"{bytes/1024d:0.##} KB",
        _=>$"{bytes} B"
    };
}
