using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Rewind;

internal sealed record CaptureSettings(string Profile, int Seconds, bool Desktop, bool Mic, string? InputId, string? DisplayId, string CaptureApi="Native Desktop",string Codec="h264",IReadOnlyList<AudioSourceSetting>? AudioSources=null,int FrameRate=60);
internal sealed class ReplayEngine : IDisposable
{
    private Process? nativeWorker;
    private Task? nativePump,nativeErrors;
    private long restartAfterTicks;
    internal DateTime RestartAfterUtc=>new(Interlocked.Read(ref restartAfterTicks),DateTimeKind.Utc);
    private AudioReplay? audio;
    private readonly Stopwatch sessionClock=new();
    private double videoEpoch=-1;
    private readonly SemaphoreSlim validationGate=new(1,1);
    private readonly CancellationTokenSource lifetime=new();
    private readonly SemaphoreSlim thumbnailGate=new(2,2);
    private readonly ClipCatalog catalog=new();
    private readonly object requestGate=new();
    private readonly Dictionary<Guid,Task<string>> saveRequests=new();
    internal IReadOnlyList<ClipCard> Gallery=>catalog.Snapshot;
    internal Task RefreshGalleryAsync()=>catalog.RefreshAsync();
    internal static string ThumbnailPath(string clip)=>ClipCatalog.ThumbnailPath(clip);
    internal ReplayEngine(){catalog.Changed+=NotifyClipsChanged;}
    private readonly List<Task> validationTasks=new();
    private readonly HashSet<string> previewRequests=new();
    private Mp4ReplayBuffer? buffer;
    private readonly SemaphoreSlim saveGate = new(1,1);
    private CancellationTokenSource? fullCheck;
    private CaptureSettings? settings;
    public bool Active { get; private set; }
    public event Action<string>? Changed;
    internal event Action? AudioChanged;
    internal bool MicrophoneLive=>audio?.MicrophoneLive==true;
    internal string EncoderName {get;private set;}="Not initialized";
    internal CapturePlan? Plan {get;private set;}
    internal AudioReplay.Meter[] AudioMeters()=>audio?.Meters()??Array.Empty<AudioReplay.Meter>();
    internal void MuteAudioLane(string id,bool muted)=>audio?.MuteLane(id,muted);
    public static string DataFolder => LocalPaths.Require(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FLICK"));
    public static string SaveFolder => LocalPaths.Require(
        StorageLocations.Current);
    public string Status { get; private set; } = "Primary monitor replay is paused.";
    public (double Seconds,long Bytes) Statistics => buffer?.Statistics ?? (0,0);
    internal CaptureSettings? CurrentSettings=>settings;
    internal long EncodedBytes=>buffer?.Length??0;
    internal bool IsSaving=>saveGate.CurrentCount==0;
    internal bool PlaybackActive {get;set;}
    internal static string MetadataPath(string clip)
    {
        string directory=Path.Combine(DataFolder,"clip-metadata");Directory.CreateDirectory(directory);
        string id=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(clip).ToUpperInvariant())))[..24];
        return Path.Combine(directory,id+".json");
    }

    public static string Tool(string name)
    {
        var path=Path.Combine(AppContext.BaseDirectory,"tools",name+".exe");
        if(!File.Exists(path))throw new FileNotFoundException("FLICK's video tools are missing. Extract the entire release ZIP into one folder, including tools, then run FLICK.exe.",path);
        SecurityBootstrap.Verify(path);return path;
    }
    internal static void RequireVideoTools(){
        foreach(string name in new[]{"ffmpeg","ffprobe","rewind-desktop-capture"})_=Tool(name);
        string bridge=Path.Combine(AppContext.BaseDirectory,"tools","rewind_desktop_bridge.dll");
        if(!File.Exists(bridge))throw new FileNotFoundException("The capture bridge is missing. Extract the entire release ZIP, including tools.",bridge);
        SecurityBootstrap.Verify(bridge);
    }
    private void Report(string message)
    {
        Status=message;
        try { Directory.CreateDirectory(DataFolder); File.AppendAllText(Path.Combine(DataFolder,"rewind.log"),$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); } catch(IOException){} catch(UnauthorizedAccessException){}
        Changed?.Invoke(message);
    }
    public void Start(CaptureSettings chosen)
    {
        if(chosen.CaptureApi=="Native Desktop"){StartNative(chosen);return;}
        throw new InvalidOperationException("Unsupported capture mode.");
    }
    private void StartNative(CaptureSettings chosen)
    {
        if(Active || nativeWorker is not null)throw new InvalidOperationException("Replay is already running.");
        RequireVideoTools();
        if(!string.IsNullOrEmpty(chosen.DisplayId)&&chosen.DisplayId!="primary")throw new InvalidOperationException("Desktop capture supports the primary display only.");
        chosen=chosen with{Seconds=MemoryPolicy.ClampSeconds(chosen.Seconds,chosen.Profile,MemoryPolicy.Available())};
        var display=DxgiDisplays.Resolve(null);var plan=QualityProfiles.Plan(chosen.Profile,chosen.FrameRate,display.Width,display.Height);Plan=plan;
        var encoder=EncoderCatalog.Detect(chosen.Codec);EncoderName=encoder.Name;
        settings=chosen;buffer?.Dispose();buffer=new Mp4ReplayBuffer{HistorySeconds=chosen.Seconds};
        sessionClock.Restart();videoEpoch=-1;
        audio=new AudioReplay(sessionClock,chosen.Seconds);audio.StateChanged+=()=>AudioChanged?.Invoke();
        try{audio.SetEnabled(chosen.Desktop,chosen.Mic,chosen.InputId);audio.SetSources(chosen.AudioSources??Array.Empty<AudioSourceSetting>());}catch(Exception e){Report("Audio unavailable: "+e.Message);}
        buffer.Updated+=()=>{if(videoEpoch<0 && buffer.Statistics.Seconds>0){var snap=buffer.TakeSnapshot(1);videoEpoch=sessionClock.Elapsed.TotalSeconds-snap.Origin-snap.Seconds;}};
        var launch=new ProcessStartInfo(Tool(QualityProfiles.Worker(plan))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in DesktopCapture.Arguments(width:plan.SourceWidth,height:plan.SourceHeight,spinUs:0,postFilter:QualityProfiles.Filter(plan,encoder),fps:plan.Fps).Concat(QualityProfiles.EncoderArguments(plan,encoder)))launch.ArgumentList.Add(arg);
        PrivateLog.Write("encoder.log",$"Selected {encoder.Name}; adapter {encoder.Adapter}; CPU fallback {encoder.Cpu}");
        if(encoder.Cpu)Report("Software encoding active; CPU usage may increase.");
        try{
            nativeWorker=Process.Start(launch)??throw new IOException("Could not start desktop capture.");
            var worker=nativeWorker;var ring=buffer;
            nativePump=worker.StandardOutput.BaseStream.CopyToAsync(ring);
            nativeErrors=Task.Run(async()=>{string? line;while((line=await worker.StandardError.ReadLineAsync()) is not null){PrivateLog.Write("capture.log",line);}await worker.WaitForExitAsync();if(worker.ExitCode!=0){Interlocked.Exchange(ref restartAfterTicks,DateTime.UtcNow.AddSeconds(2).Ticks);EncoderCatalog.Reset();}Active=false;Report(worker.ExitCode==0?"Replay stopped.":"Capture interrupted; waiting to rebuild replay.");});
            Active=true;Report("Replay is buffering.");
        }catch{nativeWorker?.Dispose();nativeWorker=null;audio.Dispose();audio=null;throw;}
    }
    public void SetHistory(int seconds){if(Active)throw new InvalidOperationException("Pause replay to change the clip length.");if(buffer is not null)buffer.HistorySeconds=seconds;audio?.SetHistory(seconds);}
    internal void SetAudioSources(IReadOnlyList<AudioSourceSetting> sources){audio?.SetSources(sources);if(settings is not null)settings=settings with{AudioSources=sources};}
    public void SetAudio(bool desktop,bool mic)
    {
        if(nativeWorker is not null){audio?.SetEnabled(desktop,mic,settings?.InputId);return;}
    }
    public async Task StopAsync()
    {
        if(nativeWorker is not null){
            var worker=nativeWorker;DesktopCapture.StopAcquisition(worker);
            try{if(!worker.HasExited){await worker.StandardInput.WriteLineAsync("q");await worker.StandardInput.FlushAsync();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));await worker.WaitForExitAsync(deadline.Token);}if(nativePump is not null)await nativePump;if(nativeErrors is not null)await nativeErrors;}
            catch(OperationCanceledException){if(!worker.HasExited){worker.Kill(true);await worker.WaitForExitAsync();}Report("Capture required a forced stop.");}
            finally{nativeWorker=null;worker.Dispose();audio?.Dispose();audio=null;Active=false;AudioChanged?.Invoke();Changed?.Invoke("Replay paused");}return;
        }
    }

    public Task<string> SaveAsync(int seconds,Guid? requestId=null){
        if(requestId is null)return SaveCoreAsync(seconds,Guid.NewGuid());
        lock(requestGate){if(saveRequests.TryGetValue(requestId.Value,out var known))return known;
            foreach(var key in saveRequests.Where(p=>p.Value.IsCompleted).Select(p=>p.Key).Take(Math.Max(0,saveRequests.Count-15)).ToArray())saveRequests.Remove(key);
            return saveRequests[requestId.Value]=SaveCoreAsync(seconds,requestId.Value);
        }
    }
    private async Task<string> SaveCoreAsync(int seconds,Guid requestId)
    {
        if(!await saveGate.WaitAsync(0))throw new InvalidOperationException("A clip is already saving. Try again after it finishes.");
        string? raw=null,output=null;
        try
        {
            if(!Active)throw new InvalidOperationException("Resume replay before saving a clip.");
            var source=buffer??throw new InvalidOperationException("Replay has not started.");
            var snapshot=source.TakeSnapshot(seconds);
            double exportSeconds=Math.Min(snapshot.Seconds,Math.Clamp(seconds,1,300));
            string saveFolder=SaveFolder;Directory.CreateDirectory(saveFolder);
            string id=DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")+"_"+Guid.NewGuid().ToString("N")[..6];
            string final=Path.Combine(saveFolder,id+".mp4");
            raw=Path.Combine(saveFolder,id+".recovery.fmp4");
            output=Path.Combine(saveFolder,id+".saving.mp4");
            long bytes=snapshot.Header.Length+snapshot.Fragments.Sum(f=>(long)f.Moof.Length+f.Mdat.Length);
            var drive=new DriveInfo(Path.GetPathRoot(saveFolder)!);
            if(drive.AvailableFreeSpace<bytes*2+64*1024*1024)throw new IOException("Insufficient free space for a safe save. Free space and retry.");
            Report("Saving replay in background…");
            await source.WriteSnapshotAsync(snapshot,raw);
            var mux=new List<string>{"-hide_banner","-loglevel","error","-nostdin","-i",raw};
            var audioFiles=new List<AudioReplay.AudioTrackFile>();
            var capturedAudio=audio;
            if(capturedAudio is not null && videoEpoch>=0)audioFiles=capturedAudio.Snapshot(Math.Max(0,videoEpoch+snapshot.Origin),exportSeconds,Path.Combine(DataFolder,id));
            foreach(var track in audioFiles){mux.Add("-i");mux.Add(track.Path);}
            mux.AddRange(new[]{"-map","0:v:0","-c:v","copy"});
            AudioCompatibility.MapSavedAudio(mux,audioFiles);
            mux.AddRange(new[]{"-t",exportSeconds.ToString("0.######",System.Globalization.CultureInfo.InvariantCulture),"-avoid_negative_ts","make_zero","-movflags","+faststart",output});
            try{await RunToolAsync("ffmpeg",mux,TimeSpan.FromSeconds(90));}finally{foreach(var track in audioFiles)File.Delete(track.Path);}
            string probe=await RunToolAsync("ffprobe",["-v","error","-show_entries","format=duration:stream=codec_type,codec_name","-of","json",output],TimeSpan.FromSeconds(20));
            using var info=JsonDocument.Parse(probe);
            bool video=info.RootElement.GetProperty("streams").EnumerateArray().Any(s=>s.GetProperty("codec_type").GetString()=="video");
            double actual=double.Parse(info.RootElement.GetProperty("format").GetProperty("duration").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
            if(!video || !double.IsFinite(actual) || actual<=0 || actual>Math.Min(snapshot.Seconds,Math.Clamp(seconds,1,300))+.1)throw new InvalidDataException("Saved clip duration/streams failed validation. Recovery file retained.");
            File.Move(output,final);
            await File.WriteAllTextAsync(MetadataPath(final),JsonSerializer.Serialize(new{RequestId=requestId,State="Basic checks passed",FullDecode="Pending",RequestedSeconds=seconds,ActualSeconds=actual,Settings=settings,Created=DateTimeOffset.Now},new JsonSerializerOptions{WriteIndented=true}));
            File.Delete(raw); // Only our temporary source, after the finalized file passed basic checks.
            Report($"Saved {actual:F1}s.");
            ScheduleValidation(final);
            return final;
        }
        catch(Exception e)
        {
            Report("Save failed: "+e.Message+". Any recovery/saving files are retained in Videos/FLICK.");
            throw;
        }
        finally{saveGate.Release();}
    }

    public event Action? ClipsChanged;
    internal void NotifyClipsChanged()=>ClipsChanged?.Invoke();
    internal void ScheduleValidation(string path){var task=Task.Run(async()=>{
        await EnsurePreviewAsync(path);await catalog.RefreshAsync();await ValidateSavedClipAsync(path);
    });lock(validationTasks){validationTasks.RemoveAll(t=>t.IsCompleted);validationTasks.Add(task);}}
    internal Task FinishValidationAsync(){lock(validationTasks)return Task.WhenAll(validationTasks.ToArray());}
    private async Task ValidateSavedClipAsync(string path)
    {
        await validationGate.WaitAsync();
        IDisposable? fileLease=null;
        try{
            fileLease=await ClipFiles.AcquireAsync(path);
            if(!File.Exists(path))return;
            await RunToolAsync("ffmpeg",["-hide_banner","-v","error","-xerror","-nostdin","-threads","2","-i",path,"-map","0:v?","-map","0:a?","-f","null","-"],TimeSpan.FromMinutes(15));
            
            var metadata=File.Exists(MetadataPath(path))?System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(MetadataPath(path)))!:new System.Text.Json.Nodes.JsonObject();metadata["FullDecode"]="Passed";
            // A clean decode does not establish smooth capture. Inspect packet timing
            // off the recording/UI threads; gaps are diagnostic, never corruption.
            try{
                string timing=await RunToolAsync("ffprobe",["-v","error","-select_streams","v:0","-show_entries","packet=pts_time","-of","json",path],TimeSpan.FromSeconds(30));
                using var packets=JsonDocument.Parse(timing);double previous=double.NaN,largest=0,at=0;int frames=0,gaps=0,invalid=0;
                foreach(var packet in packets.RootElement.GetProperty("packets").EnumerateArray()){
                    if(!packet.TryGetProperty("pts_time",out var pts)||!double.TryParse(pts.GetString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double time)||!double.IsFinite(time)){invalid++;continue;}
                    if(double.IsFinite(previous)){double delta=time-previous;if(delta<=0)invalid++;if(delta>.1)gaps++;if(delta>largest){largest=delta;at=previous;}}
                    previous=time;frames++;
                }
                metadata["FrameTiming"]=System.Text.Json.JsonSerializer.SerializeToNode(new{Frames=frames,GapsOver100ms=gaps,LargestGapMs=largest*1000,LargestGapAtSeconds=at,NonIncreasingOrInvalid=invalid});
            }catch(Exception e){PrivateLog.Write("capture-timing.log","Timing check deferred: "+e.Message);}
            await File.WriteAllTextAsync(MetadataPath(path),metadata.ToJsonString());ClipsChanged?.Invoke();
        }catch(IOException e){
            // Keep failures recoverable; resource/decoder failures must never destroy user footage.
            if(e.Message.Contains("Invalid data",StringComparison.OrdinalIgnoreCase)||e.Message.Contains("corrupt",StringComparison.OrdinalIgnoreCase)||e.Message.Contains("Error while decoding",StringComparison.OrdinalIgnoreCase)){
                string quarantine=Path.Combine(DataFolder,"quarantine");Directory.CreateDirectory(quarantine);
                if(File.Exists(path))File.Move(path,Path.Combine(quarantine,Guid.NewGuid().ToString("N")+".mp4"));ClipsChanged?.Invoke();
            }
            Report("Clip validation: "+e.Message);
        }catch(Exception e){Report("Clip check deferred: "+e.Message);}
        finally{fileLease?.Dispose();validationGate.Release();}
    }
    private async Task CreateThumbnailAsync(string path,string thumbnail)
    {
        string directory=Path.GetDirectoryName(thumbnail)!;Directory.CreateDirectory(directory);
        string cache=Path.GetDirectoryName(directory)!;File.SetAttributes(cache,File.GetAttributes(cache)|FileAttributes.Hidden);
        string temp=thumbnail+"."+Guid.NewGuid().ToString("N")+".tmp.jpg";
        try{await RunToolAsync("ffmpeg",["-v","error","-y","-ss","0.2","-i",path,"-frames:v","1","-vf","scale=480:-2","-threads","1",temp],TimeSpan.FromSeconds(30),lifetime.Token);File.Move(temp,thumbnail,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private async Task EnsurePreviewAsync(string path){
        try{await thumbnailGate.WaitAsync(lifetime.Token);}catch(OperationCanceledException){return;}try{using var lease=await ClipFiles.AcquireAsync(path);string thumb=ThumbnailPath(path);if(File.Exists(path)&&(!File.Exists(thumb)||File.GetLastWriteTimeUtc(thumb)<File.GetLastWriteTimeUtc(path)))await CreateThumbnailAsync(path,thumb);}
        catch(Exception e){PrivateLog.Write("preview.log",e.Message);}finally{thumbnailGate.Release();}
    }
    internal void RequestPreview(string path){
        lock(previewRequests){if(!previewRequests.Add(path))return;}
        _=Task.Run(async()=>{try{await EnsurePreviewAsync(path);await catalog.RefreshAsync();}finally{lock(previewRequests)previewRequests.Remove(path);}});
    }
    public async Task CheckFileAsync(string path)
    {
        if(Active)throw new InvalidOperationException("Stop replay before a full-file check to keep decoding out of gameplay.");
        if(fullCheck is not null)throw new InvalidOperationException("A full check is already running.");
        var cancellation=new CancellationTokenSource();fullCheck=cancellation;
        try
        {
            Report("Checking every decoded frame/audio packet…");
            await RunToolAsync("ffmpeg",["-hide_banner","-v","error","-xerror","-nostdin","-threads","2","-i",path,"-map","0:v?","-map","0:a?","-f","null","-"],TimeSpan.FromMinutes(15),cancellation.Token);
            string destination=MetadataPath(path);
            string sidecar=File.Exists(destination)?destination:path+".json";
            if(File.Exists(sidecar))
            {
                var data=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(sidecar))!;
                data["FullDecode"]="Passed";await File.WriteAllTextAsync(destination,data.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
            }
            Report("Full decoding passed. This checks readability, not missing events or visual capture quality.");
        }
        catch(OperationCanceledException){Report("Full-file check deferred because replay restarted.");throw;}
        finally{fullCheck=null;cancellation.Dispose();}
    }
    public async Task FinishPendingSaveAsync()
    {
        if(!await saveGate.WaitAsync(TimeSpan.FromSeconds(120)))throw new TimeoutException("A pending save did not finish before exit.");
        saveGate.Release();
    }
    public static async Task<string> RunToolAsync(string tool,IEnumerable<string> args,TimeSpan timeout,CancellationToken cancellation=default)
    {
        var start=new ProcessStartInfo(Tool(tool)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(string arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Could not start video tool.");
        try{process.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(InvalidOperationException){}
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(timeout);
        try{await process.WaitForExitAsync(deadline.Token);}
        catch(OperationCanceledException){try{process.Kill(true);}catch(InvalidOperationException){}throw;}
        string error=await stderr,output=await stdout;
        if(process.ExitCode!=0)throw new IOException(string.IsNullOrWhiteSpace(error)?$"{tool} exited with code {process.ExitCode}":error[..Math.Min(error.Length,1800)]);
        return output;
    }
    public void Dispose(){lifetime.Cancel();catalog.Changed-=NotifyClipsChanged;fullCheck?.Cancel();if(nativeWorker is not null){DesktopCapture.StopAcquisition(nativeWorker);nativeWorker.Dispose();}audio?.Dispose();
buffer?.Dispose();}
}
