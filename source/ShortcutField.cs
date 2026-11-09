using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
namespace Rewind;
internal sealed class ShortcutField : TextBox
{
    private readonly Func<string> current;
    private readonly Func<string,Task<bool>> commit;
    private readonly Action<ShortcutField,bool> captureState;
    private bool listening,pending;
    internal ShortcutField(Func<string> current,Func<string,Task<bool>> commit,Action<ShortcutField,bool> captureState){
        SetResourceReference(StyleProperty,typeof(TextBox));
        this.current=current;this.commit=commit;this.captureState=captureState;Text=current();MinHeight=38;IsReadOnly=false;ToolTip="Click, then press the new shortcut. Escape cancels.";
        PreviewMouseLeftButtonDown+=(_,e)=>{e.Handled=true;BeginCapture();};GotKeyboardFocus+=(_,_)=>BeginCapture();LostKeyboardFocus+=(_,_)=>{if(!pending)CancelCapture();};
        PreviewTextInput+=(_,e)=>e.Handled=true;DataObject.AddPastingHandler(this,(_,e)=>e.CancelCommand());
        PreviewKeyDown+=async(_,e)=>{e.Handled=true;if(!listening)BeginCapture();Key key=e.Key==Key.System?e.SystemKey:e.Key;if(key==Key.Escape){CancelCapture();return;}try{string value=Shortcut.Capture(e);if(value.Length==0)return;pending=true;if(await commit(value)){pending=false;CancelCapture();}else{Text="Shortcut unavailable — try another";pending=false;}}catch(Exception error){pending=false;Text="Press Any Key...";ToolTip=error.Message;}};
        Unloaded+=(_,_)=>CancelCapture();
    }
    private void BeginCapture(){if(listening||pending)return;listening=true;captureState(this,true);Text="Press Any Key...";Foreground=ThemePalette.Accent;Effect=null;Focus();Keyboard.Focus(this);SelectAll();}
    internal void RefreshText(){if(!listening&&!pending)Text=current();}
    internal void CancelCapture(){if(!listening)return;listening=false;if(Effect is DropShadowEffect glow)glow.BeginAnimation(DropShadowEffect.OpacityProperty,null);Effect=null;Text=current();Foreground=ThemePalette.Foreground;captureState(this,false);}
}
