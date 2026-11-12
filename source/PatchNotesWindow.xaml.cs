using System.Windows;
using System.Windows.Input;

namespace Rewind;

internal partial class PatchNotesWindow : Window
{
    internal PatchNotesWindow(string version,string notes)
    {
        InitializeComponent();
        MaxWidth=Math.Max(MinWidth,SystemParameters.WorkArea.Width);
        MaxHeight=Math.Max(MinHeight,SystemParameters.WorkArea.Height);
        NotesBody.Content=PatchNotes.FormatNotes(notes);
        BuildLabel.Text="FLICK v"+version;
        SourceInitialized+=(_,_)=>OverlayWindow.Round(this);
        SizeChanged+=(_,_)=>OverlayWindow.Round(this);
        Loaded+=(_,_)=>NotesBody.Focus();
        PreviewKeyDown+=(_,args)=>{
            if(args.Key!=Key.Escape)return;
            args.Handled=true;
            Close();
        };
    }

    private void CloseNotes(object sender,RoutedEventArgs args)=>Close();
}
