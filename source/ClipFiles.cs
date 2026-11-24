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
    internal static async Task<string> RenameAsync(string path,string name)
    {
        path=Owned(path);
        name=name.Trim();
        if(name.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase))name=name[..^4].TrimEnd();
        string stem=name.Split('.',2)[0].TrimEnd();
        if(name.Length==0||name.Length>251||name.EndsWith('.')||name.EndsWith(".saving",StringComparison.OrdinalIgnoreCase)||
            name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||
            System.Text.RegularExpressions.Regex.IsMatch(stem,@"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[1-9¹²³])\z",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase|System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new IOException("Enter a valid Windows filename for this clip.");

        string destination=Path.Combine(Path.GetDirectoryName(path)!,name+".mp4");
        using var lease=await AcquireAsync(path);
        return await Task.Run(()=>{
            if(path==destination)return path;
            if(!string.Equals(path,destination,StringComparison.OrdinalIgnoreCase)&&File.Exists(destination))
                throw new IOException("A clip with that name already exists in this folder.");
            File.Move(path,destination);
            if(!string.Equals(path,destination,StringComparison.OrdinalIgnoreCase))
            {
                try{MoveSidecar(ReplayEngine.MetadataPath(path),ReplayEngine.MetadataPath(destination));}
                catch(Exception e) when(e is IOException or UnauthorizedAccessException){PrivateLog.Write("clips.log","Rename metadata: "+e.Message);}
                try{MoveSidecar(ReplayEngine.ThumbnailPath(path),ReplayEngine.ThumbnailPath(destination));}
                catch(Exception e) when(e is IOException or UnauthorizedAccessException){PrivateLog.Write("clips.log","Rename preview: "+e.Message);}
            }
            return destination;
        });
    }
    private static void MoveSidecar(string source,string destination)
    {
        if(File.Exists(source))File.Move(source,destination,true);
        else File.Delete(destination);
    }
    internal sealed record AudioTrack(int Stream,int Ordinal,string Title);
    internal sealed record Info(double Duration,AudioTrack[] Audio,int Width=0,int Height=0);
    internal sealed record EditSegment(double Start,double End,VideoCrop? Crop=null);
    internal readonly record struct VideoSize(int Width,int Height)
    {
        internal bool Valid=>Width is >=2 and <=3840&&Height is >=2 and <=2160&&Width%2==0&&Height%2==0;
    }
    internal readonly record struct VideoCrop(double Left,double Top,double Right,double Bottom)
    {
        internal static VideoCrop Full=>new(0,0,1,1);
        internal (int X,int Y,int Width,int Height) Pixels(VideoSize source)
        {
            if(!double.IsFinite(Left)||!double.IsFinite(Top)||!double.IsFinite(Right)||!double.IsFinite(Bottom)||
                Left<0||Top<0||Right>1||Bottom>1||Left>=Right||Top>=Bottom||source.Width<2||source.Height<2)
                throw new InvalidDataException("Choose a valid crop inside the video.");
            static int Even(double value)=>(int)Math.Round(value/2)*2;
            int x=Math.Clamp(Even(Left*source.Width),0,(source.Width&~1)-2);
            int y=Math.Clamp(Even(Top*source.Height),0,(source.Height&~1)-2);
            int right=Math.Clamp(Even(Right*source.Width),x+2,source.Width&~1);
            int bottom=Math.Clamp(Even(Bottom*source.Height),y+2,source.Height&~1);
            return (x,y,right-x,bottom-y);
        }
    }
    internal static Task<Info> ProbeAsync(string path,CancellationToken cancellation=default)=>
        ReadOnlyVideo.ProbeAsync(Owned(path),cancellation);
    internal static async Task<string> TrimAsync(string input,double start,double end,bool overwrite,IProgress<string>? progress=null,
        CancellationToken cancellation=default,bool removeInterior=false,IReadOnlyCollection<int>? keepAudio=null)
    {
        var info=await ProbeAsync(input,cancellation);
        if(!double.IsFinite(start)||!double.IsFinite(end)||start<0||end<=start||end>info.Duration+.01)
            throw new ArgumentOutOfRangeException(nameof(start),"Choose a valid start/end range.");
        if(removeInterior&&(start<=0||end>=info.Duration))
            throw new ArgumentOutOfRangeException(nameof(start),"Choose an interior range with playable video on both sides.");
        EditSegment[] segments=removeInterior?[new(0,start),new(end,info.Duration)]:[new(start,Math.Min(end,info.Duration))];
        return await SaveEditAsync(input,segments,null,overwrite,progress,cancellation,keepAudio);
    }

    internal static async Task<string> SaveEditAsync(string input,IReadOnlyList<EditSegment> segments,VideoSize? resize,bool overwrite,
        IProgress<string>? progress=null,CancellationToken cancellation=default,IReadOnlyCollection<int>? keepAudio=null)
    {
        input=Owned(input);
        progress?.Report("Waiting for clip…");
        using var lease=await AcquireAsync(input,cancellation);
        var before=new FileInfo(input);
        long length=before.Length;
        DateTime modified=before.LastWriteTimeUtc;
        var info=await ProbeAsync(input,cancellation);
        var source=new VideoSize(info.Width,info.Height);
        var outputSize=resize??new VideoSize(info.Width&~1,info.Height&~1);
        if(!outputSize.Valid)throw new InvalidDataException("Choose an even output size up to 3840 × 2160.");
        if(segments.Count is <1 or >64)throw new InvalidDataException("Keep between 1 and 64 sections.");
        double previousEnd=0;
        foreach(var segment in segments)
        {
            if(!double.IsFinite(segment.Start)||!double.IsFinite(segment.End)||segment.Start<previousEnd||
                segment.End<=segment.Start||segment.End>info.Duration+.001)
                throw new InvalidDataException("The selected sections must be in order and inside the source clip.");
            if(segment.Crop is {} crop)_=crop.Pixels(source);
            previousEnd=segment.End;
        }
        var editable=AudioCompatibility.Editable(info.Audio);
        var selected=keepAudio is null?editable:editable.Where(track=>keepAudio.Contains(track.Ordinal)).ToArray();
        double expected=segments.Sum(segment=>segment.End-segment.Start);
        string id=Guid.NewGuid().ToString("N")[..8];
        string root=Path.GetDirectoryName(input)!;
        string name=Path.GetFileNameWithoutExtension(input);
        string temp=Path.Combine(root,$"{name}_{id}.saving.mp4");
        string output=overwrite?input:Path.Combine(root,$"{name}_edit_{id}.mp4");
        try
        {
            await EncodeEditAsync(input,info,outputSize,segments,selected,expected,temp,progress,cancellation);
            progress?.Report("Checking saved edit…");
            await AudioCompatibility.EnsureMixAsync(temp,cancellation);
            var result=await ProbeAsync(temp,cancellation);
            if(result.Width!=outputSize.Width||result.Height!=outputSize.Height||
                result.Audio.Length!=selected.Length+(selected.Length>1?1:0))
                throw new InvalidDataException("The edit could not be verified. The original was preserved.");
            // A cut can start between two frames. The allowance is one short frame interval per join,
            // plus container padding, so the check still catches a dropped or duplicated section.
            double tolerance=.1+segments.Count*.04;
            if(Math.Abs(result.Duration-expected)>tolerance)
                throw new InvalidDataException($"The edit is {result.Duration:F2}s. Expected {expected:F2}s.");
            if(overwrite)
            {
                progress?.Report("Validating video before replacing the original…");
                await ReplayEngine.RunToolAsync("ffmpeg",["-v","error","-xerror","-nostdin","-threads","2","-i",temp,
                    "-map","0:v?","-map","0:a?","-f","null","-"],TimeSpan.FromMinutes(15),cancellation);
                await WaitForFileReleaseAsync(input,TimeSpan.FromSeconds(8),cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            before.Refresh();
            if(!before.Exists||before.Length!=length||before.LastWriteTimeUtc!=modified)
                throw new IOException("The source clip changed while editing. It has been preserved.");
            if(overwrite)
            {
                string backup=input+"."+id+".bak";
                progress?.Report("Replacing original…");
                var wait=Stopwatch.StartNew();
                while(true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try{File.Replace(temp,input,backup);break;}
                    catch(IOException e) when((e.HResult&0xffff) is 32 or 33)
                    {
                        if(wait.Elapsed>TimeSpan.FromSeconds(5))
                            throw new IOException("The original is still open in another program. It was preserved.",e);
                        await Task.Delay(100,cancellation);
                        before.Refresh();
                        if(!before.Exists||before.Length!=length||before.LastWriteTimeUtc!=modified)
                            throw new IOException("The source clip changed while editing. It has been preserved.");
                    }
                }
                try{File.Delete(ReplayEngine.ThumbnailPath(input));File.Delete(backup);}
                catch(Exception e) when(e is IOException or UnauthorizedAccessException)
                {PrivateLog.Write("clips.log","Edit cleanup: "+e.GetType().Name);}
            }
            else File.Move(temp,output);
            try
            {
                await File.WriteAllTextAsync(ReplayEngine.MetadataPath(output),JsonSerializer.Serialize(new{
                    State="Edit saved",FullDecode="Pending",Sections=segments,Width=result.Width,Height=result.Height,
                    KeptAudioTracks=selected.Select(track=>track.Title),ActualSeconds=result.Duration,Created=DateTimeOffset.Now}));
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException)
            {PrivateLog.Write("clips.log","Edit metadata: "+e.GetType().Name);}
            return output;
        }
        finally
        {
            try{File.Delete(temp);}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException)
            {PrivateLog.Write("clips.log","Edit temporary file: "+e.GetType().Name);}
        }
    }

    private static string Seconds(double value)=>value.ToString("0.######",CultureInfo.InvariantCulture);

    private static async Task EncodeEditAsync(string input,Info info,VideoSize outputSize,IReadOnlyList<EditSegment> segments,
        AudioTrack[] audio,double duration,string output,IProgress<string>? progress,CancellationToken cancellation)
    {
        var filters=new List<string>();
        var inputs=new List<string>();
        var source=new VideoSize(info.Width,info.Height);
        for(int i=0;i<segments.Count;i++)
        {
            var segment=segments[i];
            string range=$"start={Seconds(segment.Start)}:end={Seconds(segment.End)}";
            string cropFilter="";
            if(segment.Crop is {} crop)
            {
                var pixels=crop.Pixels(source);
                cropFilter=$",crop={pixels.Width}:{pixels.Height}:{pixels.X}:{pixels.Y}";
            }
            string video=$"[v{i}]";
            filters.Add($"[0:v:0]trim={range},setpts=PTS-STARTPTS{cropFilter},"+
                $"scale={outputSize.Width}:{outputSize.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1,"+
                $"pad={outputSize.Width}:{outputSize.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1{video}");
            inputs.Add(video);
            for(int lane=0;lane<audio.Length;lane++)
            {
                string label=$"[a{i}_{lane}]";
                filters.Add($"[0:{audio[lane].Stream}]apad=whole_dur={Seconds(info.Duration)},atrim={range},asetpts=PTS-STARTPTS{label}");
                inputs.Add(label);
            }
        }
        string videoOutput="[outv]";
        var audioOutputs=Enumerable.Range(0,audio.Length).Select(i=>$"[outa{i}]").ToArray();
        filters.Add(string.Concat(inputs)+$"concat=n={segments.Count}:v=1:a={audio.Length}"+videoOutput+string.Concat(audioOutputs));
        string graphPath=output+".filters";
        var args=new List<string>{"-v","error","-nostdin","-nostats","-progress","pipe:1","-threads","2","-i",input,
            "-filter_complex_threads","2","-/filter_complex",graphPath,"-map",videoOutput};
        for(int i=0;i<audio.Length;i++)
        {
            args.AddRange(["-map",audioOutputs[i]]);
            AudioCompatibility.Metadata(args,i,audio[i].Title,i==0);
        }
        args.AddRange(["-c:v","libx264","-preset","veryfast","-crf","18","-threads:v","2","-pix_fmt","yuv420p",
            "-fps_mode","passthrough","-c:a","aac","-b:a","192k","-movflags","+faststart",output]);
        progress?.Report("Exporting edit…");
        try
        {
            await File.WriteAllTextAsync(graphPath,string.Join(";",filters),cancellation);
            await ReplayEngine.RunToolAsync("ffmpeg",args,TimeSpan.FromMinutes(15),cancellation,line=>{
                const string prefix="out_time_us=";
                if(progress is not null&&line.StartsWith(prefix,StringComparison.Ordinal)&&
                    long.TryParse(line.AsSpan(prefix.Length),CultureInfo.InvariantCulture,out long microseconds))
                    progress.Report($"Exporting edit… {(int)Math.Clamp(microseconds/(duration*10_000),0,99)}%");
            });
        }
        finally
        {
            try{File.Delete(graphPath);}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException)
            {PrivateLog.Write("clips.log","Edit filter cleanup: "+e.GetType().Name);}
        }
    }
}
