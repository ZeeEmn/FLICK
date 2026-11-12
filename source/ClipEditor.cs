using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Rewind;
// One in-app player: preview mode and expanded trimming share the same decoder.
internal sealed class ClipEditor : Grid
{
    private readonly string path;
    private readonly ReplayEngine engine;
    private readonly Action close;
    private readonly Grid mediaHost=new(){Background=Brushes.Black};
    private readonly MediaElement video=new(){LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Close,MinHeight=180,Stretch=Stretch.Uniform,Volume=0};
    private readonly Slider timeline=new(){Minimum=0,TickFrequency=.1,Height=32,Background=Brushes.Transparent};
    private readonly TrimTimeline markers=new(){Height=52};
    private readonly TextBlock range=new(),message=new(){TextWrapping=TextWrapping.Wrap,Foreground=ThemePalette.Muted};
    private readonly StackPanel trimArea=new();
    private readonly WrapPanel actions=new();
    private readonly ComboBox operation=new(){ItemsSource=new[]{"Keep selected range","Remove selected middle"},SelectedIndex=0};
    private readonly StackPanel audioTracks=new();
    private readonly List<(ClipFiles.AudioTrack Track,MediaElement Player,CheckBox Keep,Slider Level)> audio=new();
    private readonly CancellationTokenSource loading=new();
    private readonly List<string> audioFiles=new();
    private bool audioReady;
    private bool Removing=>operation.SelectedIndex==1;
    private readonly Button play=new(),edit=new();
    private readonly Button nextClip=new(){Content="Next clip ›",FontWeight=FontWeights.SemiBold};
    private readonly Slider previewVolume=new(){Minimum=0,Maximum=1,Width=104,VerticalAlignment=VerticalAlignment.Center,ToolTip="Playback volume only"};
    private readonly DispatcherTimer progress=new(){Interval=TimeSpan.FromMilliseconds(100)};
    private readonly DispatcherTimer scrub=new(){Interval=TimeSpan.FromMilliseconds(80)};
    private readonly DispatcherTimer seekResume=new(){Interval=TimeSpan.FromMilliseconds(180)};
    private bool resumeAfterSeek,resumePending;
    private ClipFiles.Info? info;
    private bool busy,closed,playing,seeking,updating,trimming,releasing;
    private CancellationTokenSource? saveCancellation;
    private string? savedPath;
    private Exception? saveError;
    private readonly Action<string>? saved;
    private double scrubPosition;
    private readonly TaskCompletionSource ready=new(),previewReady=new();
    private RoutedEventHandler opened=null!,ended=null!;private EventHandler<ExceptionRoutedEventArgs> failed=null!;
    internal Task Ready=>ready.Task;
    internal Task PreviewReady=>previewReady.Task;
    internal ClipEditor(string path,ReplayEngine engine,Action close,bool trimming=true,Action<string>? saved=null,Action<double>? next=null,double volume=1){
        this.path=path;this.engine=engine;this.close=close;this.trimming=trimming;this.saved=saved;
        System.Windows.Documents.TextElement.SetFontFamily(this,Typography.Inter);Background=ThemePalette.Surface;
        var content=new Grid{Margin=new Thickness(24)};for(int i=0;i<7;i++)content.RowDefinitions.Add(new(){Height=i==2?new GridLength(1,GridUnitType.Star):GridLength.Auto});Children.Add(content);
        var heading=new DockPanel();var cancel=Button("X",()=>{if(busy)saveCancellation?.Cancel();else Dismiss();});cancel.ToolTip="Close preview";DockPanel.SetDock(cancel,Dock.Right);heading.Children.Add(cancel);heading.Children.Add(new TextBlock{Text="Clip preview",FontSize=22,FontWeight=FontWeights.SemiBold,Foreground=ThemePalette.Foreground});content.Children.Add(heading);
        content.Children.Add(new TextBlock{Text=Path.GetFileName(path),Foreground=ThemePalette.Muted,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,10,0,12)});mediaHost.Children.Add(video);content.Children.Add(mediaHost);
        var controls=new WrapPanel{Margin=new Thickness(0,10,0,0)};play.Content="Play";play.FontWeight=FontWeights.SemiBold;
        play.Click+=(_,_)=>TogglePlayback();
        mediaHost.MouseLeftButtonUp+=(_,e)=>{if(TogglePlayback())e.Handled=true;};
        controls.Children.Add(play);
        nextClip.IsEnabled=next is not null;
        nextClip.ToolTip=next is null?"Last clip in the gallery":"Play the next clip in gallery order";
        nextClip.Click+=(_,_)=>{if(!busy&&!closed)next?.Invoke(previewVolume.Value);};
        controls.Children.Add(nextClip);
        range.Foreground=ThemePalette.Foreground;range.VerticalAlignment=VerticalAlignment.Center;range.Margin=new Thickness(10,0,14,0);controls.Children.Add(range);
        var volumeControls=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)};
        volumeControls.Children.Add(new System.Windows.Shapes.Path
        {
            Data=Geometry.Parse("M2,8 L6,8 L11,3 L11,21 L6,16 L2,16 Z M15,8 Q20,12 15,16 M18,4 Q27,12 18,20"),
            Stroke=ThemePalette.Foreground,StrokeThickness=1.7,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,
            Width=17,Height=17,Stretch=Stretch.Uniform,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)
        });
        System.Windows.Automation.AutomationProperties.SetName(previewVolume,"Playback volume");
        previewVolume.Value=volume;previewVolume.ValueChanged+=(_,_)=>ApplyPreviewVolume();
        volumeControls.Children.Add(previewVolume);controls.Children.Add(volumeControls);
        edit.Content="Trim clip";edit.FontWeight=FontWeights.SemiBold;edit.Click+=(_,_)=>{this.trimming=!this.trimming;Update();};controls.Children.Add(edit);
        content.Children.Add(controls);content.Children.Add(timeline);
        operation.SelectionChanged+=(_,_)=>{saveError=null;Update();};trimArea.Children.Add(operation);
        trimArea.Children.Add(new TextBlock{Text="Drag the start and end markers",Foreground=ThemePalette.Muted,Margin=new Thickness(0,12,0,0)});trimArea.Children.Add(markers);trimArea.Children.Add(new SmoothScrollViewer{Content=audioTracks,MaxHeight=140,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden});
        actions.Children.Add(Button("Save as New File",()=>_=SaveAsync(false)));actions.Children.Add(Button("Overwrite Current File",()=>_=SaveAsync(true)));trimArea.Children.Add(actions);content.Children.Add(trimArea);content.Children.Add(message);for(int i=0;i<content.Children.Count;i++)Grid.SetRow(content.Children[i],i);
        timeline.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((_,_)=>BeginScrub()));timeline.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,_)=>EndScrub()));
        timeline.PreviewMouseLeftButtonDown+=(_,e)=>{if(info is null||busy)return;BeginScrub();e.Handled=true;SeekFraction(e.GetPosition(timeline).X/Math.Max(1,timeline.ActualWidth));timeline.CaptureMouse();};
        timeline.PreviewMouseMove+=(_,e)=>{if(timeline.IsMouseCaptured&&e.LeftButton==System.Windows.Input.MouseButtonState.Pressed)SeekFraction(e.GetPosition(timeline).X/Math.Max(1,timeline.ActualWidth));};
        timeline.PreviewMouseLeftButtonUp+=(_,_)=>EndScrub();
        timeline.LostMouseCapture+=(_,_)=>EndScrub();
        timeline.ValueChanged+=(_,_)=>{if(!updating&&seeking)QueueSeek(timeline.Value);};
        markers.DragStarted+=BeginScrub;markers.DragCompleted+=EndScrub;
        markers.Changed+=(value,isStart)=>{saveError=null;QueueSeek(value);Update();};
        opened=(_,_)=>{if(closed||busy||releasing)return;video.Position=TimeSpan.Zero;if(audioReady)SetPlaying(!this.trimming);previewReady.TrySetResult();};
        ended=(_,_)=>{if(!playing||busy||releasing)return;SetPlaying(false);if(!this.trimming||Removing)Seek(0);};
        failed=(_,e)=>{if(busy||releasing||closed)return;SetPlaying(false);message.Text="Preview unavailable: "+e.ErrorException.Message;previewReady.TrySetException(e.ErrorException);};
        video.MediaOpened+=opened;video.MediaEnded+=ended;video.MediaFailed+=failed;
        progress.Tick+=(_,_)=>{
            if(closed||!IsVisible){SetPlaying(false);return;}
            double position=video.Position.TotalSeconds;
            if(this.trimming&&!Removing&&position>=markers.End){
                SetPlaying(false);
                Seek(markers.End);
                return;
            }
            if(!seeking){updating=true;timeline.Value=position;updating=false;}
            markers.Position=position;
            if(playing)foreach(var item in audio)
                if(Math.Abs(item.Player.Position.TotalSeconds-position)>.12)item.Player.Position=video.Position;
            Update();
        };
        scrub.Tick+=(_,_)=>{scrub.Stop();if(seeking&&!closed&&!busy)SeekVideo(scrubPosition);};
        seekResume.Tick+=(_,_)=>{seekResume.Stop();bool resume=resumePending;resumePending=false;if(!closed&&!busy&&!releasing&&!seeking&&IsVisible)SetPlaying(resume);};
        IsVisibleChanged+=(_,_)=>{if(!IsVisible&&!closed)CloseForHide();};Unloaded+=(_,_)=>Release();Loaded+=async(_,_)=>await LoadAsync();Update();
    }
    private static Button Button(string text,Action action){var button=new Button{Content=text,FontWeight=FontWeights.SemiBold};button.Click+=(_,_)=>action();return button;}
    private async Task LoadAsync(){
        actions.IsEnabled=false;play.IsEnabled=false;message.Text="Loading clip...";
        try{video.Source=new Uri(path);video.Play();video.Pause();var result=await ClipFiles.ProbeAsync(path,loading.Token);if(closed||!IsVisible)return;info=result;timeline.Maximum=info.Duration;markers.Duration=info.Duration;markers.Start=0;markers.End=info.Duration;await PrepareAudioAsync(info);if(closed)return;audioReady=true;SetPlaying(!this.trimming);actions.IsEnabled=true;play.IsEnabled=true;Update();}
        catch(Exception e){if(!closed){message.Text=e.Message;previewReady.TrySetException(e);}}
        finally{ready.TrySetResult();}
    }
    private async Task PrepareAudioAsync(ClipFiles.Info clip){
        string cache=Path.Combine(ReplayEngine.DataFolder,".cache","audio");Directory.CreateDirectory(cache);
        foreach(var track in AudioCompatibility.Editable(clip.Audio)){
            string file=Path.Combine(cache,Guid.NewGuid().ToString("N")+".m4a");audioFiles.Add(file);
            await ReplayEngine.RunToolAsync("ffmpeg",["-v","error","-nostdin","-i",path,"-map","0:"+track.Stream,"-c","copy","-f","mp4",file],TimeSpan.FromSeconds(60),loading.Token);
            if(closed){try{File.Delete(file);}catch(IOException){}return;}
            var player=new MediaElement{LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Close,Width=1,Height=1,Opacity=0,IsHitTestVisible=false,Volume=1};mediaHost.Children.Add(player);
            var keep=new CheckBox{IsChecked=true,Margin=new Thickness(8,0,8,0)};var level=new Slider{Minimum=0,Maximum=1,Value=1,Width=140,ToolTip="Preview volume only."};
            var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,6,0,6)};row.Children.Add(keep);row.Children.Add(new TextBlock{Text=track.Title,Width=160,VerticalAlignment=VerticalAlignment.Center,Foreground=ThemePalette.Foreground});row.Children.Add(level);audioTracks.Children.Add(row);keep.ToolTip="Keep this audio track in the saved edit.";
            keep.Click+=(_,_)=>ApplyPreviewVolume();level.ValueChanged+=(_,_)=>ApplyPreviewVolume();audio.Add((track,player,keep,level));ApplyPreviewVolume();player.Source=new Uri(file);player.Play();player.Pause();
        }
        if(clip.Audio.Length>0)audioTracks.Children.Add(new TextBlock{Text="Select which audio tracks to save. Volume sliders affect preview playback.",FontSize=11,Foreground=ThemePalette.Muted,TextWrapping=TextWrapping.Wrap});
    }
    private void ApplyPreviewVolume()
    {
        foreach(var item in audio)item.Player.Volume=item.Keep.IsChecked==true?previewVolume.Value*item.Level.Value:0;
    }
    internal void SeekFraction(double fraction){if(info is null||busy||closed)return;double value=info.Duration*Math.Clamp(fraction,0,1);if(seeking)QueueSeek(value);else Seek(value);}
    private void BeginScrub(){
        if(seeking||closed||busy||releasing||info is null)return;
        bool resume=playing||resumePending;SetPlaying(false);resumeAfterSeek=resume;seeking=true;scrubPosition=timeline.Value;
        foreach(var item in audio)item.Player.IsMuted=true;
    }
    private void EndScrub(){
        if(!seeking)return;
        scrub.Stop();seeking=false; // Clear before releasing capture, which raises LostMouseCapture.
        if(timeline.IsMouseCaptured)timeline.ReleaseMouseCapture();
        if(closed||busy||releasing)return;
        SeekVideo(scrubPosition);foreach(var item in audio)item.Player.Position=video.Position;
        resumePending=resumeAfterSeek;seekResume.Stop();seekResume.Start();
    }
    private void QueueSeek(double value){
        if(closed||busy||releasing)return;
        scrubPosition=value;updating=true;timeline.Value=value;updating=false;
        // Keep only the newest request. Audio stays paused and is positioned once on release.
        if(!scrub.IsEnabled)scrub.Start();
    }
    private void SeekVideo(double value){video.Position=TimeSpan.FromSeconds(Math.Clamp(value,0,info?.Duration??double.MaxValue));markers.Position=value;updating=true;timeline.Value=value;updating=false;Update();}
    private void Seek(double value){if(closed||busy)return;video.Position=TimeSpan.FromSeconds(Math.Max(0,value));foreach(var item in audio)item.Player.Position=video.Position;markers.Position=value;updating=true;timeline.Value=value;updating=false;Update();}
    private bool OutsideSelection(double position)=>position<markers.Start||position>=markers.End-.01;
    private bool TogglePlayback()
    {
        if(!audioReady||!play.IsEnabled||closed||busy||releasing)return false;
        bool next=!(playing||resumePending||(seeking&&resumeAfterSeek));
        EndScrub();
        if(next&&trimming&&!Removing&&OutsideSelection(video.Position.TotalSeconds))Seek(markers.Start);
        SetPlaying(next);
        return true;
    }
    private void SetPlaying(bool value){seekResume.Stop();resumePending=false;playing=value;play.Content=playing?"Pause":"Play";foreach(var item in audio)item.Player.IsMuted=seeking;if(value){video.Play();foreach(var item in audio){item.Player.Position=video.Position;item.Player.Play();}progress.Start();}else{video.Pause();foreach(var item in audio)item.Player.Pause();progress.Stop();}}
    private void Update(){
        trimArea.Visibility=trimming?Visibility.Visible:Visibility.Collapsed;edit.Content=trimming?"Close trimming":"Trim clip";
        if(info is null)return;range.Text=$"{TimeSpan.FromSeconds(video.Position.TotalSeconds):mm\\:ss\\.f} / {TimeSpan.FromSeconds(info.Duration):mm\\:ss\\.f}";
        if(saveError is not null)return;
        if(Removing&&!busy){message.Text=$"Remove {TimeSpan.FromSeconds(markers.Start):mm\\:ss\\.f} – {TimeSpan.FromSeconds(markers.End):mm\\:ss\\.f} ({TimeSpan.FromSeconds(markers.End-markers.Start):mm\\:ss\\.f}).";return;}
        if(!busy)message.Text=trimming?$"Keep {TimeSpan.FromSeconds(markers.Start):mm\\:ss\\.f} â€“ {TimeSpan.FromSeconds(markers.End):mm\\:ss\\.f} ({TimeSpan.FromSeconds(markers.End-markers.Start):mm\\:ss\\.f}).":"";
    }
    private async Task SaveAsync(bool overwrite){
        if(busy||info is null)return;double start=markers.Start,end=markers.End;bool removing=Removing;
        int[] keep=audio.Where(a=>a.Keep.IsChecked==true).Select(a=>a.Track.Ordinal).ToArray();
        busy=true;saveError=null;savedPath=null;trimArea.IsEnabled=false;timeline.IsEnabled=false;edit.IsEnabled=false;play.IsEnabled=false;
        bool canAdvance=nextClip.IsEnabled;nextClip.IsEnabled=false;
        using var cancellation=new CancellationTokenSource();saveCancellation=cancellation;
        var progress=new Progress<string>(stage=>{if(!closed)message.Text=stage;});
        try{
            message.Text="Closing preview...";await ReleasePlayerAsync(overwrite,cancellation.Token);
            string result=await Task.Run(()=>ClipFiles.TrimAsync(path,start,end,overwrite,progress,cancellation.Token,removing,keep));
            savedPath=result;engine.ScheduleValidation(result);Dismiss();saved?.Invoke(result);engine.NotifyClipsChanged();
        }catch(Exception e){
            saveError=e;if(closed)return;message.Text=e is OperationCanceledException?"Trim cancelled or timed out.":e.Message;busy=false;trimArea.IsEnabled=true;timeline.IsEnabled=true;edit.IsEnabled=true;play.IsEnabled=true;nextClip.IsEnabled=canAdvance;releasing=false;
            if(!mediaHost.Children.Contains(video))mediaHost.Children.Add(video);video.Source=new Uri(path);video.Play();video.Pause();foreach(var item in audio){if(!mediaHost.Children.Contains(item.Player))mediaHost.Children.Add(item.Player);int i=audio.IndexOf(item);if(i<audioFiles.Count&&File.Exists(audioFiles[i])){item.Player.Source=new Uri(audioFiles[i]);item.Player.Play();item.Player.Pause();}}
        }finally{saveCancellation=null;busy=false;}
    }
    private async Task ReleasePlayerAsync(bool overwrite,CancellationToken cancellation){
        releasing=true;Release();video.Clock=null;mediaHost.Children.Remove(video);foreach(var item in audio)mediaHost.Children.Remove(item.Player);
        // Let WPF apply Close/Source=null and unload the media presentation before probing its OS handle.
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
        if(overwrite)await Task.Run(()=>ClipFiles.WaitForFileReleaseAsync(path,TimeSpan.FromSeconds(8),cancellation));
    }
    private void Release(){progress.Stop();scrub.Stop();seekResume.Stop();resumePending=false;seeking=false;playing=false;video.Stop();video.Source=null;video.Close();foreach(var item in audio){item.Player.Stop();item.Player.Source=null;item.Player.Close();}}
    private void Finish(){loading.Cancel();ready.TrySetCanceled();previewReady.TrySetCanceled();video.MediaOpened-=opened;video.MediaEnded-=ended;video.MediaFailed-=failed;Release();string[] disposableFiles=audioFiles.ToArray();foreach(var item in audio)mediaHost.Children.Remove(item.Player);mediaHost.Children.Remove(video);audioFiles.Clear();audio.Clear();audioTracks.Children.Clear();
        _=Task.Run(async()=>{foreach(string file in disposableFiles)for(int attempt=0;attempt<6;attempt++){try{File.Delete(file);break;}catch(IOException){await Task.Delay(60);}catch(UnauthorizedAccessException){break;}}});}
    internal void CloseForHide(){if(closed)return;saveCancellation?.Cancel();closed=true;releasing=true;Finish();Dispatcher.BeginInvoke(close);}
    private void Dismiss(){closed=true;Finish();close();}
}
