using System.Globalization;
using System.IO;
namespace Rewind;

// A default stereo mix for ordinary players; original lanes stay independently editable.
internal static class AudioCompatibility
{
    internal const string MixTitle="FLICK Mix";
    internal static ClipFiles.AudioTrack[] Editable(IEnumerable<ClipFiles.AudioTrack> tracks){var all=tracks.ToArray();var stems=all.Where(t=>t.Title!=MixTitle).ToArray();return stems.Length>0?stems:all;}
    internal static void AddMix(List<string> args,IReadOnlyList<string> inputs,IReadOnlyList<double>? gains=null){
        if(inputs.Count==0)return;
        var graph=new List<string>();var labels=new List<string>();
        for(int i=0;i<inputs.Count;i++){string label="flicklane"+i;double gain=gains is null?1:Math.Clamp(gains[i],0,1);graph.Add("["+inputs[i]+"]aformat=sample_rates=48000:channel_layouts=stereo,volume="+gain.ToString("0.######",CultureInfo.InvariantCulture)+"["+label+"]");labels.Add("["+label+"]");}
        graph.Add(string.Concat(labels)+"amix=inputs="+inputs.Count+":duration=longest:dropout_transition=0:normalize=1[flickmix]");
        args.AddRange(["-filter_complex",string.Join(";",graph),"-map","[flickmix]"]);
    }
    internal static void Metadata(List<string> args,int ordinal,string title,bool isDefault){args.AddRange(["-metadata:s:a:"+ordinal,"title="+title,"-metadata:s:a:"+ordinal,"handler_name="+title,"-disposition:a:"+ordinal,isDefault?"default":"0"]);}
    internal static void MapSavedAudio(List<string> args,IReadOnlyList<AudioReplay.AudioTrackFile> tracks){
        if(tracks.Count==0)return;
        if(tracks.Count>1){AddMix(args,Enumerable.Range(1,tracks.Count).Select(i=>i+":a:0").ToArray());Metadata(args,0,MixTitle,true);}
        for(int i=0;i<tracks.Count;i++){args.AddRange(["-map",(i+1)+":a:0"]);Metadata(args,i+(tracks.Count>1?1:0),tracks[i].Title,tracks.Count==1);}
        args.AddRange(["-c:a","aac","-b:a","192k","-ar","48000"]);
    }
    internal static async Task EnsureMixAsync(string input,CancellationToken cancellation){
        var info=await ReadOnlyVideo.ProbeAsync(input,cancellation);var tracks=Editable(info.Audio);if(tracks.Length<2)return;
        string temp=Path.Combine(Path.GetDirectoryName(input)!,"flick-mix-"+Guid.NewGuid().ToString("N")+".saving.mp4");
        try{
            var args=new List<string>{"-v","error","-nostdin","-i",input,"-map","0:v:0","-c","copy"};
            AddMix(args,tracks.Select(t=>"0:"+t.Stream).ToArray());args.AddRange(["-c:a:0","aac","-b:a:0","192k","-ar:a:0","48000"]);Metadata(args,0,MixTitle,true);
            for(int i=0;i<tracks.Length;i++){args.AddRange(["-map","0:"+tracks[i].Stream]);Metadata(args,i+1,tracks[i].Title,false);}
            args.AddRange(["-movflags","+faststart",temp]);await ReplayEngine.RunToolAsync("ffmpeg",args,TimeSpan.FromMinutes(5),cancellation);
            var check=await ReadOnlyVideo.ProbeAsync(temp,cancellation);if(check.Audio.Length!=tracks.Length+1||check.Audio[0].Title!=MixTitle)throw new InvalidDataException("The playback mix could not be verified.");
            File.Move(temp,input,true);
        }finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
