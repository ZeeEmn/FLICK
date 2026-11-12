using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Rewind;

internal static class PatchNotes
{
    private static PatchNotesWindow? openWindow;
    private static readonly Lazy<string> CurrentNotes=new(ReadNotes);
    internal static bool HasNotes=>!string.IsNullOrWhiteSpace(CurrentNotes.Value);

    private static string ReadNotes()
    {
        var assembly=typeof(PatchNotes).Assembly;
        using var stream=assembly.GetManifestResourceStream("Rewind.PatchNotes.txt");
        using var reader=stream is null?null:new StreamReader(stream);
        return reader?.ReadToEnd()??"";
    }

    internal static Action? Prepare()=>Prepare(ReplayEngine.DataFolder,UpdateChecker.DisplayVersion,CurrentNotes.Value);

    internal static void Open()
    {
        if(HasNotes)Show(UpdateChecker.DisplayVersion,CurrentNotes.Value);
    }

    internal static Action? Prepare(string folder,string version,string notes)
    {
        string marker=Path.Combine(folder,"patch-notes-version.txt");
        try
        {
            if(File.Exists(marker))
            {
                if(!IsUpgrade(File.ReadAllText(marker).Trim(),version))return null;
            }
            else if(!File.Exists(Path.Combine(folder,"preferences.json")) &&
                    !File.Exists(Path.Combine(folder,"rewind.log")))
            {
                Record(marker,version);
                return null;
            }

            if(string.IsNullOrWhiteSpace(notes))
            {
                Record(marker,version);
                return null;
            }
            return ()=>Show(version,notes,marker);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException)
        {
            PrivateLog.Write("ui.log","Patch notes: "+error.Message);
            return null;
        }
    }

    internal static bool IsUpgrade(string previous,string current)
    {
        var oldVersion=previous.Split('-',2);
        var newVersion=current.Split('-',2);
        if(!Version.TryParse(oldVersion[0],out var oldNumber)||
           !Version.TryParse(newVersion[0],out var newNumber))return false;
        int comparison=newNumber.CompareTo(oldNumber);
        if(comparison!=0)return comparison>0;
        if(newVersion.Length==1)return oldVersion.Length==2;
        if(oldVersion.Length==1)return false;
        return TryDevNumber(oldVersion[1],out int oldDev)&&
               TryDevNumber(newVersion[1],out int newDev)&&newDev>oldDev;
    }

    private static bool TryDevNumber(string label,out int number)
    {
        number=0;
        return label.StartsWith("dev.",StringComparison.Ordinal)&&int.TryParse(label.AsSpan(4),out number);
    }

    private static void Record(string marker,string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        string temporary=marker+".tmp";
        File.WriteAllText(temporary,version);
        File.Move(temporary,marker,true);
    }

    private static void Show(string version,string notes,string? marker=null)
    {
        void RecordShown()
        {
            if(marker is null)return;
            try{Record(marker,version);}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException)
            {PrivateLog.Write("ui.log","Patch notes marker: "+error.Message);}
        }

        if(openWindow is {} existing)
        {
            if(existing.WindowState==WindowState.Minimized)existing.WindowState=WindowState.Normal;
            existing.Activate();
            RecordShown();
            return;
        }

        var window=new PatchNotesWindow(version,notes);
        openWindow=window;
        window.Closed+=(_,_)=>openWindow=null;
        if(marker is not null)
        {
            void OnRendered(object? sender,EventArgs args)
            {
                window.ContentRendered-=OnRendered;
                RecordShown();
            }
            window.ContentRendered+=OnRendered;
        }
        window.Show();
    }

    internal static StackPanel FormatNotes(string notes)
    {
        var content=new StackPanel();
        StackPanel? category=null;
        int entryCount=0;
        bool featured=false;
        foreach(string block in notes.ReplaceLineEndings("\n").TrimEnd().Split("\n\n",StringSplitOptions.RemoveEmptyEntries))
        {
            if(block.StartsWith("# ",StringComparison.Ordinal))
            {
                var title=NotesText(block[2..],28);
                title.FontWeight=FontWeights.Bold;
                title.Margin=new Thickness(0,2,0,10);
                content.Children.Add(title);
            }
            else if(block.StartsWith("## ",StringComparison.Ordinal))
            {
                string section=block[3..];
                featured=section=="New:";
                category=new StackPanel();
                entryCount=0;
                var heading=NotesText(section,13);
                heading.FontWeight=FontWeights.SemiBold;
                heading.Foreground=ThemePalette.Muted;
                heading.Margin=new Thickness(0,0,0,10);
                category.Children.Add(heading);
                content.Children.Add(new Border
                {
                    Background=ThemePalette.Divider,
                    BorderBrush=featured?ThemePalette.Accent:ThemePalette.Line,
                    BorderThickness=featured?new Thickness(3,1,1,1):new Thickness(1),
                    CornerRadius=new CornerRadius(8),
                    Padding=new Thickness(14,12,14,12),
                    Margin=new Thickness(0,2,0,12),
                    Child=category
                });
            }
            else if(block.StartsWith("### ",StringComparison.Ordinal))
            {
                string[] entry=block[4..].Split('\n',2);
                var title=NotesText(entry[0],featured?18:15);
                title.FontWeight=FontWeights.SemiBold;
                var entries=category??content;
                if(entryCount++>0)entries.Children.Add(new Border
                {
                    Height=1,Background=ThemePalette.Line,Margin=new Thickness(0,10,0,10)
                });
                entries.Children.Add(title);
                if(entry.Length==2)
                {
                    var body=NotesText(entry[1],13);
                    body.Foreground=ThemePalette.Muted;
                    body.Margin=new Thickness(0,3,0,0);
                    entries.Children.Add(new Expander
                    {
                        Header="Dev Notes",
                        IsExpanded=featured,
                        Content=body
                    });
                }
            }
            else
            {
                var body=NotesText(block,13);
                body.Margin=new Thickness(0,0,0,8);
                content.Children.Add(body);
            }
        }
        return content;
    }

    private static TextBlock NotesText(string text,double size)=>new()
    {
        Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,LineHeight=size*1.5
    };
}
