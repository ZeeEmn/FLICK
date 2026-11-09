using System.IO;
using System.Text.Json;
using System.Windows.Input;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Rewind;
internal sealed class ProductSettings
{
    public bool HudEnabled {get;set;}=true;
    public string HudPosition {get;set;}="Top-Right";
    public string AccentHex {get;set;}=ThemePalette.DefaultAccent;
    public string ThemeVariant {get;set;}="System Default";
    public string? StoragePath {get;set;}
    public List<AudioSourceSetting> AudioSources {get;set;}=new();
    public int Seconds {get;set;}=60;
    public string Profile {get;set;}="Default";
    public int FrameRate {get;set;}=60;
    public string Codec {get;set;}="h264";
    public bool Desktop {get;set;}=true;
    public bool Mic {get;set;}=true;
    public string? InputId {get;set;}
    public string PanelShortcut {get;set;}="Alt + Z";
    public string SaveShortcut {get;set;}="Alt + F10";
    public bool LaunchOnLogin {get;set;}=true;
    internal void Normalize(){
        Profile=QualityProfiles.Normalize(Profile);FrameRate=FrameRate==30?30:60;
        if(HudPosition is not ("Top-Right" or "Top-Left" or "Bottom-Right" or "Bottom-Left"))HudPosition="Top-Right";
        if(ThemeVariant is not ("System Default" or "Pure Dark Onyx" or "Light Mode"))ThemeVariant="System Default";
        if(!ThemePalette.ValidHex(AccentHex))AccentHex=ThemePalette.DefaultAccent;
        AudioSources=(AudioSources??new()).Where(a=>!string.IsNullOrWhiteSpace(a.Id)&&a.Id.Length<=2048).DistinctBy(a=>a.Id).Take(6).ToList();
        try{if(StoragePath is not null)StoragePath=LocalPaths.Require(StoragePath);}catch{StoragePath=null;}
        Codec="h264"; // Video encoding is automatic; migrate old manual HEVC preferences.
        Seconds=MemoryPolicy.ClampSeconds(Seconds,Profile,MemoryPolicy.Available());
        if(InputId?.Length>2048)InputId=null;
        try{if(Shortcut.Parse(PanelShortcut)==(1u,0x30u))throw new FormatException();Shortcut.Parse(PanelShortcut);if(PanelShortcut.Length>64)throw new FormatException();}catch{PanelShortcut="Alt + Z";}
        try{if(Shortcut.Parse(SaveShortcut)==(1u,0x30u))throw new FormatException();Shortcut.Parse(SaveShortcut);if(SaveShortcut.Length>64||SaveShortcut==PanelShortcut)throw new FormatException();}catch{SaveShortcut="Alt + F10";}
    }
    internal static ProductSettings Load(){
        var value=new ProductSettings();try{string file=Path.Combine(ReplayEngine.DataFolder,"preferences.json");if(!File.Exists(file))file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"REWIND","preferences.json");if(new FileInfo(file).Length<=65536)value=JsonSerializer.Deserialize<ProductSettings>(File.ReadAllText(file),new JsonSerializerOptions{MaxDepth=16})??value;}catch{}
        value.Normalize();value.ApplySetupChoice();return value;
    }

    // A setup choice is consumed once, so opting out is respected on first launch.
    private void ApplySetupChoice(){
        try{
            using var setup=Registry.CurrentUser.OpenSubKey(@"Software\FLICK\Setup",true);
            if(setup?.GetValue("PendingStartup") is not string choice)return;
            LaunchOnLogin=choice=="1";Save();setup.DeleteValue("PendingStartup",false);
        }catch(IOException){}catch(UnauthorizedAccessException){}catch(System.Security.SecurityException){}
    }
    private static readonly object SaveGate=new();
    internal void Save(){lock(SaveGate){Normalize();Directory.CreateDirectory(ReplayEngine.DataFolder);string path=Path.Combine(ReplayEngine.DataFolder,"preferences.json"),temp=path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(this));File.Move(temp,path,true);}}
    internal void ApplyStartup(){using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");if(LaunchOnLogin)key.SetValue("FLICK","\""+Environment.ProcessPath+"\" --background");else key.DeleteValue("FLICK",false);
        if(key.GetValue("REWIND") is string legacy&&legacy.StartsWith('"')){int end=legacy.IndexOf('"',1);if(end>1){string? directory=Path.GetDirectoryName(legacy[1..end]);if(directory is not null&&File.Exists(Path.Combine(directory,"REWIND.dll"))&&File.Exists(Path.Combine(directory,"NAudio.Wasapi.dll")))key.DeleteValue("REWIND",false);}}
    }
}
internal sealed record AudioSourceSetting(string Id,string Title,bool IsInput);
internal static class Shortcut
{
    internal static (uint Modifiers,uint Key) Parse(string text){
        uint mods=0;Key key=Key.None;
        foreach(string token in text.Split('+',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)){
            if(token=="0")key=Key.D0;else if(token=="Ctrl")mods|=2;else if(token=="Alt")mods|=1;else if(token=="Shift")mods|=4;
            else if(!Enum.TryParse(token,true,out key))throw new FormatException("Invalid shortcut key.");
        }
        if((mods==0&&(key<Key.F1||key>Key.F24))||key==Key.None||key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift)throw new FormatException("Choose a modifier and a letter or function key.");
        return(mods,(uint)KeyInterop.VirtualKeyFromKey(key));
    }
    internal static string Capture(KeyEventArgs e){
        Key key=e.Key==Key.System?e.SystemKey:e.Key;
        if(key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)return "";
        var mods=Keyboard.Modifiers;if(mods.HasFlag(ModifierKeys.Windows))throw new FormatException("Windows-key combinations are reserved.");var parts=new List<string>();
        if(mods.HasFlag(ModifierKeys.Control))parts.Add("Ctrl");if(mods.HasFlag(ModifierKeys.Alt))parts.Add("Alt");if(mods.HasFlag(ModifierKeys.Shift))parts.Add("Shift");
        parts.Add(key.ToString());string result=string.Join(" + ",parts);Parse(result);return result;
    }
}
