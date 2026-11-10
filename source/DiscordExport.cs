using System.IO;
using System.Globalization;
namespace Rewind;
internal static class DiscordExport
{
    private static readonly SemaphoreSlim Gate=new(1,1);
    internal const long Limit=9_900_000;
    internal static int Bitrate(double seconds,int tracks,long limit=Limit){if(!double.IsFinite(seconds)||seconds<=0)throw new ArgumentOutOfRangeException(nameof(seconds));double available=limit*.96*8/seconds-tracks*AudioBitrate(seconds,limit);return (int)Math.Clamp(available,30000,30000000);}
    private static int AudioBitrate(double seconds,long limit)=>(int)Math.Clamp(limit*.96*8/seconds*.25,48000,128000);
    internal static async Task<string> ExportAsync(string input,IReadOnlyCollection<int>? keep,IProgress<string>? progress,CancellationToken cancellation,long limit=Limit,IReadOnlyDictionary<int,double>? gains=null,bool external=false){
        if(!await Gate.WaitAsync(0,cancellation))throw new InvalidOperationException("Another compression is already running.");
        try{return await ExportCoreAsync(input,keep,progress,cancellation,limit,gains,external);}finally{Gate.Release();}
    }
    private static async Task<string> ExportCoreAsync(string input,IReadOnlyCollection<int>? keep,IProgress<string>? progress,CancellationToken cancellation,long limit,IReadOnlyDictionary<int,double>? gains,bool external){
        input=external?ReadOnlyVideo.LocalFile(input):input;
        using var lease=external?null:await ClipFiles.AcquireAsync(input,cancellation);
        using var sourceLock=external?new FileStream(input,FileMode.Open,FileAccess.Read,FileShare.Read):null;
        var info=external?await ReadOnlyVideo.ProbeAsync(input,cancellation):await ClipFiles.ProbeAsync(input,cancellation);var selected=AudioCompatibility.Editable(info.Audio).Where(a=>keep is null||keep.Contains(a.Ordinal)).ToArray();int rate=Bitrate(info.Duration,selected.Length>0?1:0,limit);int audioRate=AudioBitrate(info.Duration,limit);string destination=external?ReplayEngine.SaveFolder:Path.GetDirectoryName(input)!;Directory.CreateDirectory(destination);string stem=Path.Combine(destination,Path.GetFileNameWithoutExtension(input)+"_discord_"+Guid.NewGuid().ToString("N")[..6]);string temp=stem+".saving.mp4",final=stem+".mp4";
        try{
            var choice=await Task.Run(()=>EncoderCatalog.Detect("h264"),cancellation);for(int pass=0;pass<5;pass++){
                progress?.Report(pass==0?"Encoding size-limited export...":"Adjusting bitrate to meet the size budget...");
                var args=new List<string>{"-v","error","-nostdin","-y","-i",input,"-map","0:v:0"};if(selected.Length>0)AudioCompatibility.AddMix(args,selected.Select(t=>"0:"+t.Stream).ToArray(),selected.Select(t=>gains is not null&&gains.TryGetValue(t.Ordinal,out double gain)?gain:1).ToArray());
                int width=rate<100000?192:rate<140000?256:rate<250000?320:rate<400000?480:rate<1000000?1280:0;string scale=width>0?"scale='min(iw,"+width+")':-2,":"";args.AddRange(new[]{"-vf",scale+"format=yuv420p"});args.AddRange(EncoderCatalog.CodecArguments(choice,rate).Where(a=>a!="-an"));
                if(choice.Name.EndsWith("_nvenc",StringComparison.Ordinal)){int index=args.IndexOf("-multipass");args[index+1]="fullres";}
                args.AddRange(new[]{"-maxrate",rate.ToString(CultureInfo.InvariantCulture),"-bufsize",((long)rate*2).ToString(CultureInfo.InvariantCulture)});
                if(selected.Length>0)args.AddRange(new[]{"-c:a","aac","-b:a",audioRate.ToString(CultureInfo.InvariantCulture)});
                if(selected.Length>0)AudioCompatibility.Metadata(args,0,AudioCompatibility.MixTitle,true);
                args.AddRange(new[]{"-movflags","+faststart",temp});await ReplayEngine.RunToolAsync("ffmpeg",args,TimeSpan.FromMinutes(20),cancellation);long bytes=new FileInfo(temp).Length;
                if(bytes<limit){progress?.Report("Validating export...");await ReplayEngine.RunToolAsync("ffmpeg",new[]{"-v","error","-xerror","-i",temp,"-map","0:v?","-map","0:a?","-f","null","-"},TimeSpan.FromMinutes(15),cancellation);File.Move(temp,final);return final;}
                int next=(int)(rate*(limit/(double)bytes)*.90);if(next>=rate||next<30000)break;rate=next;
            }
            throw new IOException("The export could not fit under the size budget. Try a shorter clip.");
        }finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
