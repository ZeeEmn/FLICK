using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms=System.Windows.Forms;

namespace Rewind;

internal sealed class App : System.Windows.Application
{
    private Forms.NotifyIcon? tray;
    private HudWindow? hud;
    private bool screenshotKey;
    private void UpdateHud(){if(hud is not null&&shell is not null&&engine is not null){var settings=shell.Preferences;hud.Update(settings.HudEnabled,settings.HudPosition,engine.Active,engine.MicrophoneLive);}}
    private void AudioStateChanged()=>Dispatcher.BeginInvoke(UpdateHud);
    private void ThemeSystemChanged(object sender,Microsoft.Win32.UserPreferenceChangedEventArgs args)=>Dispatcher.BeginInvoke(ThemePalette.SystemChanged);
    private Shell? shell;
    private ReplayEngine? engine;
    private Mutex? instance;
    private HwndSource? messages;
    private bool panelKey,saveKey;
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    [STAThread]
    public static int Main(string[] args)
    {
        try{SecurityBootstrap.Initialize();var app=new App();app.Run();return 0;}catch(Exception e){MessageBox.Show(e.Message,"FLICK could not start");return 1;}
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var launchArgs=Environment.GetCommandLineArgs().Skip(1).ToArray();
        bool inspection=false;
        instance=new Mutex(true,inspection?"Local\\FLICK.InterfaceInspection":"Local\\REWIND.Development",out bool created);
        if(!created){MessageBox.Show("FLICK is already running. Exit the older tray instance before launching this version.","FLICK");Shutdown();return;}
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        engine=new ReplayEngine();
        shell=new Shell(engine,()=>Shutdown(),inspection);
        if(!inspection){hud=new HudWindow();shell.ViewSettingsChanged+=UpdateHud;engine.AudioChanged+=AudioStateChanged;UpdateHud();Microsoft.Win32.SystemEvents.UserPreferenceChanged+=ThemeSystemChanged;}
        messages=new HwndSource(new HwndSourceParameters("FLICK hotkeys"){Width=0,Height=0,WindowStyle=0});
        messages.AddHook(MessageHook);
        shell.ShortcutApplyRequested+=(panel,save,screenshot)=>SetShortcuts(panel,save,screenshot);
        shell.ShortcutCaptureStateChanged+=listening=>{if(messages is null)return;if(listening){UnregisterHotKey(messages.Handle,1);UnregisterHotKey(messages.Handle,2);UnregisterHotKey(messages.Handle,3);screenshotKey=false;}else {SetShortcuts(shell.PanelShortcut,shell.SaveShortcut,shell.ScreenshotShortcut);}};
        if(!inspection){SetShortcuts(shell.PanelShortcut,shell.SaveShortcut,shell.ScreenshotShortcut);}
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("Open FLICK Home",null,(_,_)=>shell.ShowHome());
        menu.Items.Add("FLICK It",null,async(_,_)=>await shell.SaveClipAsync());
        menu.Items.Add("Open clips folder",null,(_,_)=>Shell.OpenFolder());
        menu.Items.Add("Exit",null,async(_,_)=>{await shell.StopBeforeExitAsync();Shutdown();});
        tray=new Forms.NotifyIcon{Text="FLICK - replay paused",Icon=new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory,"Assets","flick.ico")),Visible=true,ContextMenuStrip=menu};

        string? notifiedClip=null;UpdateResult? notifiedUpdate=null;shell.SavedClipNotification+=path=>{notifiedUpdate=null;notifiedClip=path;};tray.BalloonTipClicked+=async(_,_)=>{if(notifiedUpdate is {} update)Shell.OpenUpdatePage(update);else if(notifiedClip is {} path)await shell.OpenSavedClipAsync(path);else shell.ShowHome();};
        shell.NotificationRequested+=message=>{notifiedUpdate=null;tray.ShowBalloonTip(5000,"FLICK",message,Forms.ToolTipIcon.Info);};
        shell.UpdateAvailable+=update=>{notifiedClip=null;notifiedUpdate=update;tray.ShowBalloonTip(5000,"FLICK update available",update.Message+" Click to open the release page.",Forms.ToolTipIcon.Info);};
        engine.Changed+=message=>Dispatcher.BeginInvoke(()=>
        {
            UpdateHud();if(tray is not null)tray.Text=engine.Active?"FLICK - recording primary monitor":"FLICK - replay paused";
        });
        _=shell.BeginBackgroundAsync();
        if(!inspection)_=shell.CheckForUpdatesAsync();
        if(!inspection)tray.ShowBalloonTip(3000,"FLICK","Running in the background. Replay is starting.",Forms.ToolTipIcon.Info);
    }
    private bool SetShortcuts(string panel,string save,string screenshot)
    {
        if(messages is null || shell is null)return false;
        var p=ParseShortcut(panel);var s=ParseShortcut(save);var shot=ParseShortcut(screenshot);
        if(p==s||p==shot||s==shot)return false;
        UnregisterHotKey(messages.Handle,1);UnregisterHotKey(messages.Handle,2);UnregisterHotKey(messages.Handle,3);
        panelKey=RegisterHotKey(messages.Handle,1,p.Modifiers|0x4000,p.Key);
        saveKey=RegisterHotKey(messages.Handle,2,s.Modifiers|0x4000,s.Key);
        screenshotKey=RegisterHotKey(messages.Handle,3,shot.Modifiers|0x4000,shot.Key);
        shell.SetHotkeyState(panelKey,saveKey,screenshotKey);return panelKey&&saveKey&&screenshotKey;
    }
    private static (uint Modifiers,uint Key) ParseShortcut(string value)=>Shortcut.Parse(value);
    private IntPtr MessageHook(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(msg==0x0312 && shell is not null)
        {
            handled=true;
            if(wParam.ToInt32()==1)shell.ToggleOverlay();
            else if(wParam.ToInt32()==2)_=shell.SaveClipAsync();
            else if(wParam.ToInt32()==3)_=shell.SaveScreenshotAsync();
        }
        return IntPtr.Zero;
    }
    private void ShowPanel(){shell?.ShowOverlay();}
    protected override void OnExit(ExitEventArgs e)
    {
        if(engine is not null)engine.AudioChanged-=AudioStateChanged;if(shell is not null)shell.ViewSettingsChanged-=UpdateHud;Microsoft.Win32.SystemEvents.UserPreferenceChanged-=ThemeSystemChanged;hud?.Close();tray?.Dispose();
        if(messages is not null){UnregisterHotKey(messages.Handle,1);UnregisterHotKey(messages.Handle,2);UnregisterHotKey(messages.Handle,3);messages.RemoveHook(MessageHook);messages.Dispose();messages=null;}
        shell?.Release();engine?.Dispose();instance?.Dispose();base.OnExit(e);
    }
}
