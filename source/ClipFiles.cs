using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Rewind;
// File operations are independent of capture. All writes remain inside the clip directory.
internal static class ClipFiles
{
    private sealed class Gate{internal readonly SemaphoreSlim Semaphore=new(1,1);internal int Users;}
    private static readonly object GateLock=new();
    private static readonly Dictionary<string,Gate> Gates=new(StringComparer.OrdinalIgnoreCase);
    private sealed class Lease(string path,Gate gate):IDisposable{private int released;public void Dispose(){if(Interlocked.Exchange(ref released,1)!=0)return;gate.Semaphore.Release();DropGate(path,gate);}}
    private static void DropGate(string path,Gate gate){lock(GateLock){if(--gate.Users==0){Gates.Remove(path);gate.Semaphore.Dispose();}}}
    private static string Owned(string path){
        string full=Path.GetFullPath(path),root=Path.GetFullPath(ReplayEngine.SaveFolder)+Path.DirectorySeparatorChar;
        if(!ClipCatalog.Folders().Any(folder=>full.StartsWith(Path.GetFullPath(folder)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))||!Path.GetExtension(full).Equals(".mp4",StringComparison.OrdinalIgnoreCase))throw new IOException("Select a clip from the FLICK clips folder.");
        if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked clips cannot be edited or deleted here.");return full;
    }
    internal static async Task<IDisposable> AcquireAsync(string path,CancellationToken cancellation=default){
        path=Owned(path);Gate gate;lock(GateLock){if(!Gates.TryGetValue(path,out gate!))Gates[path]=gate=new();gate.Users++;}
        try{if(!await gate.Semaphore.WaitAsync(TimeSpan.FromSeconds(15),cancellation))throw new IOException("This clip is busy with another operation. Please try again when it finishes.");return new Lease(path,gate);}
        catch{DropGate(path,gate);throw;}
    }
    internal static async Task WaitForFileReleaseAsync(string path,TimeSpan timeout,CancellationToken cancellation=default){
        path=Owned(path);var clock=Stopwatch.StartNew();
        while(true){
            cancellation.ThrowIfCancellationRequested();
            try{using var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);return;}
            catch(IOException e) when((e.HResult&0xffff) is 32 or 33){if(clock.Elapsed>=timeout)throw new IOException("The video file is still open. Close other players and try again.",e);}
            await Task.Delay(80,cancellation);
        }
    }
    internal static async Task DeleteAsync(string path){
        path=Owned(path);using var lease=await AcquireAsync(path);
        await Task.Run(()=>{File.Delete(path);string metadata=ReplayEngine.MetadataPath(path);File.Delete(metadata);File.Delete(ReplayEngine.ThumbnailPath(path));});
    }
    internal sealed record AudioTrack(int Stream,int Ordinal,string Title);
    internal sealed record Info(double Duration,double[] Keyframes,AudioTrack[] Audio);
    internal static async Task<Info> ProbeAsync(string path,CancellationToken cancellation=default){
        path=Owned(path);
        string json=await ReplayEngine.RunToolAsync("ffprobe",["-v","error","-select_streams","v:0","-skip_frame","nokey","-show_frames","-show_entries","frame=best_effort_timestamp_time:format=duration","-of","json",path],TimeSpan.FromSeconds(60),cancellation);
        using var doc=JsonDocument.Parse(json);
        double duration=double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);
        var keys=doc.RootElement.GetProperty("frames").EnumerateArray().Where(f=>f.TryGetProperty("best_effort_timestamp_time",out _)).Select(f=>double.Parse(f.GetProperty("best_effort_timestamp_time").GetString()!,CultureInfo.InvariantCulture)).ToArray();
        if(!double.IsFinite(duration)||duration<=0||keys.Length==0)throw new InvalidDataException("No playable video/keyframes found.");string streams=await ReplayEngine.RunToolAsync("ffprobe",["-v","error","-show_entries","stream=index,codec_type:stream_tags=title,handler_name","-of","json",path],TimeSpan.FromSeconds(20),cancellation);
        using var tracks=JsonDocument.Parse(streams);int ordinal=0;
        var audio=tracks.RootElement.GetProperty("streams").EnumerateArray().Where(t=>t.GetProperty("codec_type").GetString()=="audio").Select(t=>new AudioTrack(t.GetProperty("index").GetInt32(),ordinal++,t.TryGetProperty("tags",out var tags)&&tags.TryGetProperty("title",out var title)?title.GetString()!:t.TryGetProperty("tags",out var handlerTags)&&handlerTags.TryGetProperty("handler_name",out var handler)&&handler.GetString()!="SoundHandler"?handler.GetString()!:"Audio "+ordinal)).ToArray();
        return new(duration,keys,audio);
    }
    internal static double SnapStart(Info info,double requested)=>info.Keyframes.Where(k=>k<=requested+0.000001).DefaultIfEmpty(0).Max();
    internal static async Task<string> TrimAsync(string input,double start,double end,bool overwrite,IProgress<string>? progress=null,CancellationToken cancellation=default,bool removeInterior=false,IReadOnlyCollection<int>? keepAudio=null){
        input=Owned(input);progress?.Report("Waiting for clip...");using var lease=await AcquireAsync(input,cancellation);
        var info=await ProbeAsync(input,cancellation);
        if(!double.IsFinite(start)||!double.IsFinite(end)||start<0||end<=start||end>info.Duration+0.01)throw new ArgumentOutOfRangeException(nameof(start),"Choose a valid start/end range.");
        double actualStart=removeInterior?NextKeyframe(info,start):SnapStart(info,start);
        double actualEnd=removeInterior?NextKeyframe(info,end):end;
        if(removeInterior&&(actualStart<=0||actualEnd>=info.Duration||actualEnd<=actualStart))throw new ArgumentOutOfRangeException(nameof(start),"Choose an interior range with playable video on both sides.");
        var selected=keepAudio is null?info.Audio:info.Audio.Where(a=>keepAudio.Contains(a.Ordinal)).ToArray();
        var maps=new List<string>();if(selected.Length==info.Audio.Length)maps.AddRange(["-map","0"]);else{maps.AddRange(["-map","0:v:0"]);foreach(var track in selected)maps.AddRange(["-map","0:"+track.Stream]);}
        var before=new FileInfo(input);long length=before.Length;DateTime modified=before.LastWriteTimeUtc;
        string id=Guid.NewGuid().ToString("N")[..8];string temp=Path.Combine(Path.GetDirectoryName(input)!,Path.GetFileNameWithoutExtension(input)+"_"+id+".saving.mp4");
        string output=overwrite?input:Path.Combine(Path.GetDirectoryName(input)!,Path.GetFileNameWithoutExtension(input)+"_trim_"+id+".mp4");
        try{
            progress?.Report("Copying selected range...");
            if(removeInterior)await RemoveMiddleAsync(input,actualStart,actualEnd,info.Duration,temp,maps,cancellation);
            else{
                var args=new List<string>{"-v","error","-nostdin","-ss",actualStart.ToString("0.######",CultureInfo.InvariantCulture),"-i",input,"-t",(end-actualStart).ToString("0.######",CultureInfo.InvariantCulture)};
                args.AddRange(maps);args.AddRange(["-c","copy","-avoid_negative_ts","make_zero","-movflags","+faststart",temp]);
                await ReplayEngine.RunToolAsync("ffmpeg",args,TimeSpan.FromMinutes(3),cancellation);
            }
            string check=await ReplayEngine.RunToolAsync("ffprobe",["-v","error","-show_entries","format=duration:stream=codec_type","-of","json",temp],TimeSpan.FromSeconds(20),cancellation);
            using var probe=JsonDocument.Parse(check);
            if(!probe.RootElement.GetProperty("streams").EnumerateArray().Any(s=>s.GetProperty("codec_type").GetString()=="video"))throw new InvalidDataException("Trim did not produce video.");
            double duration=double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);
            if(probe.RootElement.GetProperty("streams").EnumerateArray().Count(s=>s.GetProperty("codec_type").GetString()=="audio")!=selected.Length)throw new InvalidDataException("Edit did not preserve the selected audio tracks.");
            if(!double.IsFinite(duration)||duration<=0)throw new InvalidDataException("Trim is empty.");
            // Validate before replacing an existing clip, so a bad remux cannot destroy its source.
            if(overwrite){progress?.Report("Validating trimmed video...");await ReplayEngine.RunToolAsync("ffmpeg",["-v","error","-xerror","-nostdin","-threads","2","-i",temp,"-map","0:v?","-map","0:a?","-f","null","-"],TimeSpan.FromMinutes(15),cancellation);}
            before.Refresh();if(!before.Exists||before.Length!=length||before.LastWriteTimeUtc!=modified)throw new IOException("The source clip changed while editing. It has been preserved.");
            if(overwrite){
                progress?.Report("Replacing original...");await WaitForFileReleaseAsync(input,TimeSpan.FromSeconds(8),cancellation);
                string backup=input+"."+id+".bak";var replacement=Stopwatch.StartNew();
                while(true){cancellation.ThrowIfCancellationRequested();try{File.Replace(temp,input,backup);break;}catch(IOException e) when((e.HResult&0xffff) is 32 or 33){if(replacement.Elapsed>TimeSpan.FromSeconds(5))throw new IOException("The original is still locked by another program. It has been preserved.",e);await Task.Delay(100,cancellation);}}
                try{File.Delete(backup);}catch(IOException){} // A locked backup remains recoverable.
                File.Delete(ReplayEngine.ThumbnailPath(input));
            }else File.Move(temp,output);
            await File.WriteAllTextAsync(ReplayEngine.MetadataPath(output),JsonSerializer.Serialize(new{State="Trim saved",FullDecode="Pending",RequestedStart=start,ActualStart=actualStart,RequestedEnd=end,ActualEnd=actualEnd,RemovedInterior=removeInterior,KeptAudioTracks=selected.Select(a=>a.Title).ToArray(),ActualSeconds=duration,StreamCopy=true,Created=DateTimeOffset.Now}));
            return output;
        }finally{if(File.Exists(temp))File.Delete(temp);}
    }
    internal static double NextKeyframe(Info info,double value)=>info.Keyframes.FirstOrDefault(k=>k>=value-.0001,info.Duration);
    private static async Task RemoveMiddleAsync(string input,double start,double end,double duration,string output,IEnumerable<string> maps,CancellationToken cancellation){
        string directory=Path.GetDirectoryName(input)!,id=Guid.NewGuid().ToString("N");
        string left=Path.Combine(directory,"flick-"+id+"-a.saving.mp4"),right=Path.Combine(directory,"flick-"+id+"-b.saving.mp4"),list=Path.Combine(directory,"flick-"+id+".concat.txt");
        try{
            async Task Slice(double from,double length,string target){
                var args=new List<string>{"-v","error","-nostdin","-ss",from.ToString("0.######",CultureInfo.InvariantCulture),"-i",input,"-t",length.ToString("0.######",CultureInfo.InvariantCulture)};
                args.AddRange(maps);args.AddRange(["-c","copy","-avoid_negative_ts","make_zero","-movflags","+faststart",target]);
                await ReplayEngine.RunToolAsync("ffmpeg",args,TimeSpan.FromMinutes(3),cancellation);
            }
            await Slice(0,start,left);await Slice(end,duration-end,right);
            await File.WriteAllTextAsync(list,"ffconcat version 1.0\nfile '"+Path.GetFileName(left)+"'\nfile '"+Path.GetFileName(right)+"'\n",cancellation);
            await ReplayEngine.RunToolAsync("ffmpeg",["-v","error","-nostdin","-f","concat","-safe","0","-i",list,"-map","0","-c","copy","-movflags","+faststart",output],TimeSpan.FromMinutes(3),cancellation);
        }finally{foreach(string file in new[]{left,right,list})if(File.Exists(file))File.Delete(file);}
    }

}
