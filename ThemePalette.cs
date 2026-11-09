using System.Windows;
using System.Windows.Media;
using System.Windows.Data;
using Microsoft.Win32;
namespace Rewind;
internal static class ThemePalette
{
    internal const string DefaultAccent="#980234";
    internal static event Action? Changed;
    private sealed class ColorValue : DependencyObject {internal static readonly DependencyProperty ValueProperty=DependencyProperty.Register(nameof(Value),typeof(Color),typeof(ColorValue));public Color Value{get=>(Color)GetValue(ValueProperty);set=>SetValue(ValueProperty,value);}}
    private static readonly Dictionary<SolidColorBrush,ColorValue> colors=new();
    private static SolidColorBrush Make(Color color){var value=new ColorValue{Value=color};var brush=new SolidColorBrush();BindingOperations.SetBinding(brush,SolidColorBrush.ColorProperty,new Binding(nameof(ColorValue.Value)){Source=value});colors.Add(brush,value);return brush;}
    private static void Set(SolidColorBrush brush,Color color)=>colors[brush].Value=color;
    internal static readonly SolidColorBrush Accent=Make(Color.FromRgb(152,2,52)), Surface=Make(Color.FromRgb(7,8,10)), Secondary=Make(Color.FromRgb(27,32,38)), Foreground=Make(Colors.White), Muted=Make(Color.FromRgb(144,154,168)), Line=Make(Color.FromRgb(57,65,77)), OnAccent=Make(Colors.Black);
    internal static readonly SolidColorBrush Divider=Make(Color.FromArgb(77,152,2,52));
    private static bool system;
    private static string lastAccent=DefaultAccent,lastVariant="System Default";
    internal static bool ValidHex(string? text)=>text is not null&&System.Text.RegularExpressions.Regex.IsMatch(text,"^#[0-9a-fA-F]{6}$");
    internal static void Initialize(){if(Application.Current is null)return;foreach(var pair in new[]{("ThemeAccent",Accent),("ThemeSurface",Surface),("ThemeSecondary",Secondary),("ThemeText",Foreground),("ThemeMuted",Muted),("ThemeLine",Line),("ThemeOnAccent",OnAccent),("ThemeDivider",Divider)})Application.Current.Resources[pair.Item1]=pair.Item2;}
    internal static void Apply(string accent,string variant){Initialize();lastAccent=ValidHex(accent)?accent:DefaultAccent;lastVariant=variant;system=variant=="System Default";bool light=variant=="Light Mode";
        if(system)try{using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");light=key?.GetValue("AppsUseLightTheme") is int value&&value!=0;}catch{}
        Set(Accent,(Color)ColorConverter.ConvertFromString(lastAccent));Set(Surface,(Color)ColorConverter.ConvertFromString(light?"#F3F4F6":"#07080A"));Set(Secondary,(Color)ColorConverter.ConvertFromString(light?"#E0E4EA":"#1B2026"));Set(Foreground,(Color)ColorConverter.ConvertFromString(light?"#17202B":"#F0F3F7"));Set(Muted,(Color)ColorConverter.ConvertFromString(light?"#526174":"#909AA8"));Set(Line,(Color)ColorConverter.ConvertFromString(light?"#BAC2CE":"#39414D"));Set(Divider,Color.FromArgb(77,Accent.Color.R,Accent.Color.G,Accent.Color.B));Set(OnAccent,(.2126*Accent.Color.R+.7152*Accent.Color.G+.0722*Accent.Color.B)>145?Colors.Black:Colors.White);Changed?.Invoke();
    }
    internal static void SystemChanged(){if(system)Apply(lastAccent,lastVariant);}
    internal static Brush Resolve(string color)=>color.ToUpperInvariant() switch{"#C1FF43" or "#293A15"=>Accent,"#07080A"=>Surface,"#F0F3F7" or "#FFFFFF"=>Foreground,"#858F9F" or "#929CA9" or "#959DA9"=>Muted,"#343D48" or "#29313B" or "#303842" or "#485563" or "#333943"=>Line,"#1B2026" or "#151B22" or "#21262C"=>Secondary,_=>(Brush)new BrushConverter().ConvertFromString(color)!};
}
