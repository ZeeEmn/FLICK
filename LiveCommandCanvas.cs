using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
namespace Rewind;
internal sealed class LiveCommandCanvas : Grid
{
    private readonly ReplayEngine engine;
    private readonly Action<string,bool> muteLane;
    // Only PCM peak levels use a timer. No native audio-meter or geometry polling.
    private readonly DispatcherTimer timer=new(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(125)};
    private readonly TextBlock title=new(){Text="Currently Capturing: Full Desktop",FontSize=21,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap},status=new(){FontSize=11,Foreground=ThemePalette.Muted},encoder=new(){FontSize=12,TextWrapping=TextWrapping.Wrap},buffer=new(){FontSize=11,Foreground=ThemePalette.Muted};
    private bool watching;
    private Window? owner;
    private int refreshQueued;
    internal long MeterTicks {get;private set;}
    internal long StatusUpdates {get;private set;}
    private readonly StackPanel lanes=new();
    private readonly Dictionary<string,(ProgressBar Bar,TextBlock Level,AudioGlyphButton Mute)> rows=new();
    private string signature="";
    internal LiveCommandCanvas(ReplayEngine engine,Action<string,bool>? mute=null,UIElement? qualityControls=null)
    {
        this.engine=engine;muteLane=mute??engine.MuteAudioLane;
        System.Windows.Documents.TextElement.SetForeground(this,ThemePalette.Foreground);
        RowDefinitions.Add(new(){Height=GridLength.Auto});RowDefinitions.Add(new(){Height=GridLength.Auto});RowDefinitions.Add(new());
        var top=new StackPanel{Margin=new Thickness(0,12,0,6)};
        var row=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        row.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M1,2 L27,2 L27,20 L1,20 Z M14,20 L14,26 M7,26 L21,26"),Stroke=ThemePalette.Foreground,StrokeThickness=1.4,Width=31,Height=29,Stretch=Stretch.Uniform,Margin=new Thickness(0,0,14,0)});
        row.Children.Add(title);top.Children.Add(row);status.Margin=new Thickness(45,8,0,12);top.Children.Add(status);
        if(qualityControls is not null)top.Children.Add(qualityControls);Children.Add(top);
        var divider=new Border{Height=1,Background=ThemePalette.Divider,Margin=new Thickness(0,8,0,10)};SetRow(divider,1);Children.Add(divider);
        var matrix=new Grid{Margin=new Thickness(0,0,0,10)};matrix.ColumnDefinitions.Add(new(){Width=new GridLength(1.3,GridUnitType.Star)});matrix.ColumnDefinitions.Add(new());
        var audio=new StackPanel{Margin=new Thickness(0,0,20,0)};audio.Children.Add(new TextBlock{Text="AUDIO MATRIX",FontSize=10,Foreground=ThemePalette.Muted,Margin=new Thickness(0,8,0,12)});audio.Children.Add(new SmoothScrollViewer{Content=lanes,MaxHeight=145,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden});matrix.Children.Add(audio);
        var details=new StackPanel{Margin=new Thickness(20,8,0,0)};details.Children.Add(new TextBlock{Text="ENCODER",FontSize=10,Foreground=ThemePalette.Muted,Margin=new Thickness(0,0,0,12)});details.Children.Add(encoder);details.Children.Add(buffer);SetColumn(details,1);matrix.Children.Add(details);SetRow(matrix,2);Children.Add(matrix);
        timer.Tick+=(_,_)=>{if(!watching||!CanObserve()){Stop();return;}MeterTicks++;UpdateMeters();};
        Loaded+=(_,_)=>{owner=Window.GetWindow(this);if(owner is not null)owner.StateChanged+=OwnerStateChanged;Start();};
        IsVisibleChanged+=(_,_)=>{if(IsVisible)Start();else Stop();};
        Unloaded+=(_,_)=>{Stop();if(owner is not null)owner.StateChanged-=OwnerStateChanged;owner=null;};
        RefreshState();
    }
    private void OwnerStateChanged(object? sender,EventArgs e){if(owner?.WindowState==WindowState.Minimized)Stop();else Start();}
    private bool CanObserve(){var window=Window.GetWindow(this);return IsLoaded&&IsVisible&&window is not null&&window.IsVisible&&window.WindowState!=WindowState.Minimized;}
    private void Start()
    {
        if(watching||!CanObserve())return;
        watching=true;engine.Changed+=EngineChanged;engine.AudioChanged+=QueueStateUpdate;ThemePalette.Changed+=QueueStateUpdate;RefreshState();
    }
    private void Stop()
    {
        timer.Stop();
        if(!watching)return;watching=false;engine.Changed-=EngineChanged;engine.AudioChanged-=QueueStateUpdate;ThemePalette.Changed-=QueueStateUpdate;
    }
    private void EngineChanged(string message)=>QueueStateUpdate();
    private void QueueStateUpdate()
    {
        if(Dispatcher.HasShutdownStarted||Interlocked.Exchange(ref refreshQueued,1)!=0)return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background,()=>{Interlocked.Exchange(ref refreshQueued,0);if(watching){if(CanObserve())RefreshState();else Stop();}});
    }
    private void RefreshState()
    {
        StatusUpdates++;status.Text=engine.Active?"Replay is running":"Replay paused";
        encoder.Text=engine.EncoderName switch{"h264_nvenc"=>"NVIDIA NVENC · H.264","h264_amf"=>"AMD AMF · H.264","h264_qsv"=>"Intel Quick Sync · H.264","libx264"=>"Software H.264",_=>engine.EncoderName};
        var plan=engine.Plan;buffer.Text=plan is null?"Waiting for capture":$"{plan.Width} × {plan.Height} · {plan.Fps} FPS target\n{plan.Bitrate/1_000_000} Mbps · RAM buffer "+(engine.Active?"active":"paused");
        UpdateMeters();if(engine.Active&&watching)timer.Start();else timer.Stop();
    }
    private void UpdateMeters()
    {
        var meters=engine.AudioMeters();string keys=string.Join("|",meters.Select(m=>m.Id));
        if(keys!=signature){signature=keys;rows.Clear();lanes.Children.Clear();foreach(var meter in meters){
            var line=new Grid{Margin=new Thickness(0,0,0,12)};line.ColumnDefinitions.Add(new());line.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
            line.Children.Add(new TextBlock{Text=meter.Title,FontSize=11,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,0,5,5)});
            var mute=new AudioGlyphButton();SetColumn(mute,1);line.Children.Add(mute);line.RowDefinitions.Add(new());line.RowDefinitions.Add(new(){Height=GridLength.Auto});
            var bar=new ProgressBar{Minimum=0,Maximum=1,Height=3,BorderThickness=new Thickness(0),Foreground=ThemePalette.Accent,Background=ThemePalette.Secondary};bar.SetResourceReference(StyleProperty,"AudioPeakMeter");SetRow(bar,1);line.Children.Add(bar);
            var level=new TextBlock{FontSize=10,Foreground=ThemePalette.Muted,Margin=new Thickness(6,0,0,0)};SetRow(level,1);SetColumn(level,1);line.Children.Add(level);
            string id=meter.Id;mute.Click+=(_,_)=>{var current=engine.AudioMeters().FirstOrDefault(m=>m.Id==id);if(current is not null)muteLane(id,current.Enabled);};rows[meter.Id]=(bar,level,mute);lanes.Children.Add(line);
        }}
        if(meters.Length==0&&lanes.Children.Count==0)lanes.Children.Add(new TextBlock{Text="Audio lanes appear when replay starts.",FontSize=11,Foreground=ThemePalette.Muted,TextWrapping=TextWrapping.Wrap});
        foreach(var meter in meters)if(rows.TryGetValue(meter.Id,out var row)){
            if(row.Bar.Value!=meter.Peak)row.Bar.Value=meter.Peak;
            string level=!meter.Enabled?"Muted":meter.Muted?"Device muted":!meter.Available?"Idle":meter.Peak<.001?"−∞ dB":(20*Math.Log10(meter.Peak)).ToString("0")+" dB";
            if(row.Level.Text!=level)row.Level.Text=level;row.Mute.SetMuted(!meter.Enabled||meter.Muted);
            if(meter.Muted)row.Mute.ToolTip="The device is muted in Windows. Click to toggle the FLICK lane.";
        }
    }
}
