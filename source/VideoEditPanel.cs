using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace Rewind;

internal sealed class VideoEditPanel : StackPanel
{
    private sealed record Part(ClipFiles.EditSegment Segment,bool Kept);
    private static readonly (string Label,ClipFiles.VideoSize Size)[] Presets=
    [
        ("Original size",default),
        ("16:9 · YouTube",new(1920,1080)),
        ("9:16 · TikTok, Reels & Shorts",new(1080,1920)),
        ("4:5 · Instagram portrait feed",new(1080,1350)),
        ("1:1 · Instagram square feed",new(1080,1080)),
        ("3:4 · Portrait",new(1080,1440)),
        ("4:3 · Classic",new(1440,1080)),
        ("Custom",default)
    ];
    private readonly List<Part> parts=new();
    internal EditTrack Track {get;}=new();
    private readonly Button split=new(){Content="][",MinWidth=42,ToolTip="Split at playhead"};
    private readonly Button remove=new(){Margin=new Thickness(4,0,0,0)};
    private readonly Button crop=new(){Content="Crop segment",Margin=new Thickness(4,0,0,0)};
    private readonly Button clearCrop=new(){Content="Reset crop",Margin=new Thickness(4,0,0,0)};
    private readonly Button mergePrevious=new(){Content="Merge left",Margin=new Thickness(10,0,0,0)};
    private readonly Button mergeNext=new(){Content="Merge right",Margin=new Thickness(4,0,0,0)};
    private readonly TextBlock feedback=new(){Foreground=ThemePalette.Muted,Margin=new Thickness(0,0,0,8),Visibility=Visibility.Collapsed};
    private readonly ComboBox sizes=new(){MinWidth=202,Margin=new Thickness(0,0,10,0),ItemsSource=Presets.Select(p=>p.Label).ToArray()};
    private readonly TextBox width=new(){Width=62,MaxLength=4};
    private readonly TextBox height=new(){Width=62,MaxLength=4};
    private bool sizing;
    private int selection;
    private double playhead;
    private ClipFiles.VideoSize original;
    internal bool Cropping {get;private set;}
    internal ClipFiles.VideoCrop CurrentCrop=>parts.Count==0?ClipFiles.VideoCrop.Full:parts[selection].Segment.Crop??ClipFiles.VideoCrop.Full;
    internal event Action? Changed;

    internal VideoEditPanel(Func<double> position)
    {
        Children.Add(Track);
        var toolbar=new Grid{Margin=new Thickness(0,0,0,8)};
        toolbar.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        toolbar.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        toolbar.RowDefinitions.Add(new(){Height=GridLength.Auto});
        toolbar.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var commands=new WrapPanel();
        commands.Children.Add(split);
        commands.Children.Add(remove);
        commands.Children.Add(crop);
        commands.Children.Add(clearCrop);
        commands.Children.Add(mergePrevious);
        commands.Children.Add(mergeNext);
        toolbar.Children.Add(commands);
        AutomationProperties.SetName(split,"Split at playhead");
        split.Click+=(_,_)=>Split(position());
        remove.Click+=(_,_)=>{
            if(parts.Count==0)return;
            var part=parts[selection];
            parts[selection]=part with{Kept=!part.Kept};
            Cropping=false;
            Refresh();
        };
        crop.Click+=(_,_)=>{
            if(parts.Count==0)return;
            Cropping=!Cropping;
            Refresh();
        };
        clearCrop.Click+=(_,_)=>SetCrop(ClipFiles.VideoCrop.Full);
        mergePrevious.Click+=(_,_)=>Merge(-1);
        mergeNext.Click+=(_,_)=>Merge(1);
        ToolTipService.SetShowOnDisabled(mergePrevious,true);
        ToolTipService.SetShowOnDisabled(mergeNext,true);

        var size=new WrapPanel();
        size.Children.Add(new TextBlock{Text="Output size",Margin=new Thickness(0,0,8,0),VerticalAlignment=VerticalAlignment.Center});
        size.Children.Add(sizes);
        size.Children.Add(width);
        size.Children.Add(new TextBlock{Text=" × ",VerticalAlignment=VerticalAlignment.Center});
        size.Children.Add(height);
        toolbar.Children.Add(size);
        void PlaceToolbar(double available)
        {
            bool wide=available>=1050;
            Grid.SetRow(size,wide?0:1);
            Grid.SetColumn(size,wide?1:0);
            size.HorizontalAlignment=wide?HorizontalAlignment.Right:HorizontalAlignment.Left;
            size.Margin=wide?new Thickness(18,0,0,0):new Thickness(0,8,0,0);
        }
        toolbar.SizeChanged+=(_,e)=>PlaceToolbar(e.NewSize.Width);
        PlaceToolbar(0);
        Children.Add(toolbar);
        Children.Add(feedback);
        AutomationProperties.SetName(sizes,"Output size preset");
        AutomationProperties.SetName(width,"Output width");
        AutomationProperties.SetName(height,"Output height");
        sizes.SelectionChanged+=(_,_)=>{
            if(sizing||sizes.SelectedIndex<0)return;
            if(sizes.SelectedIndex==0)SetSize(original);
            else if(sizes.SelectedIndex<Presets.Length-1)SetSize(Presets[sizes.SelectedIndex].Size);
            Changed?.Invoke();
        };
        void CustomSizeChanged()
        {
            if(sizing)return;
            sizing=true;
            sizes.SelectedIndex=Presets.Length-1;
            sizing=false;
            Changed?.Invoke();
        }
        width.TextChanged+=(_,_)=>CustomSizeChanged();
        height.TextChanged+=(_,_)=>CustomSizeChanged();
        Refresh();
    }

