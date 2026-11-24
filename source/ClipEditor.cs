using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Threading;

namespace Rewind;
// Preview and multi-segment editing share the same decoder.
internal sealed class ClipEditor : Grid
{
    private readonly string path;
    private readonly ReplayEngine engine;
    private readonly Action close;
    private readonly Grid mediaHost=new(){Background=Brushes.Black,ClipToBounds=true};
    private readonly Canvas videoCanvas=new(){ClipToBounds=true};
    private readonly MediaElement video=new(){LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Close,Stretch=Stretch.Fill,Volume=0};
    private readonly Slider timeline=new(){Minimum=0,TickFrequency=.1,Height=32,Background=Brushes.Transparent};
    private readonly VideoCropOverlay cropOverlay=new(){Visibility=Visibility.Collapsed};
    private readonly VideoEditPanel editPanel;
    private readonly Grid content=new(){Margin=new Thickness(24)};
    private readonly FrameworkElement header,fileLabel;
    private readonly Button fullscreenButton=new();
    private readonly TextBlock range=new(),message=new(){TextWrapping=TextWrapping.Wrap,Foreground=ThemePalette.Muted};
    private readonly StackPanel trimArea=new();
    private readonly WrapPanel actions=new();
    private readonly StackPanel audioTracks=new();
    private readonly List<(ClipFiles.AudioTrack Track,MediaElement Player,CheckBox Keep,Slider Level)> audio=new();
    private readonly CancellationTokenSource loading=new();
    private readonly List<string> audioFiles=new();
    private bool audioReady;
    private readonly Button play=new(),edit=new();
    private readonly Button previousClip=new(),nextClip=new(),mute=new();
    private readonly Button retry=new(){Content="Retry preview",HorizontalAlignment=HorizontalAlignment.Left,Visibility=Visibility.Collapsed};
    private bool muted,videoReady,fullscreen;
    private string? previewError;
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
    private (Rect Clip,Matrix Transform)? currentFraming;
    private readonly TaskCompletionSource ready=new(),previewReady=new();
    private RoutedEventHandler opened=null!,ended=null!;private EventHandler<ExceptionRoutedEventArgs> failed=null!;
    internal Task Ready=>ready.Task;
    internal Task PreviewReady=>previewReady.Task;
    internal ClipEditor(string path,ReplayEngine engine,Action close,bool trimming=true,Action<string>? saved=null,Action<double,bool>? next=null,double volume=1,bool muted=false,Action<double,bool>? previous=null,Action? toggleFullscreen=null){
        this.path=path;this.engine=engine;this.close=close;this.trimming=trimming;this.saved=saved;this.muted=muted;
        editPanel=new VideoEditPanel(()=>timeline.Value);
        editPanel.Track.ScrubStarted+=BeginScrub;
        editPanel.Track.Seek+=position=>{if(info is not null&&seeking)QueueSeek(position);};
        editPanel.Track.ScrubEnded+=EndScrub;
        editPanel.Changed+=()=>{if(editPanel.Cropping&&(playing||resumePending))SetPlaying(false);saveError=null;Update();};
        cropOverlay.Changed+=editPanel.SetCrop;
        Focusable=true;
        System.Windows.Documents.TextElement.SetFontFamily(this,Typography.Inter);Background=ThemePalette.Surface;
        for(int i=0;i<8;i++)content.RowDefinitions.Add(new(){Height=i==2?new GridLength(1,GridUnitType.Star):GridLength.Auto});Children.Add(content);
        var heading=new DockPanel();var cancel=Button("X",()=>{if(busy)saveCancellation?.Cancel();else Dismiss();});cancel.ToolTip="Close preview";DockPanel.SetDock(cancel,Dock.Right);heading.Children.Add(cancel);
        var headingText=new TextBlock{Text=trimming?"Edit Video":"Clip preview",FontSize=22,FontWeight=FontWeights.SemiBold,Foreground=ThemePalette.Foreground};
        heading.Children.Add(headingText);header=heading;content.Children.Add(header);
        fileLabel=new TextBlock{Text=Path.GetFileName(path),Foreground=ThemePalette.Muted,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,10,0,12)};
        content.Children.Add(fileLabel);
        videoCanvas.Children.Add(video);mediaHost.Children.Add(videoCanvas);
        SetZIndex(cropOverlay,1);mediaHost.Children.Add(cropOverlay);content.Children.Add(mediaHost);
        mediaHost.SizeChanged+=(_,_)=>ApplyFraming();
        var controls=new Grid{Margin=new Thickness(0,10,0,0)};
        controls.ColumnDefinitions.Add(new());
        controls.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var playback=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        controls.Children.Add(playback);
        SetPlayIcon();
        SetIcon(nextClip,"M4,3 L4,17 L14,10 Z M16,3 L16,17 L18,17 L18,3 Z","Next clip");
        play.Click+=(_,_)=>TogglePlayback();
        mediaHost.MouseLeftButtonUp+=(_,e)=>{if(!editPanel.Cropping&&TogglePlayback())e.Handled=true;};
        SetIcon(previousClip,"M16,3 L16,17 L6,10 Z M4,3 L4,17 L2,17 L2,3 Z","Previous clip");
        previousClip.IsEnabled=previous is not null;
        previousClip.Click+=(_,_)=>{if(!busy&&!closed)previous?.Invoke(previewVolume.Value,this.muted);};
        playback.Children.Add(previousClip);
        playback.Children.Add(Button("−5s",()=>SeekRelative(-5)));
        playback.Children.Add(play);
        playback.Children.Add(Button("+5s",()=>SeekRelative(5)));
        nextClip.IsEnabled=next is not null;
        nextClip.ToolTip=next is null?"Last clip in the gallery":"Play the next clip in gallery order";
        nextClip.Click+=(_,_)=>{if(!busy&&!closed)next?.Invoke(previewVolume.Value,this.muted);};
        playback.Children.Add(nextClip);
        range.Foreground=ThemePalette.Foreground;range.VerticalAlignment=VerticalAlignment.Center;range.Margin=new Thickness(10,0,14,0);playback.Children.Add(range);
        var volumeControls=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)};
        RefreshMuteIcon();
        mute.Click+=(_,_)=>ToggleMute();
        volumeControls.Children.Add(mute);
        System.Windows.Automation.AutomationProperties.SetName(previewVolume,"Playback volume");
        previewVolume.Value=volume;previewVolume.ValueChanged+=(_,_)=>ApplyPreviewVolume();
        volumeControls.Children.Add(previewVolume);playback.Children.Add(volumeControls);
        edit.HorizontalAlignment=HorizontalAlignment.Right;edit.VerticalAlignment=VerticalAlignment.Center;
        edit.FontWeight=FontWeights.SemiBold;
        edit.Click+=(_,_)=>{
            this.trimming=!this.trimming;
            headingText.Text=this.trimming?"Edit Video":"Clip preview";
            progress.Interval=TimeSpan.FromMilliseconds(this.trimming?33:100);
            if(!this.trimming)editPanel.StopCrop();
            Update();
        };
        var viewerActions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        viewerActions.Children.Add(edit);
        if(toggleFullscreen is not null)
        {
            fullscreenButton.Click+=(_,_)=>{if(!busy)toggleFullscreen();};
            viewerActions.Children.Add(fullscreenButton);
            SetFullscreenIcon();
        }
        SetColumn(viewerActions,1);
        controls.Children.Add(viewerActions);
        content.Children.Add(controls);content.Children.Add(timeline);
        trimArea.Children.Add(editPanel);
        var export=new Grid{Margin=new Thickness(0,8,0,0)};
        export.ColumnDefinitions.Add(new());
        export.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        export.Children.Add(new Expander{Header="Export audio tracks",Foreground=ThemePalette.Foreground,
            Content=new SmoothScrollViewer{Content=audioTracks,MaxHeight=84,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden}});
        actions.HorizontalAlignment=HorizontalAlignment.Right;
        actions.VerticalAlignment=VerticalAlignment.Top;
        actions.Children.Add(Button("Save as New File",()=>_=SaveAsync(false)));
        actions.Children.Add(Button("Overwrite Current File",()=>_=SaveAsync(true)));
        SetColumn(actions,1);
        export.Children.Add(actions);
        trimArea.Children.Add(export);
        content.Children.Add(trimArea);content.Children.Add(message);content.Children.Add(retry);retry.Click+=(_,_)=>OpenVideo();for(int i=0;i<content.Children.Count;i++)Grid.SetRow(content.Children[i],i);
        timeline.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((_,_)=>BeginScrub()));timeline.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,_)=>EndScrub()));
        timeline.PreviewMouseLeftButtonDown+=(_,e)=>{if(info is null||busy)return;BeginScrub();e.Handled=true;SeekFraction(e.GetPosition(timeline).X/Math.Max(1,timeline.ActualWidth));timeline.CaptureMouse();};
        timeline.PreviewMouseMove+=(_,e)=>{if(timeline.IsMouseCaptured&&e.LeftButton==System.Windows.Input.MouseButtonState.Pressed)SeekFraction(e.GetPosition(timeline).X/Math.Max(1,timeline.ActualWidth));};
        timeline.PreviewMouseLeftButtonUp+=(_,_)=>EndScrub();
        timeline.LostMouseCapture+=(_,_)=>EndScrub();
        timeline.ValueChanged+=(_,_)=>{if(!updating&&seeking)QueueSeek(timeline.Value);};
        opened=(_,_)=>{
            if(closed||busy||releasing)return;
            videoReady=true;previewError=null;retry.Visibility=Visibility.Collapsed;
            video.Position=TimeSpan.Zero;EnablePlayback();Update();previewReady.TrySetResult();
        };
        ended=(_,_)=>{if(!playing||busy||releasing)return;SetPlaying(false);Seek(this.trimming?editPanel.NextKept(0)??0:0);};
        failed=(_,e)=>{
            if(busy||releasing||closed)return;
            ShowPreviewError(e.ErrorException);
        };
        video.MediaOpened+=opened;video.MediaEnded+=ended;video.MediaFailed+=failed;
        progress.Tick+=(_,_)=>{
            if(closed||!IsVisible){SetPlaying(false);return;}
            double position=video.Position.TotalSeconds;
            if(this.trimming&&!editPanel.IsKept(position))
            {
                if(editPanel.NextKept(position) is double nextPosition){Seek(nextPosition);return;}
                SetPlaying(false);Update();return;
            }
            if(!seeking){updating=true;timeline.Value=position;updating=false;}
            if(playing)foreach(var item in audio)
                if(Math.Abs(item.Player.Position.TotalSeconds-position)>.12)item.Player.Position=video.Position;
            Update();
        };
        scrub.Tick+=(_,_)=>{scrub.Stop();if(seeking&&!closed&&!busy)SeekVideo(scrubPosition);};
        seekResume.Tick+=(_,_)=>{seekResume.Stop();bool resume=resumePending;resumePending=false;if(!closed&&!busy&&!releasing&&!seeking&&IsVisible)SetPlaying(resume);};
        PreviewKeyDown+=(_,e)=>{
            if(Keyboard.Modifiers!=ModifierKeys.None||closed||busy||releasing)return;
            if(toggleFullscreen is not null&&(fullscreen&&e.Key==Key.Escape||e.Key==Key.F&&Keyboard.FocusedElement is not TextBox))
            {if(!e.IsRepeat)toggleFullscreen();e.Handled=true;return;}
            if(e.Key==Key.M){if(!e.IsRepeat)ToggleMute();e.Handled=true;return;}
            if(trimArea.IsKeyboardFocusWithin&&!editPanel.Track.IsKeyboardFocusWithin)return;
            if(e.Key is Key.Left or Key.Right){SeekRelative(e.Key==Key.Left?-5:5);e.Handled=true;}
            if(e.Key is Key.Up or Key.Down){previewVolume.Value=Math.Clamp(previewVolume.Value+(e.Key==Key.Up?.05:-.05),0,1);e.Handled=true;}
        };
        IsVisibleChanged+=(_,_)=>{if(!IsVisible&&!closed)CloseForHide();};Unloaded+=(_,_)=>Release();
        Loaded+=async(_,_)=>{Focus();await LoadAsync();};Update();
    }
    private static Button Button(string text,Action action){var button=new Button{Content=text,FontWeight=FontWeights.SemiBold};button.Click+=(_,_)=>action();return button;}
    private static void SetIcon(Button button,string geometry,string label){
        button.Content=new System.Windows.Shapes.Path{Data=Geometry.Parse(geometry),Stroke=ThemePalette.Foreground,
            StrokeThickness=1.7,Width=15,Height=15,Stretch=Stretch.Uniform};
        button.ToolTip=label;AutomationProperties.SetName(button,label);
    }
    private void SetPlayIcon()=>SetIcon(play,playing?"M6,3 L6,17 M13,3 L13,17":"M5,3 L16,10 L5,17 Z",playing?"Pause":"Play");
    private void SetFullscreenIcon()=>SetIcon(fullscreenButton,
        fullscreen?"M2,7 L7,7 L7,2 M13,2 L13,7 L18,7 M2,13 L7,13 L7,18 M13,18 L13,13 L18,13":
            "M2,7 L2,2 L7,2 M13,2 L18,2 L18,7 M2,13 L2,18 L7,18 M13,18 L18,18 L18,13",
        fullscreen?"Exit fullscreen (Esc)":"Fullscreen (F)");
    internal void SetFullscreen(bool enabled)
    {
        if(fullscreen==enabled)return;
        fullscreen=enabled;
        if(enabled)editPanel.StopCrop();
        var visibility=enabled?Visibility.Collapsed:Visibility.Visible;
        header.Visibility=fileLabel.Visibility=edit.Visibility=visibility;
        content.Margin=enabled?new Thickness(0):new Thickness(24);
        SetFullscreenIcon();
        Update();
        Focus();
    }
    private void RefreshMuteIcon()=>SetIcon(mute,muted?"M2,8 L6,8 L11,3 L11,21 L6,16 L2,16 Z M15,9 L21,15 M21,9 L15,15":
        "M2,8 L6,8 L11,3 L11,21 L6,16 L2,16 Z M15,8 Q20,12 15,16 M18,4 Q27,12 18,20",muted?"Unmute (M)":"Mute (M)");
    private void ToggleMute(){muted=!muted;RefreshMuteIcon();ApplyPreviewVolume();}
    private void EnablePlayback(){
        play.IsEnabled=videoReady&&audioReady;
        if(play.IsEnabled)SetPlaying(!trimming);
    }
    private void OpenVideo(){
        if(closed||busy||releasing)return;
        SetPlaying(false);videoReady=false;play.IsEnabled=false;previewError=null;retry.Visibility=Visibility.Collapsed;
        message.Text="Loading preview...";
        try{
            LocalPaths.Require(path);
            if(!File.Exists(path))throw new FileNotFoundException();
            video.Close();video.Source=new Uri(path,UriKind.Absolute);video.Play();video.Pause();
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException){
            ShowPreviewError(e);
        }
    }
    private void ShowPreviewError(Exception error){
        SetPlaying(false);videoReady=false;play.IsEnabled=false;
        bool missing=!File.Exists(path);
        previewError=missing?"This clip is missing or was moved. Close the preview to refresh the clips list.":
            $"Windows could not play this preview (0x{error.HResult:X8}). Try again or open the clip from its folder.";
        message.Text=previewError;retry.Visibility=missing?Visibility.Collapsed:Visibility.Visible;
        if(missing)_=engine.RefreshGalleryAsync();
        PrivateLog.Write("preview.log",$"Video preview failed (0x{error.HResult:X8}).");
        previewReady.TrySetException(error);
    }
    private async Task LoadAsync(){
        actions.IsEnabled=false;play.IsEnabled=false;message.Text="Loading clip...";
        try{
            string localPath=LocalPaths.Require(path);
            if(!File.Exists(localPath))throw new FileNotFoundException();
            var result=await ClipFiles.ProbeAsync(localPath,loading.Token);if(closed||!IsVisible)return;
            info=result;timeline.Maximum=info.Duration;editPanel.Load(info);
            OpenVideo();
            await PrepareAudioAsync(info);if(closed)return;
            audioReady=true;actions.IsEnabled=true;EnablePlayback();Update();
        }
        catch(Exception e){
            if(!closed){
                ShowPreviewError(e);
                retry.Visibility=Visibility.Collapsed;
                if(File.Exists(path))message.Text="Could not prepare this clip for preview. Try reopening it or play the file from its folder.";
            }
        }
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
        foreach(var item in audio)item.Player.Volume=!muted&&item.Keep.IsChecked==true?previewVolume.Value*item.Level.Value:0;
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
        if(trimming)editPanel.SetPlayhead(value);
        // Keep only the newest request. Audio stays paused and is positioned once on release.
        if(!scrub.IsEnabled)scrub.Start();
    }
    private void SeekVideo(double value){video.Position=TimeSpan.FromSeconds(Math.Clamp(value,0,info?.Duration??double.MaxValue));updating=true;timeline.Value=value;updating=false;Update();}
    private void Seek(double value){if(closed||busy)return;SeekVideo(value);foreach(var item in audio)item.Player.Position=video.Position;}
    private void SeekRelative(double seconds)
    {
        if(!videoReady||busy||closed||releasing||seeking)return;
        Seek(video.Position.TotalSeconds+seconds);
    }
    private bool TogglePlayback()
    {
        if(!videoReady||!audioReady||!play.IsEnabled||closed||busy||releasing)return false;
        bool next=!(playing||resumePending||(seeking&&resumeAfterSeek));
        EndScrub();
        if(next&&trimming)
        {
            editPanel.StopCrop();
            double position=video.Position.TotalSeconds;
            if(!editPanel.IsKept(position))
            {
                if((editPanel.NextKept(position)??editPanel.NextKept(0)) is not double kept)return false;
                Seek(kept);
            }
        }
        SetPlaying(next);
        return true;
    }
    private void SetPlaying(bool value){seekResume.Stop();resumePending=false;playing=value;SetPlayIcon();foreach(var item in audio)item.Player.IsMuted=seeking;if(value){video.Play();foreach(var item in audio){item.Player.Position=video.Position;item.Player.Play();}progress.Interval=TimeSpan.FromMilliseconds(trimming?33:100);progress.Start();}else{video.Pause();foreach(var item in audio)item.Player.Pause();progress.Stop();}}
    private void Update(){
        trimArea.Visibility=trimming&&!fullscreen?Visibility.Visible:Visibility.Collapsed;edit.Content=trimming?"Close editor":"Edit Video";
        timeline.Visibility=trimming&&!fullscreen?Visibility.Collapsed:Visibility.Visible;
        if(trimming)editPanel.SetPlayhead(timeline.Value);
        ApplyFraming();
        if(info is null)return;range.Text=$"{TimeSpan.FromSeconds(video.Position.TotalSeconds):mm\\:ss\\.f} / {TimeSpan.FromSeconds(info.Duration):mm\\:ss\\.f}";
        if(saveError is not null||previewError is not null)return;
        if(!busy)message.Text=trimming&&editPanel.Cropping?"Drag the crop edges or corners. This crop applies to the selected segment.":"";
    }

    private void ApplyFraming()
    {
        bool cropMode=trimming&&editPanel.Cropping&&videoReady;
        cropOverlay.Visibility=cropMode?Visibility.Visible:Visibility.Collapsed;
        if(info is null||info.Width<2||info.Height<2||!videoReady||mediaHost.ActualWidth<=0||mediaHost.ActualHeight<=0)return;
        var area=new Rect(0,0,mediaHost.ActualWidth,mediaHost.ActualHeight);
        var source=Fit(area,info.Width,info.Height);
        Canvas.SetLeft(video,source.Left);Canvas.SetTop(video,source.Top);
        video.Width=source.Width;video.Height=source.Height;
        if(cropMode)cropOverlay.Refresh(source,editPanel.CurrentCrop);
        var crop=trimming&&!cropMode?editPanel.CropAt(timeline.Value):null;
        var target=editPanel.PreviewSize();
        if(!trimming||cropMode||(crop is null&&target.Width==info.Width&&target.Height==info.Height))
        {
            if(currentFraming is not null){video.Clip=null;video.RenderTransform=Transform.Identity;currentFraming=null;}
            return;
        }
        var pixels=(crop??ClipFiles.VideoCrop.Full).Pixels(new ClipFiles.VideoSize(info.Width,info.Height));
        var actual=new Rect(source.Width*pixels.X/info.Width,source.Height*pixels.Y/info.Height,
            source.Width*pixels.Width/info.Width,source.Height*pixels.Height/info.Height);
        var canvas=Fit(area,target.Width,target.Height);
        var destination=Fit(canvas,pixels.Width,pixels.Height);
        double scale=destination.Width/actual.Width;
        var transform=new Matrix(scale,0,0,scale,destination.Left-source.Left-actual.Left*scale,destination.Top-source.Top-actual.Top*scale);
        if(currentFraming==(actual,transform))return;
        currentFraming=(actual,transform);
        video.Clip=new RectangleGeometry(actual);
        video.RenderTransform=new MatrixTransform(transform);
    }

    private static Rect Fit(Rect area,int width,int height)
    {
        double scale=Math.Min(area.Width/width,area.Height/height);
        double w=width*scale,h=height*scale;
        return new Rect(area.X+(area.Width-w)/2,area.Y+(area.Height-h)/2,w,h);
    }

    private async Task SaveAsync(bool overwrite){
        if(busy||info is null)return;
        var segments=editPanel.Kept();
        ClipFiles.VideoSize? size;
        try
        {
            if(segments.Length==0)throw new InvalidDataException("Keep at least one segment before exporting.");
            size=editPanel.Resized();
        }
        catch(InvalidDataException error){saveError=error;message.Text=error.Message;return;}
        editPanel.StopCrop();
        int[] keep=audio.Where(a=>a.Keep.IsChecked==true).Select(a=>a.Track.Ordinal).ToArray();
        busy=true;saveError=null;savedPath=null;trimArea.IsEnabled=false;timeline.IsEnabled=false;edit.IsEnabled=false;play.IsEnabled=false;
        bool canAdvance=nextClip.IsEnabled;nextClip.IsEnabled=false;
        using var cancellation=new CancellationTokenSource();saveCancellation=cancellation;
        var progress=new Progress<string>(stage=>{if(!closed)message.Text=stage;});
        try{
            message.Text="Closing preview...";await ReleasePlayerAsync(overwrite,cancellation.Token);
            string result=await Task.Run(()=>ClipFiles.SaveEditAsync(path,segments,size,overwrite,progress,cancellation.Token,keep));
            savedPath=result;engine.ScheduleValidation(result);Dismiss();saved?.Invoke(result);engine.NotifyClipsChanged();
        }catch(Exception e){
            saveError=e;if(closed)return;message.Text=e is OperationCanceledException?"Edit cancelled or timed out.":e.Message;busy=false;trimArea.IsEnabled=true;timeline.IsEnabled=true;edit.IsEnabled=true;play.IsEnabled=true;nextClip.IsEnabled=canAdvance;releasing=false;
            if(!videoCanvas.Children.Contains(video))videoCanvas.Children.Add(video);busy=false;OpenVideo();if(previewError is null)message.Text=e is OperationCanceledException?"Edit cancelled or timed out.":e.Message;foreach(var item in audio){if(!mediaHost.Children.Contains(item.Player))mediaHost.Children.Add(item.Player);int i=audio.IndexOf(item);if(i<audioFiles.Count&&File.Exists(audioFiles[i])){item.Player.Source=new Uri(audioFiles[i]);item.Player.Play();item.Player.Pause();}}
        }finally{saveCancellation=null;busy=false;}
    }
    private async Task ReleasePlayerAsync(bool overwrite,CancellationToken cancellation){
        releasing=true;Release();video.Clock=null;videoCanvas.Children.Remove(video);foreach(var item in audio)mediaHost.Children.Remove(item.Player);
        // Let WPF apply Close/Source=null and unload the media presentation before probing its OS handle.
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
        if(overwrite)await Task.Run(()=>ClipFiles.WaitForFileReleaseAsync(path,TimeSpan.FromSeconds(8),cancellation));
    }
    private void Release(){progress.Stop();scrub.Stop();seekResume.Stop();resumePending=false;seeking=false;playing=false;video.Stop();video.Source=null;video.Close();foreach(var item in audio){item.Player.Stop();item.Player.Source=null;item.Player.Close();}}
    private void Finish(){loading.Cancel();ready.TrySetCanceled();previewReady.TrySetCanceled();video.MediaOpened-=opened;video.MediaEnded-=ended;video.MediaFailed-=failed;Release();string[] disposableFiles=audioFiles.ToArray();foreach(var item in audio)mediaHost.Children.Remove(item.Player);videoCanvas.Children.Remove(video);audioFiles.Clear();audio.Clear();audioTracks.Children.Clear();
        _=Task.Run(async()=>{foreach(string file in disposableFiles)for(int attempt=0;attempt<6;attempt++){try{File.Delete(file);break;}catch(IOException){await Task.Delay(60);}catch(UnauthorizedAccessException){break;}}});}
    internal void CloseForHide(){if(closed)return;saveCancellation?.Cancel();closed=true;releasing=true;Finish();Dispatcher.BeginInvoke(close);}
    private void Dismiss(){closed=true;Finish();close();}
}
