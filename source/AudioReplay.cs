using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Rewind;
// Separate bounded audio history. No changes to native video capture, leases, or synchronization.
internal sealed class AudioReplay : IDisposable
{
    private sealed class MixerHiddenLoopbackCapture(MMDevice device) : WasapiLoopbackCapture(device)
    {
        // AUDCLNT_SESSIONFLAGS_DISPLAY_HIDE applies when WASAPI creates the session.
        private const AudioClientStreamFlags DisplayHide=(AudioClientStreamFlags)0x20000000;

        protected override AudioClientStreamFlags GetAudioClientStreamFlags()
            =>base.GetAudioClientStreamFlags()|DisplayHide;
    }

    private sealed record Packet(double Start,byte[] Bytes);
    private sealed class Track : IDisposable
    {
        internal readonly object Gate=new();
        internal readonly Queue<Packet> Packets=new();
        internal WasapiCapture? Capture;
        internal MMDevice? Device;
        internal WaveFormat Format=new(48000,16,2);
        internal volatile bool Enabled;
        internal double LastEnd;
        internal Packet? Latest;
        internal bool Input;
        internal volatile bool Muted,Available;
        internal (int Index,Track Lane)[] Channels=[];
        internal AudioEndpointVolumeNotificationDelegate? VolumeNotice;
        internal string Title="Audio";
        public void Dispose(){Enabled=false;Available=false;if(Device is not null&&VolumeNotice is not null)Device.AudioEndpointVolume.OnVolumeNotification-=VolumeNotice;Capture?.StopRecording();Capture?.Dispose();Device?.Dispose();Capture=null;Device=null;}
    }
    private readonly Track desktop=new(){Title="Desktop Audio"},mic=new(){Title="Microphone",Input=true};
    private readonly object matrixGate=new();
    private readonly Dictionary<string,Track> extra=new(StringComparer.OrdinalIgnoreCase);
    private bool microphoneIsSplit;
    internal event Action? StateChanged;
    internal bool MicrophoneLive{get{lock(matrixGate)return Lanes().Any(l=>l.Device.Input&&l.Lane.Enabled&&l.Device.Available&&!l.Device.Muted);}}
    private readonly Stopwatch clock;
    private int history;
    internal AudioReplay(Stopwatch clock,int seconds){this.clock=clock;history=seconds;}
    internal void SetHistory(int seconds)=>history=Math.Clamp(seconds,1,300);
    internal static string[] Microphones(){using var devices=new MMDeviceEnumerator();var result=new List<string>();foreach(var device in devices.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active))using(device)result.Add(device.ID+"|"+device.FriendlyName);return result.ToArray();}
    internal void Configure(bool output,bool input,string? inputId,IReadOnlyList<AudioSourceSetting> sources)
    {
        var selected=AudioSourceSetting.Normalize(sources);
        string? microphoneId=inputId;
        if(microphoneId is null&&selected.Any(s=>s.Channels is not null))
        {
            try
            {
                using var enumerator=new MMDeviceEnumerator();
                using var device=enumerator.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Communications);
                microphoneId=device.ID;
            }
            catch(System.Runtime.InteropServices.COMException){}
        }

        Exception? defaultFailure=null;
        lock(matrixGate)
        {
            microphoneIsSplit=selected.Any(s=>s.Channels is not null&&string.Equals(s.Id,microphoneId,StringComparison.OrdinalIgnoreCase));
            try{SetTrack(desktop,output,false,null);}
            catch(Exception e){defaultFailure=e;}
            try{SetTrack(mic,input&&!microphoneIsSplit,true,inputId);}
            catch(Exception e){defaultFailure??=e;}
            foreach(var source in selected)
            {
                if(string.Equals(source.Id,desktop.Device?.ID,StringComparison.OrdinalIgnoreCase)||
                    string.Equals(source.Id,mic.Device?.ID,StringComparison.OrdinalIgnoreCase))continue;
                var track=new Track{Title=source.Title,Input=source.IsInput,Enabled=true,
                    Channels=source.Channels?.Select(c=>(c.Index,new Track{Title=c.Name??$"Input {c.Index+1}",Enabled=true})).ToArray()??[]};
                try{SetTrack(track,true,source.IsInput,source.Id);}
                catch(Exception e){PrivateLog.Write("audio.log",e.Message);}
                extra.Add(source.Id,track);
            }
        }
        StateChanged?.Invoke();
        if(defaultFailure is not null)throw new IOException(defaultFailure.Message,defaultFailure);
    }

    internal void SetEnabled(bool output,bool input,string? inputId)
    {
        lock(matrixGate)
        {
            SetTrack(desktop,output,false,null);
            SetTrack(mic,input&&!microphoneIsSplit,true,inputId);
        }
        StateChanged?.Invoke();
    }

    private void SetTrack(Track track,bool enabled,bool input,string? id)
    {
        track.Enabled=enabled;track.Input=input;
        if(!enabled||track.Capture is not null)return;
        try
        {
            using var devices=new MMDeviceEnumerator();
            track.Device=id is not null?devices.GetDevice(id):input
                ?devices.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Communications)
                :devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
            track.Muted=track.Device.AudioEndpointVolume.Mute;
            track.VolumeNotice=value=>{track.Muted=value.Muted;StateChanged?.Invoke();};
            track.Device.AudioEndpointVolume.OnVolumeNotification+=track.VolumeNotice;
            var capture=input?new WasapiCapture(track.Device):new MixerHiddenLoopbackCapture(track.Device);
            track.Capture=capture;
            if(track.Channels.Length==0)
            {
                track.Format=new WaveFormat(48000,16,input?1:2);
                capture.WaveFormat=track.Format;
                capture.DataAvailable+=(_,e)=>{
                    if(e.BytesRecorded<=0)return;
                    var data=new byte[e.BytesRecorded];
                    if(track.Enabled&&!track.Muted)Buffer.BlockCopy(e.Buffer,0,data,0,data.Length);
                    StorePacket(track,data,clock.Elapsed.TotalSeconds);
                };
            }
            else
            {
                using var client=track.Device.AudioClient;
                var format=client.MixFormat;
                if(track.Channels.Any(c=>c.Index>=format.Channels))
                    throw new IOException("A selected input channel is no longer available.");
                var splitter=new AudioInputChannels(format);
                capture.WaveFormat=format;
                foreach(var (_,lane) in track.Channels)lane.Format=splitter.MonoFormat;
                capture.DataAvailable+=(_,e)=>StoreChannels(track,splitter,e.Buffer,e.BytesRecorded);
            }
            capture.RecordingStopped+=(_,_)=>{track.Available=false;StateChanged?.Invoke();};
            track.Available=true;
            capture.StartRecording();
        }
        catch
        {
            track.Dispose();
            throw;
        }
    }

    private void StoreChannels(Track track,AudioInputChannels splitter,byte[] buffer,int bytes)
    {
        int frames=splitter.FrameCount(bytes);
        if(frames==0)return;
        double end=clock.Elapsed.TotalSeconds;
        bool muted=track.Muted;
        foreach(var (index,lane) in track.Channels)
        {
            var data=new byte[frames*splitter.MonoFormat.BlockAlign];
            if(lane.Enabled&&!muted)splitter.CopyChannel(buffer.AsSpan(0,bytes),index,data);
            StorePacket(lane,data,end);
        }
    }

    private void StorePacket(Track track,byte[] data,double end)
    {
        double duration=data.Length/(double)track.Format.AverageBytesPerSecond;
        lock(track.Gate)
        {
            double proposed=end-duration;
            double start=Math.Abs(proposed-track.LastEnd)<.15?track.LastEnd:Math.Max(track.LastEnd,proposed);
            var packet=new Packet(start,data);
            track.Packets.Enqueue(packet);
            track.Latest=packet;
            track.LastEnd=start+duration;
            while(track.Packets.Count>0&&track.Packets.Peek().Start<end-history-20)track.Packets.Dequeue();
        }
    }

    internal sealed record Endpoint(string Id,string Title,bool IsInput,int Channels);
    internal static Endpoint[] Endpoints()
    {
        using var devices=new MMDeviceEnumerator();
        var items=new List<Endpoint>();
        foreach(var flow in new[]{DataFlow.Render,DataFlow.Capture})
        foreach(var device in devices.EnumerateAudioEndPoints(flow,DeviceState.Active))
        using(device)
        {
            bool input=flow==DataFlow.Capture;
            int channels=1;
            if(input)
            {
                try
                {
                    using var client=device.AudioClient;
                    var format=client.MixFormat;
                    _=new AudioInputChannels(format);
                    channels=Math.Min(format.Channels,AudioSourceSetting.MaximumChannels);
                }
                catch(Exception e) when(e is NotSupportedException or System.Runtime.InteropServices.COMException){}
            }
            items.Add(new(device.ID,device.FriendlyName,input,channels));
        }
        return items.ToArray();
    }

    // Call under matrixGate. A split endpoint owns one device and several independent lanes.
    private IEnumerable<(string Id,Track Lane,Track Device)> Lanes()
    {
        yield return ("desktop",desktop,desktop);
        if(!microphoneIsSplit)yield return ("mic",mic,mic);
        foreach(var (id,device) in extra)
        {
            if(device.Channels.Length==0){yield return (id,device,device);continue;}
            foreach(var (index,lane) in device.Channels)yield return (id+"#"+index,lane,device);
        }
    }

    internal sealed record Meter(string Id,string Title,double Peak,bool Enabled,bool Muted,bool Available);
    internal Meter[] Meters()
    {
        (string Id,Track Lane,Track Device)[] tracks;
        lock(matrixGate)tracks=Lanes().ToArray();
        return tracks.Select(item=>{
            var (id,lane,device)=item;
            Packet? packet;double lastEnd;
            lock(lane.Gate){packet=lane.Latest;lastEnd=lane.LastEnd;}
            double peak=0;
            if(lane.Enabled&&!device.Muted&&device.Available&&packet is not null&&clock.Elapsed.TotalSeconds-lastEnd<.5)
            {
                byte[] data=packet.Bytes;
                for(int i=0;i+1<data.Length;i+=32)peak=Math.Max(peak,Math.Abs((int)(short)(data[i]|data[i+1]<<8))/32768d);
            }
            return new Meter(id,lane.Title,peak,lane.Enabled,device.Muted,device.Available);
        }).ToArray();
    }

    internal void MuteLane(string id,bool muted)
    {
        lock(matrixGate)
        {
            var track=Lanes().FirstOrDefault(l=>l.Id==id).Lane;
            if(track is not null)track.Enabled=!muted;
        }
        StateChanged?.Invoke();
    }
    internal sealed record AudioTrackFile(string Path,string Title);
    internal List<AudioTrackFile> Snapshot(double start,double seconds,string prefix)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);var files=new List<AudioTrackFile>();int index=0;
        (Track,string)[] tracks;lock(matrixGate)tracks=Lanes().Select(t=>(t.Lane,t.Lane.Title)).ToArray();
        foreach(var (track,title) in tracks){
            Packet[] packets;lock(track.Gate)packets=track.Packets.ToArray();if(packets.Length==0)continue;
            string file=prefix+"-audio"+(index++)+".wav";
            using var writer=new WaveFileWriter(file,track.Format);
            int align=track.Format.BlockAlign;long total=(long)(seconds*track.Format.AverageBytesPerSecond)/align*align,position=0;
            byte[] silence=new byte[8192];
            foreach(var packet in packets){
                long offset=(long)((packet.Start-start)*track.Format.AverageBytesPerSecond)/align*align;
                long skip=Math.Max(0,position-offset),at=Math.Max(position,offset);if(skip>=packet.Bytes.Length||at>=total)continue;
                while(position<at){int n=(int)Math.Min(silence.Length,at-position);writer.Write(silence,0,n);position+=n;}
                int count=(int)Math.Min(packet.Bytes.Length-skip,total-position);writer.Write(packet.Bytes,(int)skip,count);position+=count;
            }
            while(position<total){int n=(int)Math.Min(silence.Length,total-position);writer.Write(silence,0,n);position+=n;}
            files.Add(new(file,title));
        }
        return files;
    }
    public void Dispose(){lock(matrixGate){desktop.Dispose();mic.Dispose();foreach(var track in extra.Values)track.Dispose();extra.Clear();}}
}
