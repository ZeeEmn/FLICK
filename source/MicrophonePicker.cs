using System.Windows.Controls;
namespace Rewind;
internal sealed class MicrophonePicker : ComboBox
{
    private sealed record Device(string? Id,string Name){public override string ToString()=>Name;}
    private static readonly object Gate=new();
    private static Task<string[]>? cached;
    private static DateTime expires;
    private readonly Func<string?> current;
    private bool updating;
    internal MicrophonePicker(Func<string?> current,Func<string?,Task> changed,bool discover){
        SetResourceReference(StyleProperty,typeof(ComboBox));
        this.current=current;Items.Add(new Device(null,"Windows default input"));if(current() is {} id)Items.Add(new Device(id,"Selected microphone"));SelectedItem=Items.Cast<Device>().First(d=>d.Id==current());
        SelectionChanged+=async(_,_)=>{if(!updating&&SelectedItem is Device item&&item.Id!=current())await changed(item.Id);};
        if(discover){Loaded+=async(_,_)=>await RefreshAsync();DropDownOpened+=async(_,_)=>await RefreshAsync();IsVisibleChanged+=async(_,_)=>{if(IsVisible)await RefreshAsync();};}
    }
    private async Task RefreshAsync(){
        try{Task<string[]> request;lock(Gate){if(cached is null||DateTime.UtcNow>expires){expires=DateTime.UtcNow.AddMinutes(1);cached=Task.Run(AudioReplay.Microphones);}request=cached;}
            string[] names=await request;if(!IsLoaded||!IsVisible)return;updating=true;
            try{Items.Clear();Items.Add(new Device(null,"Windows default input"));foreach(string name in names){int split=name.IndexOf('|');if(split>0)Items.Add(new Device(name[..split],name[(split+1)..]));}if(current() is {} id&&!Items.Cast<Device>().Any(d=>d.Id==id))Items.Add(new Device(id,"Selected microphone (unavailable)"));SelectedItem=Items.Cast<Device>().First(d=>d.Id==current());}finally{updating=false;}
        }catch(Exception e){PrivateLog.Write("devices.log",e.Message);}
    }
}
