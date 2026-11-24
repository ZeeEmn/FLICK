using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;

namespace Rewind;

internal sealed class AudioSourceSelector : Window
{
    private readonly Dictionary<string,AudioSourceSetting> selected;
    private readonly StackPanel rows=new();
    private readonly TextBlock status=new(){Foreground=ThemePalette.Muted,TextWrapping=TextWrapping.Wrap};
    private readonly Button apply=new(){Content="Apply audio lanes",IsEnabled=false,HorizontalAlignment=HorizontalAlignment.Right};

    internal AudioSourceSelector(IReadOnlyList<AudioSourceSetting> current,Action<IReadOnlyList<AudioSourceSetting>> save,bool discover)
    {
        Title="FLICK · Audio lanes";
        Width=620;Height=500;MinWidth=380;MinHeight=290;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;
        Background=ThemePalette.Surface;Foreground=ThemePalette.Foreground;FontFamily=Typography.Inter;
        MaxHeight=SystemParameters.WorkArea.Height;MaxWidth=SystemParameters.WorkArea.Width;
        WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=0,GlassFrameThickness=new Thickness(0)});
        SourceInitialized+=(_,_)=>OverlayWindow.Round(this);
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/FLICK;component/Theme.xaml",UriKind.Relative)});
        selected=current.ToDictionary(a=>a.Id,StringComparer.OrdinalIgnoreCase);

        var root=new DockPanel();
        var header=Shell.DialogHeader(this,"Audio lanes");
        DockPanel.SetDock(header,Dock.Top);
        root.Children.Add(header);
        var body=new DockPanel{Margin=new Thickness(20,14,20,16)};
        root.Children.Add(body);Content=root;
        var hint=new TextBlock{Text="Add up to six audio lanes. Use Choose channels to record inputs from the same device separately.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
        DockPanel.SetDock(hint,Dock.Top);body.Children.Add(hint);
        var footer=new DockPanel{Margin=new Thickness(0,12,0,0)};
        DockPanel.SetDock(footer,Dock.Bottom);body.Children.Add(footer);
        DockPanel.SetDock(apply,Dock.Right);footer.Children.Add(apply);footer.Children.Add(status);
        body.Children.Add(new SmoothScrollViewer{Content=rows,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});

        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};
        Loaded+=async(_,_)=>{
            try
            {
                var devices=discover?await Task.Run(AudioReplay.Endpoints):[];
                if(!IsLoaded||!IsVisible)return;
                foreach(var device in devices)AddDevice(device);
                var present=devices.Select(d=>d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach(var source in selected.Values.Where(s=>!present.Contains(s.Id)).ToArray())
                    AddDevice(new(source.Id,source.Title+" (unavailable)",source.IsInput,1));
                if(rows.Children.Count==0)status.Text="No active audio devices found.";
                else apply.IsEnabled=true;
            }
            catch(Exception)
            {
                status.Text="Windows audio devices could not be loaded. Close this window and try again.";
            }
        };
        apply.Click+=(_,_)=>{
            if(selected.Values.Sum(s=>s.LaneCount)>AudioSourceSetting.MaximumLanes)
            {
                status.Text="Choose at most six additional lanes.";
                return;
            }
            try{save(AudioSourceSetting.Normalize(selected.Values));Close();}
            catch(InvalidOperationException e){status.Text=e.Message;}
        };
    }

    private void AddDevice(AudioReplay.Endpoint device)
    {
        selected.TryGetValue(device.Id,out var saved);
        string label=(device.IsInput?"Input · ":"Output · ")+device.Title;
        var group=new StackPanel{Margin=new Thickness(0,7,0,0)};
        rows.Children.Add(group);
        var row=new DockPanel{MinHeight=36};
        group.Children.Add(row);
        var check=new CheckBox{IsChecked=saved is not null,VerticalAlignment=VerticalAlignment.Center,ToolTip=label,Margin=new Thickness(12,0,6,0)};
        AutomationProperties.SetName(check,label);
        DockPanel.SetDock(check,Dock.Right);row.Children.Add(check);

        bool canChoose=device.IsInput&&device.Channels>1;
        bool expanded=canChoose&&saved?.Channels is {Length:>0};
        var choice=new Button
        {
            Visibility=canChoose?Visibility.Visible:Visibility.Collapsed,
            Foreground=ThemePalette.Foreground,Background=ThemePalette.Surface,
            FontSize=11,Margin=new Thickness(8,0,0,0),Padding=new Thickness(8,4,8,4),
            MinWidth=126,VerticalAlignment=VerticalAlignment.Center
        };
        AutomationProperties.SetName(choice,"Choose channels for "+device.Title);
        DockPanel.SetDock(choice,Dock.Right);row.Children.Add(choice);
        row.Children.Add(new TextBlock
        {
            Text=label,Foreground=ThemePalette.Foreground,TextWrapping=TextWrapping.Wrap,
            VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,0,0,0)
        });
        var channelRows=new StackPanel();
        var channelPanel=new Border
        {
            Child=channelRows,Background=ThemePalette.Secondary,CornerRadius=new CornerRadius(6),
            Margin=new Thickness(6,5,6,2),Padding=new Thickness(12,9,10,9)
        };
        group.Children.Add(channelPanel);
        group.Children.Add(new Border{Height=1,Background=ThemePalette.Divider,Margin=new Thickness(6,7,6,0)});
        void SetExpanded(bool value)
        {
            expanded=value;
            choice.Content=expanded?"Hide channels ▴":"Choose channels ▾";
            channelPanel.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;
        }
        SetExpanded(expanded);
        choice.Click+=(_,_)=>SetExpanded(!expanded);
        var parts=new List<(int Index,CheckBox Pick,TextBox Name)>();

        void Change(bool enabled)
        {
            if(!enabled){selected.Remove(device.Id);status.Text="";return;}
            var channels=parts.Where(p=>p.Pick.IsChecked==true)
                .Select(p=>new AudioChannelSetting(p.Index,p.Name.Text)).ToArray();
            selected[device.Id]=new(device.Id,saved?.Title??device.Title,device.IsInput,
                channels.Length>0?channels:parts.Count==0?saved?.Channels:null);
            status.Text="";
        }

        if(canChoose)
        {
            var note=new TextBlock{Text="Selected channels become separate tracks. A split device replaces its regular microphone lane.",Foreground=ThemePalette.Muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,5)};
            channelRows.Children.Add(note);
            for(int index=0;index<device.Channels;index++)
            {
                string input=$"Input {index+1}";
                var prior=saved?.Channels?.FirstOrDefault(c=>c.Index==index);
                var item=new DockPanel{Margin=new Thickness(0,3,0,3)};
                var pick=new CheckBox{IsChecked=prior is not null,VerticalAlignment=VerticalAlignment.Center};
                AutomationProperties.SetName(pick,device.Title+" "+input);
                DockPanel.SetDock(pick,Dock.Right);item.Children.Add(pick);
                var caption=new TextBlock
                {
                    Text=input,Foreground=ThemePalette.Foreground,Width=65,VerticalAlignment=VerticalAlignment.Center,
                    Margin=new Thickness(0,0,8,0)
                };
                DockPanel.SetDock(caption,Dock.Left);item.Children.Add(caption);
                var name=new TextBox
                {
                    Text=prior?.Name??"",MaxLength=160,ToolTip="Optional track name",IsEnabled=prior is not null,
                    Margin=new Thickness(0,0,12,0),MinHeight=29
                };
                AutomationProperties.SetName(name,input+" track name");
                item.Children.Add(name);channelRows.Children.Add(item);
                parts.Add((index,pick,name));
                pick.Click+=(_,_)=>{
                    name.IsEnabled=pick.IsChecked==true;
                    check.IsChecked=parts.Any(p=>p.Pick.IsChecked==true);
                    Change(check.IsChecked==true);
                };
                name.TextChanged+=(_,_)=>{if(check.IsChecked==true&&pick.IsChecked==true)Change(true);};
            }
        }
        check.Click+=(_,_)=>{
            if(check.IsChecked!=true)
                foreach(var (_,pick,name) in parts){pick.IsChecked=false;name.IsEnabled=false;}
            Change(check.IsChecked==true);
        };
    }
}