    private void SetSize(ClipFiles.VideoSize size)
    {
        sizing=true;
        width.Text=size.Width.ToString(CultureInfo.InvariantCulture);
        height.Text=size.Height.ToString(CultureInfo.InvariantCulture);
        sizing=false;
    }

    internal void Load(ClipFiles.Info info)
    {
        original=new(info.Width&~1,info.Height&~1);
        sizes.SelectedIndex=0;
        parts.Add(new(new(0,info.Duration),true));
        Refresh();
    }

    internal void SetPlayhead(double position)
    {
        playhead=position;
        if(parts.Count==0)return;
        int index=parts.FindIndex(part=>position<part.Segment.End);
        if(index<0)index=parts.Count-1;
        if(index!=selection)
        {
            selection=index;
            Cropping=false;
            feedback.Visibility=Visibility.Collapsed;
            RefreshCommands();
        }
        Track.SetPosition(position,selection);
    }

    internal void Split(double at)
    {
        if(parts.Count>=64){ShowFeedback("This edit already has 64 segments.");return;}
        int index=parts.FindIndex(part=>at>part.Segment.Start+.05&&at<part.Segment.End-.05);
        if(index<0){ShowFeedback("Move the playhead inside a segment to split it.");return;}
        var part=parts[index];
        parts[index]=part with{Segment=part.Segment with{End=at}};
        parts.Insert(index+1,new(new(at,part.Segment.End),part.Kept));
        playhead=at;
        selection=index+1;
        Cropping=false;
        Refresh();
    }

    private bool CanMerge(int neighbor)=>neighbor>=0&&neighbor<parts.Count&&parts[selection].Kept&&parts[neighbor].Kept&&
        parts[selection].Segment.Crop==parts[neighbor].Segment.Crop;

    private void Merge(int direction)
    {
        int neighbor=selection+direction;
        if(!CanMerge(neighbor))return;
        int first=Math.Min(selection,neighbor);
        var left=parts[first];
        parts[first]=left with{Segment=left.Segment with{End=parts[first+1].Segment.End}};
        parts.RemoveAt(first+1);
        selection=first;
        Cropping=false;
        Refresh();
    }

    private void ShowFeedback(string message)
    {
        feedback.Text=message;
        feedback.Visibility=Visibility.Visible;
    }

    internal void StopCrop()
    {
        if(!Cropping)return;
        Cropping=false;
        RefreshCommands();
        Changed?.Invoke();
    }

    internal void SetCrop(ClipFiles.VideoCrop value)
    {
        if(parts.Count==0)return;
        var part=parts[selection];
        var revised=part.Segment with{Crop=value==ClipFiles.VideoCrop.Full?null:value};
        parts[selection]=part with{Segment=revised};
        if((revised.Crop is null)!=(part.Segment.Crop is null))Refresh();
        else{RefreshCommands();Changed?.Invoke();}
    }

    internal ClipFiles.EditSegment[] Kept()=>parts.Where(part=>part.Kept).Select(part=>part.Segment).ToArray();
    internal ClipFiles.VideoCrop? CropAt(double position)=>parts.FirstOrDefault(part=>
        position>=part.Segment.Start&&position<part.Segment.End)?.Segment.Crop;
    internal bool IsKept(double position)=>parts.Any(part=>part.Kept&&position>=part.Segment.Start&&position<part.Segment.End);
    internal double? NextKept(double position)=>parts.FirstOrDefault(part=>part.Kept&&part.Segment.End>position)?.Segment.Start;

    internal ClipFiles.VideoSize? Resized()
    {
        if(sizes.SelectedIndex<=0)return null;
        if(int.TryParse(width.Text,NumberStyles.None,CultureInfo.InvariantCulture,out int w)&&
            int.TryParse(height.Text,NumberStyles.None,CultureInfo.InvariantCulture,out int h))
        {
            var size=new ClipFiles.VideoSize(w,h);
            if(size.Valid)return size;
        }
        throw new InvalidDataException("Enter an even width and height up to 3840 × 2160.");
    }
    internal ClipFiles.VideoSize PreviewSize()
    {
        try{return Resized()??original;}
        catch(InvalidDataException){return original;}
    }

    private void Refresh()
    {
        feedback.Visibility=Visibility.Collapsed;
        Track.ShowSegments(parts.Select(part=>new EditTrack.Span(part.Segment.Start,part.Segment.End,part.Kept,part.Segment.Crop is not null)).ToArray(),selection);
        Track.SetPosition(playhead,selection);
        RefreshCommands();
        Changed?.Invoke();
    }

    private void RefreshCommands()
    {
        var current=parts.Count==0?null:parts[selection];
        split.IsEnabled=remove.IsEnabled=crop.IsEnabled=current is not null;
        remove.Content=current?.Kept==false?"Restore segment":"Remove segment";
        crop.Content=Cropping?"Done cropping":"Crop segment";
        clearCrop.IsEnabled=current?.Segment.Crop is not null;
        mergePrevious.IsEnabled=current is not null&&CanMerge(selection-1);
        mergeNext.IsEnabled=current is not null&&CanMerge(selection+1);
        const string mergeHint="Both adjacent segments must be kept and have the same crop.";
        mergePrevious.ToolTip=mergePrevious.IsEnabled?"Merge with the segment on the left.":mergeHint;
        mergeNext.ToolTip=mergeNext.IsEnabled?"Merge with the segment on the right.":mergeHint;
    }
}
