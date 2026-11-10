using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orientation=System.Windows.Controls.Orientation;

namespace Rewind;
// Two independent views share one recorder. Rendering and polling stop while hidden.
internal sealed class Shell : Window
{
    private readonly ReplayEngine engine;
    private readonly SaveAdmission saveAdmission=new();
    private readonly List<WeakReference<ReplaySaveButton>> saveButtons=new();
    private readonly List<WeakReference<ShortcutField>> shortcutFields=new();
    private readonly List<WeakReference<QualityControls>> qualityControls=new();
    private int saveState;private Guid saveRequest;
    private ShortcutField? listeningField;
    private Action<string>? uiEngineChanged;private Action? uiClipsChanged;
    private bool uiObserved;private int refreshQueued;
    public event Func<string,string,string,bool>? ShortcutApplyRequested;
    public event Action<bool>? ShortcutCaptureStateChanged;
    public event Action<string>? SavedClipNotification;
    internal event Action? ViewSettingsChanged;
    internal ProductSettings Preferences=>preferences;
    private readonly Action exit;
    private readonly bool inspection;
    private readonly ProductSettings preferences;
    private readonly Border overlaySurface=new();
    private readonly ContentControl overlayBody=new();
    private readonly ContentControl homeBody=new();
    private readonly Grid overlayRoot=new();
    private Grid? homeRoot;
    private readonly Grid homeWorkspace=new();
    private readonly Dictionary<string,Button> homeTabs=new();
    private readonly TranslateTransform slide=new();
    private readonly DispatcherTimer preferenceTimer=new(){Interval=TimeSpan.FromMilliseconds(350)};
    private readonly DispatcherTimer dismissTimer=new(){Interval=TimeSpan.FromMilliseconds(16)};
    private readonly DispatcherTimer autoTimer=new(){Interval=TimeSpan.FromSeconds(2)};
    private Window? home;
    private bool armed=true,changing,quitting,wasDown,overlayWanted;
    private int transition;
    private Task? startupTask;
    private Storyboard? presentation;
    private IntPtr overlayForeground;
    private DateTime retryAfter;
    private string tab="Capture",homeTab="Capture",hotkeyWarning="",captureNotice="Starting replay…";
    private EventHandler? presentationReady;
    private string lastStartError="";
    private readonly Dictionary<string,Action> stateUpdates=new();
    private static readonly Brush Lime=ThemePalette.Accent;
    private static readonly Brush Muted=ThemePalette.Muted;
    public event Action<string,string>? ShortcutsChanged;
    public event Action<string>? NotificationRequested;
    public event Action<UpdateResult>? UpdateAvailable;
    private readonly CancellationTokenSource updateLifetime=new();
    private readonly List<WeakReference<Button>> updateButtons=new();
    private UpdateResult? updateResult;
    private bool updateBusy;
    public string PanelShortcut=>preferences.PanelShortcut;
    public string SaveShortcut=>preferences.SaveShortcut;
    public string ScreenshotShortcut=>preferences.ScreenshotShortcut;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointNative point);
    [StructLayout(LayoutKind.Sequential)] private struct PointNative{public int X,Y;}
    public Shell(ReplayEngine engine,Action exit,bool inspectionOnly=false)
    {
        this.engine=engine;this.exit=exit;inspection=inspectionOnly;preferences=inspection?new():ProductSettings.Load();
        ThemePalette.Apply(preferences.AccentHex,inspection?"Pure Dark Onyx":preferences.ThemeVariant);
        Title="FLICK Overlay";Opacity=1;Visibility=Visibility.Hidden;Width=430;Height=900;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        ShowActivated=false;ShowInTaskbar=false;Topmost=true;AllowsTransparency=true;Background=Brushes.Transparent;Foreground=ThemePalette.Foreground;
        FontFamily=Typography.Inter;FontSize=12;FontWeight=FontWeights.Medium;
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/FLICK;component/Theme.xaml",UriKind.Relative)});
        Icon=BitmapFrame.Create(new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","flick.ico")));
        overlaySurface.CornerRadius=new CornerRadius(0);overlaySurface.ClipToBounds=true;overlaySurface.Background=Brush("#07080A");overlaySurface.RenderTransform=slide;overlaySurface.CacheMode=new BitmapCache{SnapsToDevicePixels=true};overlaySurface.Opacity=0;slide.X=-Width;
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new());grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        grid.Children.Add(Header(false));Grid.SetRow(overlayBody,1);overlayBody.Margin=new Thickness(20);grid.Children.Add(overlayBody);
        var footer=new StackPanel{Margin=new Thickness(20)};var save=SaveButton();save.Height=46;footer.Children.Add(save);
        var pause=Button("Pause replay",()=>_=ToggleReplayAsync());footer.Children.Add(pause);
        var status=Text("",11);status.Foreground=Muted;footer.Children.Add(status);
        stateUpdates["footer"]=()=>{pause.Content=armed?"Pause replay":"Resume replay";status.Text=hotkeyWarning.Length>0?hotkeyWarning:engine.Active?"Replay ready":armed?captureNotice:"Replay paused";};
        Grid.SetRow(footer,2);grid.Children.Add(footer);overlaySurface.Child=grid;
        overlayRoot.Children.Add(new Border{ClipToBounds=true,Child=overlaySurface});Content=overlayRoot;
        SourceInitialized+=(_,_)=>{OverlayWindow.SetMouseOnly(this,true);HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(Hook);};
        Closing+=(_,e)=>{if(!quitting){e.Cancel=true;HidePanel();}};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){if(listeningField is not null)listeningField.CancelCapture();else HidePanel();e.Handled=true;}};
        PreviewGotKeyboardFocus+=(_,e)=>{if(listeningField is null)e.Handled=true;};
        dismissTimer.Tick+=(_,_)=>{
            IntPtr foreground=GetForegroundWindow();
            if(foreground!=IntPtr.Zero&&foreground!=overlayForeground){GetWindowThreadProcessId(foreground,out uint process);if(process!=Environment.ProcessId||(home is not null&&foreground==new WindowInteropHelper(home).Handle)){HidePanel();return;}}
            bool down=(GetAsyncKeyState(1)&0x8000)!=0;
            if(down&&!wasDown&&GetCursorPos(out var point)){
                var dpi=VisualTreeHelper.GetDpi(this);var rect=new Rect(Left*dpi.DpiScaleX,Top*dpi.DpiScaleY,ActualWidth*dpi.DpiScaleX,ActualHeight*dpi.DpiScaleY);
                if(!rect.Contains(point.X,point.Y))HidePanel();
            }
            wasDown=down;
        };
        autoTimer.Tick+=async(_,_)=>{if(IsVisible||home?.IsVisible==true)UpdateStates();if(!inspection&&armed&&!changing)await EnsureReplayAsync();};
        uiEngineChanged=message=>Dispatcher.BeginInvoke(()=>{if(!quitting&&(IsVisible||home?.IsVisible==true))UpdateStates();});
        uiClipsChanged=()=>{if(quitting||Dispatcher.HasShutdownStarted||Interlocked.Exchange(ref refreshQueued,1)!=0)return;Dispatcher.BeginInvoke(DispatcherPriority.Background,()=>{Interlocked.Exchange(ref refreshQueued,0);if(quitting)return;if(home?.IsVisible==true&&homeTab is "Clips" or "Capture")ShowHomeTab(homeTab);if(IsVisible&&tab=="Clips")ShowTab("Clips");});};
        preferenceTimer.Tick+=async(_,_)=>{preferenceTimer.Stop();try{await Task.Run(preferences.Save);}catch(Exception e){Log(e.Message);}};
        ShowTab("Capture");_=engine.RefreshGalleryAsync();
    }
    internal async Task BeginBackgroundAsync(){if(inspection)return;try{await Task.Run(()=>StorageLocations.Set(preferences.StoragePath));}catch(Exception e){NotificationRequested?.Invoke("Storage unavailable: "+e.Message);}try{preferences.ApplyStartup();}catch(Exception e){Log(e.Message);}autoTimer.Start();var start=EnsureReplayAsync();await Dispatcher.InvokeAsync(WarmOverlayLayout,DispatcherPriority.Background);await start;}
    private async Task EnsureReplayAsync()
    {
        if(changing||!armed||engine.Active||engine.IsSaving||DateTime.UtcNow<retryAfter||DateTime.UtcNow<engine.RestartAfterUtc)return;
        changing=true;captureNotice="Starting replay…";
        try{
            await engine.StopAsync();
            var settings=new CaptureSettings(preferences.Profile,preferences.Seconds,preferences.Desktop,preferences.Mic,preferences.InputId,"primary","Native Desktop","h264",preferences.AudioSources.ToArray(),preferences.FrameRate);
            startupTask=Task.Run(()=>engine.Start(settings));await startupTask;engine.SetAudio(preferences.Desktop,preferences.Mic);lastStartError="";captureNotice="Waiting for display…";
        }catch(Exception e){retryAfter=DateTime.UtcNow.AddSeconds(e is FileNotFoundException?10:2);captureNotice=e is FileNotFoundException?"Installation incomplete — extract the entire release ZIP.":"Display unavailable — retrying automatically…";if(lastStartError!=e.Message){lastStartError=e.Message;Log(e.ToString());NotificationRequested?.Invoke(e.Message);}}
        finally{changing=false;if(IsVisible||home?.IsVisible==true)UpdateStates();if(restartPending&&!restartLoop&&!quitting&&!inspection)_=RestartAsync();}
    }
    private static ImageSource LogoSource(){var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","flick.png"));image.EndInit();image.Freeze();return image;}
    private static Brush Brush(string color)=>ThemePalette.Resolve(color);
    private static TextBlock Text(string text,double size=12)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Foreground=ThemePalette.Foreground,Margin=new Thickness(0,4,0,4)};
    private static Button Button(string text,Action action){var b=new Button{Content=text,FontWeight=FontWeights.SemiBold};b.Click+=(_,_)=>action();return b;}
    private static Border Card(UIElement child){var card=new Border{Background=Brush("#07080A"),BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(12),Padding=new Thickness(10),Margin=new Thickness(0,8,10,8),Child=child};RoundClip(card);return card;}
    private static void RoundClip(FrameworkElement element){void Update(){element.Clip=new RectangleGeometry(new Rect(0,0,Math.Max(0,element.ActualWidth),Math.Max(0,element.ActualHeight)),12,12);}element.SizeChanged+=(_,_)=>Update();element.Loaded+=(_,_)=>Update();}
    private static Button IconButton(string geometry,string tooltip,Brush color,Action action){var b=Button("",action);b.ToolTip=tooltip;b.Width=32;b.Height=32;b.Padding=new Thickness(8);b.Background=Brushes.Transparent;b.BorderBrush=Brushes.Transparent;b.Content=new System.Windows.Shapes.Path{Data=Geometry.Parse(geometry),Stroke=color,StrokeThickness=1.6,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,Stretch=Stretch.Uniform};return b;}
    private static bool InsideButton(DependencyObject? element){while(element is not null){if(element is System.Windows.Controls.Primitives.ButtonBase)return true;element=VisualTreeHelper.GetParent(element);}return false;}
    private UIElement Header(bool dashboard)
    {
        var row=new Grid{Background=Brush("#07080A"),Margin=new Thickness(20,10,16,10),MinHeight=44};row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new());
        var brand=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};brand.Opacity=1;brand.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M14,1 L3,13 L10,13 L6,23 L21,8 L13,8 Z"),Fill=ThemePalette.Accent,Width=17,Height=21,Stretch=Stretch.Uniform,Margin=new Thickness(0,0,8,0)});var name=Text("FLICK",14);name.Foreground=ThemePalette.Accent;name.FontWeight=FontWeights.SemiBold;brand.Children.Add(name);row.Children.Add(brand);
        if(dashboard){var tabs=Tabs(true);Grid.SetColumn(tabs,1);row.Children.Add(tabs);row.MouseLeftButtonDown+=(_,e)=>{if(!InsideButton(e.OriginalSource as DependencyObject)){try{home?.DragMove();}catch(InvalidOperationException){}}};}
        var tools=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        if(dashboard)tools.Children.Add(IconButton("M2,8 L14,8","Minimize",Muted,()=>{if(home is not null)home.WindowState=WindowState.Minimized;}));
        tools.Children.Add(IconButton("M2,2 L14,14 M14,2 L2,14","Hide FLICK",Muted,()=>{if(dashboard)home?.Hide();else HidePanel();}));Grid.SetColumn(tools,2);row.Children.Add(tools);
        return new Border{BorderBrush=ThemePalette.Divider,BorderThickness=new Thickness(0,0,0,1),Child=row};
    }
    private UIElement Tabs(bool dashboard)
    {
        var row=new StackPanel{Orientation=Orientation.Horizontal};
        foreach(string name in new[]{"Capture","Clips","Settings"}){var b=Button(name,()=>{if(dashboard)ShowHomeTab(name);else ShowTab(name);});b.Padding=new Thickness(16,10,16,10);b.Background=(dashboard?homeTab:tab)==name?Brush("#1B2026"):Brushes.Transparent;b.Foreground=(dashboard?homeTab:tab)==name?ThemePalette.Foreground:Muted;b.BorderBrush=Brushes.Transparent;if(dashboard)homeTabs[name]=b;row.Children.Add(b);}return row;
    }
    private UIElement Page(bool dashboard,string name)
    {
        var page=new Grid();page.RowDefinitions.Add(new(){Height=GridLength.Auto});page.RowDefinitions.Add(new());if(!dashboard)page.Children.Add(Tabs(false));
        var view=name=="Capture"?CaptureView(dashboard):name=="Clips"?ClipsView(dashboard):SettingsView();
        var scroll=new SmoothScrollViewer{VerticalScrollBarVisibility=name=="Clips"?ScrollBarVisibility.Disabled:ScrollBarVisibility.Hidden,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalContentAlignment=VerticalAlignment.Stretch,Content=view};
        if(name is "Capture" or "Clips" && view is FrameworkElement element)element.SetBinding(dashboard&&name=="Capture"?HeightProperty:MinHeightProperty,new System.Windows.Data.Binding("ViewportHeight"){Source=scroll});
        Grid.SetRow(scroll,1);page.Children.Add(scroll);return page;
    }
    private void ShowTab(string name){Width=name=="Settings"?Math.Min(780,SystemParameters.WorkArea.Width):430;tab=name;overlayBody.Content=Page(false,name);UpdateStates();}
    private void ShowHomeTab(string name){if(name!=homeTab)foreach(var player in homeWorkspace.Children.OfType<ClipEditor>().ToArray())player.CloseForHide();homeTab=name;foreach(var p in homeTabs){p.Value.Background=p.Key==name?Brush("#1B2026"):Brushes.Transparent;p.Value.Foreground=p.Key==name?ThemePalette.Foreground:Muted;}homeBody.Content=Page(true,name);UpdateStates();}
    public void ShowHome()
    {
        if(overlayWanted||IsVisible)HidePanel();
        if(home is null){
            home=new Window{Title="FLICK Home",WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,Width=Math.Min(1160,SystemParameters.WorkArea.Width),Height=Math.Min(790,SystemParameters.WorkArea.Height),WindowStartupLocation=WindowStartupLocation.CenterScreen,Background=Brush("#07080A"),Foreground=Foreground,FontFamily=FontFamily,FontSize=12,FontWeight=FontWeights.Medium,Icon=Icon};
            home.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/FLICK;component/Theme.xaml",UriKind.Relative)});
            System.Windows.Shell.WindowChrome.SetWindowChrome(home,new System.Windows.Shell.WindowChrome{CaptionHeight=0,ResizeBorderThickness=new Thickness(0),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(0)});
            homeRoot=new Grid{Background=Brush("#07080A")};var layout=new Grid();layout.RowDefinitions.Add(new(){Height=GridLength.Auto});layout.RowDefinitions.Add(new());layout.RowDefinitions.Add(new(){Height=GridLength.Auto});layout.Children.Add(Header(true));var footer=new DockPanel();var feedback=FeedbackButton();DockPanel.SetDock(feedback,Dock.Right);footer.Children.Add(feedback);footer.Children.Add(UpdateButton());Grid.SetRow(footer,2);layout.Children.Add(footer);homeWorkspace.Margin=new Thickness(28,8,28,20);homeWorkspace.Children.Add(homeBody);Grid.SetRow(homeWorkspace,1);layout.Children.Add(homeWorkspace);homeRoot.Children.Add(layout);RoundClip(homeRoot);home.Background=Brush("#07080A");home.SourceInitialized+=(_,_)=>OverlayWindow.Round(home);home.SizeChanged+=(_,_)=>OverlayWindow.Round(home);home.Content=new Border{Background=Brush("#07080A"),CornerRadius=new CornerRadius(12),Child=homeRoot};home.StateChanged+=(_,_)=>{if(home.WindowState==WindowState.Minimized){ReleaseEditors();ObserveUi(false);}};
            home.IsVisibleChanged+=(_,_)=>{if(!home.IsVisible){ReleaseEditors();if(!IsVisible)ObserveUi(false);}};
            home.Closing+=(_,e)=>{if(!quitting){e.Cancel=true;home.Hide();}};
        }
        ObserveUi(true);_=engine.RefreshGalleryAsync();ShowHomeTab(homeTab);home.Show();OverlayWindow.RestoreHome(home);if(!inspection)autoTimer.Start();
    }
    private UIElement CaptureView(bool dashboard)
    {
        var root=new Grid();root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new());
        var headingArea=new StackPanel{Margin=new Thickness(0,0,0,8)};root.Children.Add(headingArea);
        var targetRow=new DockPanel{Margin=new Thickness(0,6,0,0)};var badge=Text("● IDLE",10);badge.Foreground=Muted;badge.FontWeight=FontWeights.Bold;DockPanel.SetDock(badge,Dock.Right);targetRow.Children.Add(badge);
        var context=Text("",13);context.Foreground=Muted;targetRow.Children.Add(context);if(!dashboard)headingArea.Children.Add(targetRow);
        stateUpdates[dashboard?"home-target":"overlay-target"]=()=>{badge.Text=engine.Active?"● LIVE":"● IDLE";badge.Foreground=engine.Active?Lime:Muted;badge.Effect=null;};
        var columns=new Grid();columns.ColumnDefinitions.Add(new());if(dashboard)columns.ColumnDefinitions.Add(new(){Width=new GridLength(350)});
        var controls=new Grid{Margin=new Thickness(dashboard?24:4,0,4,8)};
        if(!dashboard)controls.RowDefinitions.Add(new(){Height=GridLength.Auto});
        controls.RowDefinitions.Add(new(){Height=new GridLength(1.3,GridUnitType.Star),MinHeight=155});
        controls.RowDefinitions.Add(new(){Height=new GridLength(1.6,GridUnitType.Star),MinHeight=215});
        var qualityGroup=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,12,0,12)};
        var timeGroup=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,14,0,14)};
        var audioGroup=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,14,0,14)};
        if(!dashboard){qualityGroup.Children.Add(CreateQualityControls());controls.Children.Add(qualityGroup);}Grid.SetRow(timeGroup,dashboard?0:1);controls.Children.Add(timeGroup);Grid.SetRow(audioGroup,dashboard?1:2);controls.Children.Add(audioGroup);
        timeGroup.Children.Add(Text("CLIP THE LAST",11));var time=Text(Duration(preferences.Seconds),20);time.Foreground=Lime;timeGroup.Children.Add(time);
        var slider=new ReplaySlider{Seconds=preferences.Seconds};var presets=new System.Windows.Controls.Primitives.UniformGrid{Columns=5};var choices=new Dictionary<int,Button>();
        foreach(int seconds in new[]{30,60,120,180,300}){int n=seconds;var b=Button(Duration(n),()=>slider.Seconds=n);b.Padding=new Thickness(5,8,5,8);choices[n]=b;presets.Children.Add(b);}
        bool LengthLocked()=>armed||changing||engine.Active;
        void PaintTime(){time.Text=Duration(preferences.Seconds);foreach(var pair in choices){bool selected=pair.Key==preferences.Seconds;pair.Value.BorderBrush=selected?Lime:Brush("#29313B");pair.Value.Background=ThemePalette.Secondary;pair.Value.Foreground=selected?Lime:ThemePalette.Foreground;pair.Value.Effect=null;}}
        void SetTime(int value){if(LengthLocked()){if(slider.Seconds!=preferences.Seconds)slider.Seconds=preferences.Seconds;return;}value=MemoryPolicy.ClampSeconds(value,preferences.Profile,MemoryPolicy.Available());if(slider.Seconds!=value){slider.Seconds=value;return;}if(value!=preferences.Seconds){engine.SetHistory(value);preferences.Seconds=value;SavePreferences();}PaintTime();}
        slider.Changed+=SetTime;PaintTime();timeGroup.Children.Add(slider);timeGroup.Children.Add(presets);
        var lengthNotice=Text("Pause replay to change",11);timeGroup.Children.Add(lengthNotice);
        void UpdateLength(){bool locked=LengthLocked();slider.IsEnabled=!locked;presets.IsEnabled=!locked;slider.Opacity=presets.Opacity=locked?.5:1;lengthNotice.Visibility=locked?Visibility.Visible:Visibility.Collapsed;}
        stateUpdates[dashboard?"home-duration":"overlay-duration"]=UpdateLength;UpdateLength();
        audioGroup.Children.Add(Text("AUDIO SOURCES",11));audioGroup.Children.Add(AudioToggle("Desktop audio",true,dashboard));audioGroup.Children.Add(AudioToggle("Microphone",false,dashboard));
        audioGroup.Children.Add(Text("Input device",11));var input=new MicrophonePicker(()=>preferences.InputId,async id=>{preferences.InputId=id;SavePreferences();await RestartAsync();},!inspection);audioGroup.Children.Add(input);
        if(dashboard)Grid.SetColumn(controls,1);columns.Children.Add(controls);
        if(dashboard){
            var summary=new Grid{Margin=new Thickness(0,4,20,0)};summary.RowDefinitions.Add(new());summary.RowDefinitions.Add(new(){Height=GridLength.Auto});summary.RowDefinitions.Add(new(){Height=GridLength.Auto});
            summary.Children.Add(new LiveCommandCanvas(engine,MuteLane,CreateQualityControls()));
            var actions=new Grid();actions.ColumnDefinitions.Add(new());actions.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var save=SaveButton();save.Height=42;actions.Children.Add(save);var pause=Button(armed?"Pause replay":"Resume replay",()=>_=ToggleReplayAsync());Grid.SetColumn(pause,1);actions.Children.Add(pause);Grid.SetRow(actions,1);summary.Children.Add(actions);
            var recent=new StackPanel{Margin=new Thickness(0,12,0,0)};recent.Children.Add(Text("Recent clips",16));recent.Children.Add(ClipsView(true,3));Grid.SetRow(recent,2);summary.Children.Add(recent);columns.Children.Add(summary);
        }
        Grid.SetRow(columns,1);root.Children.Add(columns);return root;
    }
    private async void MuteLane(string id,bool muted){try{if(id is "desktop" or "mic"){if(id=="desktop")preferences.Desktop=!muted;else preferences.Mic=!muted;SavePreferences();UpdateStates();await Task.Run(()=>engine.SetAudio(preferences.Desktop,preferences.Mic));}else engine.MuteAudioLane(id,muted);}catch(Exception e){NotificationRequested?.Invoke(e.Message);}}
    private sealed record AudioDevice(string? Id,string Name){public override string ToString()=>Name;}
    private UIElement AudioToggle(string name,bool desktop,bool dashboard)
    {
        bool Current()=>desktop?preferences.Desktop:preferences.Mic;
        var state=Text(Current()?"Enabled":"Muted",10);state.Foreground=Muted;
        var row=new DockPanel{Margin=new Thickness(0,8,0,6)};var toggle=new CheckBox{IsChecked=Current(),VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(toggle,Dock.Right);row.Children.Add(toggle);
        var icon=Button("",()=>{toggle.IsChecked=!toggle.IsChecked;Apply();});icon.Width=36;icon.Height=36;icon.Padding=new Thickness(9);
        icon.Content=new System.Windows.Shapes.Path{Data=Geometry.Parse(desktop?"M2,8 L6,8 L11,3 L11,21 L6,16 L2,16 Z M15,7 C20,10 20,14 15,17":"M8,5 C8,0 16,0 16,5 L16,12 C16,18 8,18 8,12 Z M4,11 L4,13 C4,23 20,23 20,13 L20,11 M12,22 L12,26"),Stroke=Lime,StrokeThickness=1.6,Stretch=Stretch.Uniform};
        row.Children.Add(icon);var text=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(7,0,0,0)};text.Children.Add(Text(name));text.Children.Add(state);row.Children.Add(text);
        async void Apply(){if(desktop)preferences.Desktop=toggle.IsChecked==true;else preferences.Mic=toggle.IsChecked==true;SavePreferences();state.Text=Current()?"Capturing":"Muted";try{await Task.Run(()=>engine.SetAudio(preferences.Desktop,preferences.Mic));}catch(Exception e){state.Text="Unavailable";Log(e.Message);}}
        stateUpdates[(dashboard?"home-":"overlay-")+(desktop?"desktop":"microphone")]=()=>{toggle.IsChecked=Current();state.Text=Current()?"Enabled":"Muted";};toggle.Click+=(_,_)=>Apply();return row;
    }
    private static ImageSource LoadImage(string path){var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(path);image.EndInit();image.Freeze();return image;}
    private void OpenEditor(string path,bool trimming=true,bool dashboard=true){
        foreach(var owner in new[]{homeWorkspace,overlayRoot})foreach(var previous in owner.Children.OfType<ClipEditor>().ToArray()){previous.CloseForHide();owner.Children.Remove(previous);}
        var host=dashboard&&home?.IsVisible==true&&home.WindowState!=WindowState.Minimized?homeWorkspace:overlayRoot;
        ClipEditor? editor=null;editor=new ClipEditor(path,engine,()=>{if(host.Children.Contains(editor!))host.Children.Remove(editor!);},trimming,_=>{if(dashboard)ShowHomeTab("Clips");else ShowTab("Clips");});Panel.SetZIndex(editor,100);host.Children.Add(editor);
    }
    private UIElement ClipsView(bool dashboard,int limit=60)
    {
        var root=new StackPanel();
        var tiles=new System.Windows.Controls.Primitives.UniformGrid{Columns=dashboard?3:1};
        foreach(var entry in engine.Gallery.Take(limit)){
            string path=entry.Path;
            var tile=new StackPanel();
            if(entry.Thumbnail is not null)tile.Children.Add(new ClipThumbnail(entry.Thumbnail,limit==3?76:dashboard?140:150,()=>OpenEditor(path,false,dashboard)));
            else {tile.Children.Add(new Border{Height=limit==3?76:140,Background=Brush("#07080A"),Child=Text("Processing preview",11)});engine.RequestPreview(path);}
            var title=Text(Path.GetFileNameWithoutExtension(path),11);title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;tile.Children.Add(title);tile.Children.Add(new Border{Height=1,Background=ThemePalette.Divider,Margin=new Thickness(0,6,0,4)});
            var tools=new DockPanel();var buttons=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(buttons,Dock.Right);tools.Children.Add(buttons);
            buttons.Children.Add(IconButton("M2,14 L3,10 L12,1 L16,5 L7,14 Z M10,3 L14,7","Trim clip",Lime,()=>OpenEditor(path,true,dashboard)));
            buttons.Children.Add(IconButton("M4,4 C7,2 15,2 18,4 L21,16 L16,19 L14,16 M8,16 L6,19 L1,16 Z M7,10 A1,1 0 1 0 9,10 A1,1 0 1 0 7,10 M13,10 A1,1 0 1 0 15,10 A1,1 0 1 0 13,10","Compress for Discord",Muted,()=>ShowCompression(path,false,dashboard)));
            var trash=IconButton("M2,4 L16,4 M6,4 L6,1 L12,1 L12,4 M4,5 L5,17 L13,17 L14,5 M7,7 L7,14 M11,7 L11,14","Delete clip",Brush("#F26972"),()=>{});buttons.Children.Add(trash);
            var date=Text(entry.Created.ToString(limit==3?"dd MMM":"dd MMM yyyy  HH:mm"),10);date.Foreground=Muted;date.VerticalAlignment=VerticalAlignment.Center;tools.Children.Add(date);tile.Children.Add(tools);var card=Card(tile);tiles.Children.Add(card);
            trash.Click+=async(_,_)=>{card.IsEnabled=false;card.Opacity=0.35;try{await ClipFiles.DeleteAsync(path);tiles.Children.Remove(card);await engine.RefreshGalleryAsync();}catch(Exception e){card.IsEnabled=true;card.Opacity=1;date.Text="Delete failed";date.ToolTip=e.Message;}};
        }
        if(tiles.Children.Count==0)root.Children.Add(Text("Your saved clips will appear here. Loading cached previews…",12));else root.Children.Add(tiles);
        if(limit<=3)return root;
        var page=new Grid();page.RowDefinitions.Add(new(){Height=GridLength.Auto});page.RowDefinitions.Add(new());page.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var gallery=new SmoothScrollViewer{Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(gallery,1);page.Children.Add(gallery);
        var footer=new StackPanel{Margin=new Thickness(0,14,0,0)};footer.Children.Add(new Border{Height=1,Background=ThemePalette.Divider,Margin=new Thickness(0,0,0,12)});var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center};actions.Children.Add(Button("Open Clips Folder",()=>OpenFolder()));var transcode=Button("⚡ Compress for Discord",ImportExternalVideo);transcode.SetResourceReference(StyleProperty,"DiscordButton");actions.Children.Add(transcode);footer.Children.Add(actions);Grid.SetRow(footer,2);page.Children.Add(footer);return page;
    }
    private UIElement SettingsView()
    {
        var root=new Grid{Margin=new Thickness(0,6,0,0)};root.ColumnDefinitions.Add(new());root.ColumnDefinitions.Add(new());var left=new StackPanel{Margin=new Thickness(0,0,26,0)};var right=new StackPanel{Margin=new Thickness(14,0,0,0)};root.Children.Add(left);Grid.SetColumn(right,1);root.Children.Add(right);
        void Toggle(Panel host,string label,bool initial,Action<bool> change){var toggle=new CheckBox{IsChecked=initial};var row=new DockPanel{Margin=new Thickness(0,8,0,14)};DockPanel.SetDock(toggle,Dock.Right);row.Children.Add(toggle);row.Children.Add(Text(label));toggle.Click+=(_,_)=>{change(toggle.IsChecked==true);SavePreferences();ViewSettingsChanged?.Invoke();};host.Children.Add(row);}
        Toggle(left,"Launch on startup",preferences.LaunchOnLogin,value=>{preferences.LaunchOnLogin=value;if(!inspection)try{preferences.ApplyStartup();}catch(Exception e){Log(e.Message);}});
        left.Children.Add(Text("Storage Location",11));var storage=new DockPanel{Margin=new Thickness(0,2,0,16)};var path=Text(StorageLocations.Current,11);path.TextWrapping=TextWrapping.NoWrap;path.TextTrimming=TextTrimming.CharacterEllipsis;path.ToolTip=StorageLocations.Current;path.VerticalAlignment=VerticalAlignment.Center;var browse=Button("Browse",()=>{});DockPanel.SetDock(browse,Dock.Right);storage.Children.Add(browse);storage.Children.Add(new Border{Padding=new Thickness(8),Background=ThemePalette.Secondary,CornerRadius=new CornerRadius(6),Child=path});left.Children.Add(storage);
        browse.Click+=async(_,_)=>{using var picker=new System.Windows.Forms.FolderBrowserDialog{InitialDirectory=StorageLocations.Current,SelectedPath=StorageLocations.Current,UseDescriptionForTitle=true,Description="Choose a local FLICK clips folder"};if(picker.ShowDialog(new DialogOwner(new WindowInteropHelper(Window.GetWindow(browse)).Handle))!=System.Windows.Forms.DialogResult.OK)return;browse.IsEnabled=false;try{await Task.Run(()=>StorageLocations.Set(picker.SelectedPath));preferences.StoragePath=StorageLocations.Current;path.Text=StorageLocations.Current;path.ToolTip=path.Text;SavePreferences();await engine.RefreshGalleryAsync();}catch(Exception e){NotificationRequested?.Invoke(e.Message);}finally{browse.IsEnabled=true;}};
        Toggle(left,"Show replay & microphone HUD",preferences.HudEnabled,value=>preferences.HudEnabled=value);
        left.Children.Add(Text("Accent color (#RRGGBB)",11));var hex=new TextBox{Text=preferences.AccentHex,MaxLength=7,MinHeight=35};hex.TextChanged+=(_,_)=>{string color=hex.Text.Trim();if(color.Length==0)color=ThemePalette.DefaultAccent;if(!ThemePalette.ValidHex(color)){hex.ToolTip="Use # followed by six hexadecimal digits.";return;}preferences.AccentHex=color;ThemePalette.Apply(color,preferences.ThemeVariant);SavePreferences();};left.Children.Add(hex);
        left.Children.Add(Text("Theme",11));var theme=new ComboBox{ItemsSource=new[]{"System Default","Pure Dark Onyx","Light Mode"},SelectedItem=preferences.ThemeVariant};theme.SelectionChanged+=(_,_)=>{preferences.ThemeVariant=(string)theme.SelectedItem;ThemePalette.Apply(preferences.AccentHex,preferences.ThemeVariant);SavePreferences();};left.Children.Add(theme);
        var audio=Button("Audio lanes · "+preferences.AudioSources.Count+" additional",()=>{var selector=new AudioSourceSelector(preferences.AudioSources,async selected=>{await Task.Run(()=>engine.SetAudioSources(selected));preferences.AudioSources=selected.ToList();SavePreferences();ViewSettingsChanged?.Invoke();},!inspection){Owner=Window.GetWindow(root)};selector.ShowDialog();});audio.Margin=new Thickness(0,18,0,6);left.Children.Add(audio);var screenshotHint=Text(ScreenshotShortcut+" saves a PNG of the primary monitor.",11);left.Children.Add(screenshotHint);stateUpdates["screenshot-hint"]=()=>screenshotHint.Text=ScreenshotShortcut+" saves a PNG of the primary monitor.";
        right.Children.Add(Text("Open overlay shortcut",11));right.Children.Add(ShortcutInput(0));right.Children.Add(Text("FLICK It shortcut",11));right.Children.Add(ShortcutInput(1));right.Children.Add(Text("Screenshot shortcut",11));right.Children.Add(ShortcutInput(2));right.Children.Add(Text("HUD Indicator Position",11));var corner=new ComboBox{ItemsSource=new[]{"Top-Right","Top-Left","Bottom-Right","Bottom-Left"},SelectedItem=preferences.HudPosition};corner.SelectionChanged+=(_,_)=>{preferences.HudPosition=(string)corner.SelectedItem;SavePreferences();ViewSettingsChanged?.Invoke();};right.Children.Add(corner);
        return root;
    }
    private QualityControls CreateQualityControls(){qualityControls.RemoveAll(w=>!w.TryGetTarget(out _));var controls=new QualityControls(preferences,ApplyQualityAsync);qualityControls.Add(new(controls));return controls;}
    private async Task ApplyQualityAsync(){try{preferences.Seconds=MemoryPolicy.ClampSeconds(preferences.Seconds,preferences.Profile,MemoryPolicy.Available());SavePreferences();UpdateStates();await RestartAsync();}catch(Exception e){NotificationRequested?.Invoke(e.Message);}}
    private sealed class DialogOwner(IntPtr handle):System.Windows.Forms.IWin32Window{public IntPtr Handle=>handle;}
    private Button FeedbackButton(){var link=Button("Report Bug / Give Feedback",()=>{try{FeedbackLink.Open();}catch(Exception e){NotificationRequested?.Invoke(e.Message);}});link.Foreground=Muted;link.Background=Brushes.Transparent;link.BorderThickness=new Thickness(0);link.FontSize=10;link.Padding=new Thickness(8,6,8,6);link.Margin=new Thickness(0,0,22,8);link.HorizontalAlignment=HorizontalAlignment.Right;return link;}
    private Button UpdateButton(){var button=Button("",()=>_=CheckForUpdatesAsync(true));button.Background=Brushes.Transparent;button.BorderThickness=new Thickness(0);button.Padding=new Thickness(8,6,8,6);button.Margin=new Thickness(20,0,0,8);button.HorizontalAlignment=HorizontalAlignment.Left;button.FontSize=11;updateButtons.Add(new(button));RefreshUpdateButtons();return button;}
    private void RefreshUpdateButtons(){for(int i=updateButtons.Count-1;i>=0;i--){if(!updateButtons[i].TryGetTarget(out var button)){updateButtons.RemoveAt(i);continue;}var row=new StackPanel{Orientation=Orientation.Horizontal};row.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M12,3 A6,6 0 1 0 14,10 M12,0 L12,4 L8,4"),Stroke=ThemePalette.Accent,StrokeThickness=1.5,Width=15,Height=15,Stretch=Stretch.Uniform,Margin=new Thickness(0,0,7,0)});row.Children.Add(Text(updateBusy?"Checking for updates…":updateResult?.State==UpdateState.Available?"Update available":"Check for updates",11));button.Content=row;button.IsEnabled=!updateBusy;button.ToolTip=updateResult?.Message??"Checks GitHub Releases. No automatic downloads or installations.";}}
    public async Task CheckForUpdatesAsync(bool manual=false){
        if(inspection||quitting||updateBusy)return;updateBusy=true;RefreshUpdateButtons();
        try{var result=await UpdateChecker.CheckAsync(updateLifetime.Token);if(quitting)return;updateResult=result;
            if(manual){if(result.State==UpdateState.Available){if(MessageBox.Show(result.Message+" Open the GitHub release page?","FLICK updates",MessageBoxButton.YesNo,MessageBoxImage.Information)==MessageBoxResult.Yes)OpenUpdatePage(result);}else MessageBox.Show(result.Message,"FLICK updates",MessageBoxButton.OK,MessageBoxImage.Information);}
            else if(result.State==UpdateState.Available)UpdateAvailable?.Invoke(result);
        }finally{updateBusy=false;if(!quitting)RefreshUpdateButtons();}
    }
    internal static void OpenUpdatePage(UpdateResult result){if(result.ReleaseUrl is {} url)Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}
    private void ImportExternalVideo(){var picker=new Microsoft.Win32.OpenFileDialog{Title="Compress External Video",Filter="Video files|*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.mpg;*.mpeg;*.ts;*.flv;*.ogv|All files|*.*",CheckFileExists=true,Multiselect=false};Window owner=home?.IsVisible==true?home:this;if(picker.ShowDialog(owner)==true)try{string path=ReadOnlyVideo.LocalFile(picker.FileName);ShowCompression(path,true,owner==home);}catch(Exception e){NotificationRequested?.Invoke(e.Message);}}
    private void ShowCompression(string path,bool external,bool dashboard){var host=dashboard&&home?.IsVisible==true?homeWorkspace:overlayRoot;if(host.Children.OfType<CompressionPrompt>().Any())return;CompressionPrompt? prompt=null;prompt=new CompressionPrompt(path,external,engine,()=>host.Children.Remove(prompt!),output=>NotificationRequested?.Invoke("Compressed copy saved: "+Path.GetFileName(output)));Panel.SetZIndex(prompt,200);host.Children.Add(prompt);}
    private TextBox ShortcutInput(int kind){
        var box=new ShortcutField(()=>kind==0?PanelShortcut:kind==1?SaveShortcut:ScreenshotShortcut,async value=>{
            string oldPanel=PanelShortcut,oldSave=SaveShortcut,oldScreenshot=ScreenshotShortcut;
            string newPanel=kind==0?value:oldPanel,newSave=kind==1?value:oldSave,newScreenshot=kind==2?value:oldScreenshot;
            if(new[]{Shortcut.Parse(newPanel),Shortcut.Parse(newSave),Shortcut.Parse(newScreenshot)}.Distinct().Count()!=3)throw new InvalidOperationException("Shortcuts must be different.");
            if(ShortcutApplyRequested?.Invoke(newPanel,newSave,newScreenshot)==false){ShortcutApplyRequested?.Invoke(oldPanel,oldSave,oldScreenshot);return false;}
            preferences.PanelShortcut=newPanel;preferences.SaveShortcut=newSave;preferences.ScreenshotShortcut=newScreenshot;
            try{if(!inspection)await Task.Run(preferences.Save);ShortcutsChanged?.Invoke(PanelShortcut,SaveShortcut);UpdateStates();return true;}
            catch{preferences.PanelShortcut=oldPanel;preferences.SaveShortcut=oldSave;preferences.ScreenshotShortcut=oldScreenshot;ShortcutApplyRequested?.Invoke(oldPanel,oldSave,oldScreenshot);throw;}
        },(field,active)=>{
            if(active){if(listeningField!=field)listeningField?.CancelCapture();listeningField=field;ShortcutCaptureStateChanged?.Invoke(true);if(Window.GetWindow(field)==this){OverlayWindow.SetMouseOnly(this,false);Activate();}}
            else if(listeningField==field){listeningField=null;ShortcutCaptureStateChanged?.Invoke(false);OverlayWindow.SetMouseOnly(this,true);}
        });shortcutFields.Add(new(box));return box;
    }
    internal async Task SaveScreenshotAsync(){try{string path=await ScreenshotCapture.SaveAsync();NotificationRequested?.Invoke("Screenshot saved: "+Path.GetFileName(path));}catch(Exception e){NotificationRequested?.Invoke(e.Message);}}
    private void SavePreferences(){if(!inspection){preferenceTimer.Stop();preferenceTimer.Start();}}
    private static string Duration(int seconds)=>seconds<60?$"{seconds}s":seconds%60==0?$"{seconds/60}m":$"{seconds/60}m {seconds%60}s";
    private bool restartPending,restartLoop;
    private async Task RestartAsync(){if(inspection||quitting)return;restartPending=true;if(changing||restartLoop)return;restartLoop=true;try{while(restartPending&&!quitting){restartPending=false;changing=true;try{await engine.FinishPendingSaveAsync();await engine.StopAsync();}finally{changing=false;}await EnsureReplayAsync();}}finally{restartLoop=false;}}
    private async Task ToggleReplayAsync(){if(changing)return;armed=!armed;if(armed)await EnsureReplayAsync();else{changing=true;UpdateStates();try{await engine.FinishPendingSaveAsync();await engine.StopAsync();}catch(Exception e){armed=engine.Active;NotificationRequested?.Invoke(e.Message);}finally{changing=false;UpdateStates();}}ShowTab(tab);if(home?.IsVisible==true)ShowHomeTab(homeTab);}
    internal bool CanSaveReplay=>armed&&!changing&&engine.Active&&!quitting;
    private ReplaySaveButton SaveButton(){saveButtons.RemoveAll(w=>!w.TryGetTarget(out _));var button=new ReplaySaveButton(()=>_=SaveClipAsync());button.SetAvailable(CanSaveReplay);button.SetState(saveState);saveButtons.Add(new(button));return button;}
    private void UpdateSaveButtons(){for(int i=saveButtons.Count-1;i>=0;i--)if(saveButtons[i].TryGetTarget(out var button)){button.SetAvailable(CanSaveReplay);button.SetState(saveState);}else saveButtons.RemoveAt(i);}
    public async Task SaveClipAsync(){
        if(!CanSaveReplay){NotificationRequested?.Invoke(!armed?"Resume replay before saving a clip.":"Replay is not ready. Please try again shortly.");return;}
        if(!saveAdmission.TryEnter())return;Guid request=saveRequest=Guid.NewGuid();saveState=1;UpdateSaveButtons();bool saved=false;
        try{if(changing)throw new InvalidOperationException("Capture is recovering. Please try again shortly.");string file=await Task.Run(()=>engine.SaveAsync(preferences.Seconds,request));saved=true;saveState=2;if(!quitting){SavedClipNotification?.Invoke(file);NotificationRequested?.Invoke("Replay saved: "+Path.GetFileName(file));}}
        catch(Exception e){saveState=0;NotificationRequested?.Invoke(e.Message);}finally{saveAdmission.Exit();UpdateSaveButtons();}
        if(saved){await Task.Delay(1200);if(!quitting&&saveRequest==request){saveState=0;UpdateSaveButtons();}}
    }
    public async Task OpenSavedClipAsync(string path){if(!await Task.Run(()=>File.Exists(path))){NotificationRequested?.Invoke("This clip is no longer available.");return;}ShowHome();ShowHomeTab("Clips");OpenEditor(path,false,true);}
    private void ObserveUi(bool enabled){
        if(enabled==uiObserved)return;uiObserved=enabled;
        if(enabled){engine.Changed+=uiEngineChanged;engine.ClipsChanged+=uiClipsChanged;}else{engine.Changed-=uiEngineChanged;engine.ClipsChanged-=uiClipsChanged;}
    }
    private void ReleaseEditors(){foreach(var host in new[]{homeWorkspace,overlayRoot})foreach(var prompt in host.Children.OfType<CompressionPrompt>().ToArray())host.Children.Remove(prompt);foreach(var owner in new[]{homeWorkspace,overlayRoot})foreach(var editor in owner.Children.OfType<ClipEditor>().ToArray()){editor.CloseForHide();owner.Children.Remove(editor);}listeningField?.CancelCapture();}
    private void UpdateStates(){for(int i=qualityControls.Count-1;i>=0;i--)if(qualityControls[i].TryGetTarget(out var control))control.Refresh();else qualityControls.RemoveAt(i);foreach(var update in stateUpdates.Values)update();UpdateSaveButtons();for(int i=shortcutFields.Count-1;i>=0;i--)if(shortcutFields[i].TryGetTarget(out var field))field.RefreshText();else shortcutFields.RemoveAt(i);}
    public void ToggleOverlay(){if(overlayWanted)HidePanel();else ShowOverlay();}
    private void CancelPresentationReady(){if(presentationReady is not null){CompositionTarget.Rendering-=presentationReady;presentationReady=null;}}
    private void WarmOverlayLayout(){
        if(quitting)return;
        new WindowInteropHelper(this).EnsureHandle();var bounds=OverlayWindow.ScreenBounds(this);
        Left=bounds.Left;Top=bounds.Top;Height=bounds.Height;
        overlayRoot.Measure(new Size(Width,Height));overlayRoot.Arrange(new Rect(0,0,Width,Height));overlayRoot.UpdateLayout();
    }
    public void ShowOverlay(bool keyboardInput=false){
        overlayWanted=true;overlayForeground=GetForegroundWindow();int version=++transition;
        CancelPresentationReady();presentation?.Remove(this);slide.X=-Width;overlaySurface.Opacity=0;
        // Retain the existing controls/device list; no tree rebuild on the hotkey path.
        ObserveUi(true);_=engine.RefreshGalleryAsync();UpdateStates();WarmOverlayLayout();Show();OverlayWindow.ShowWithoutActivation(this);
        wasDown=(GetAsyncKeyState(1)&0x8000)!=0;dismissTimer.Start();if(!inspection)autoTimer.Start();
        // Submit a transparent staging frame before starting the single presentation clock.
        int staged=0;TimeSpan previous=TimeSpan.MinValue;
        presentationReady=(_,e)=>{
            if(e is RenderingEventArgs rendering){if(rendering.RenderingTime==previous)return;previous=rendering.RenderingTime;}
            if(version!=transition||!overlayWanted){CancelPresentationReady();return;}
            if(staged++==0)return;CancelPresentationReady();AnimatePresentation(-Width,0,0,1,190,null);
        };
        CompositionTarget.Rendering+=presentationReady;
    }
    private void AnimatePresentation(double x0,double x1,double a0,double a1,int milliseconds,Action? completed){
        var board=new Storyboard();var duration=TimeSpan.FromMilliseconds(milliseconds);
        Timeline.SetDesiredFrameRate(board,60);
        var move=new DoubleAnimation(x0,x1,duration){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};Storyboard.SetTarget(move,overlaySurface);Storyboard.SetTargetProperty(move,new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));board.Children.Add(move);
        var alpha=new DoubleAnimation(a0,a1,duration){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};Storyboard.SetTarget(alpha,overlaySurface);Storyboard.SetTargetProperty(alpha,new PropertyPath(OpacityProperty));board.Children.Add(alpha);
        if(completed is not null)board.Completed+=(_,_)=>completed();presentation=board;board.Begin(this,true);
    }
    private void HidePanel(){
        overlayWanted=false;dismissTimer.Stop();if(preferenceTimer.IsEnabled){preferenceTimer.Stop();if(!inspection)_=Task.Run(()=>{try{preferences.Save();}catch(Exception e){Log(e.Message);}});}CancelPresentationReady();ReleaseEditors();if(home?.IsVisible!=true)ObserveUi(false);if(!IsVisible)return;
        int version=++transition;double x=slide.X,alpha=overlaySurface.Opacity;presentation?.Remove(this);
        AnimatePresentation(x,-Width,alpha,0,140,()=>{if(version!=transition)return;Hide();overlaySurface.Opacity=0;});
    }
    private IntPtr Hook(IntPtr h,int message,IntPtr w,IntPtr l,ref bool handled){if(message==0x21){handled=true;return new IntPtr(3);}return IntPtr.Zero;}
    public void SetHotkeyState(bool panel,bool save,bool screenshot){hotkeyWarning=!panel?"Overlay shortcut is in use":!save?"FLICK It shortcut is in use":!screenshot?"Screenshot shortcut is in use":"";UpdateStates();}
    public async Task StopBeforeExitAsync(){quitting=true;armed=false;autoTimer.Stop();listeningField?.CancelCapture();if(startupTask is not null)try{await startupTask.WaitAsync(TimeSpan.FromSeconds(20));}catch(Exception e){Log(e.Message);}await engine.StopAsync();await engine.FinishPendingSaveAsync();quitting=true;home?.Close();}
    public void Release(){updateLifetime.Cancel();preferenceTimer.Stop();dismissTimer.Stop();autoTimer.Stop();ReleaseEditors();ObserveUi(false);CancelPresentationReady();presentation?.Remove(this);quitting=true;home?.Close();}
    public static void OpenFolder(string? file=null){Directory.CreateDirectory(ReplayEngine.SaveFolder);var p=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};p.ArgumentList.Add(file is null?ReplayEngine.SaveFolder:Path.GetDirectoryName(file)!);Process.Start(p);}
    private static void Log(string message){Directory.CreateDirectory(ReplayEngine.DataFolder);File.AppendAllText(Path.Combine(ReplayEngine.DataFolder,"ui.log"),DateTimeOffset.Now.ToString("O")+" "+message+Environment.NewLine);}
    internal void ShowPreview(){ShowHome();}
}
