using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Rewind;
// Separate bounded audio history. No changes to native video capture, leases, or synchronization.
internal sealed class AudioReplay : IDisposable
{
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
        internal bool Input,Muted,Available;
        internal AudioEndpointVolumeNotificationDelegate? VolumeNotice;
        internal string Title="Audio";
        public void Dispose(){Enabled=false;Available=false;if(Device is not null&&VolumeNotice is not null)Device.AudioEndpointVolume.OnVolumeNotification-=VolumeNotice;Capture?.StopRecording();Capture?.Dispose();Device?.Dispose();Capture=null;Device=null;}
    }
    private readonly Track desktop=new(),mic=new();
    private readonly object matrixGate=new();
    private readonly Dictionary<string,Track> extra=new();
    internal event Action? StateChanged;
    internal bool MicrophoneLive{get{lock(matrixGate)return new[]{mic}.Concat(extra.Values).Any(t=>t.Input&&t.Enabled&&t.Available&&!t.Muted);}}
    private readonly Stopwatch clock;
    private int history;
    internal AudioReplay(Stopwatch clock,int seconds){this.clock=clock;history=seconds;}
    internal void SetHistory(int seconds)=>history=Math.Clamp(seconds,1,300);
    internal static string[] Microphones(){using var devices=new MMDeviceEnumerator();var result=new List<string>();foreach(var device in devices.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active))using(device)result.Add(device.ID+"|"+device.FriendlyName);return result.ToArray();}
    internal void SetEnabled(bool output,bool input,string? inputId)
    {
        SetTrack(desktop,output,false,null);SetTrack(mic,input,true,inputId);StateChanged?.Invoke();
    }
    private void SetTrack(Track track,bool enabled,bool input,string? id)
    {
        track.Enabled=enabled;track.Input=input;if(!enabled||track.Capture is not null)return;
        using var devices=new MMDeviceEnumerator();
        track.Device=input?(string.IsNullOrEmpty(id)?devices.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Communications):devices.GetDevice(id)):(id is not null?devices.GetDevice(id):devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia));
        track.Title=input?"Microphone":"Desktop Audio";track.Muted=track.Device.AudioEndpointVolume.Mute;track.VolumeNotice=value=>{track.Muted=value.Muted;StateChanged?.Invoke();};track.Device.AudioEndpointVolume.OnVolumeNotification+=track.VolumeNotice;
        track.Capture=input?new WasapiCapture(track.Device):new WasapiLoopbackCapture(track.Device);
        track.Format=new WaveFormat(48000,16,input?1:2);track.Capture.WaveFormat=track.Format;
        track.Capture.DataAvailable+=(_,e)=>{
            int count=e.BytesRecorded;if(count<=0)return;byte[] data=new byte[count];if(track.Enabled&&!track.Muted)Buffer.BlockCopy(e.Buffer,0,data,0,count);
            double duration=count/(double)track.Format.AverageBytesPerSecond;
            lock(track.Gate){
                double proposed=clock.Elapsed.TotalSeconds-duration;
                double start=Math.Abs(proposed-track.LastEnd)<0.15?track.LastEnd:Math.Max(track.LastEnd,proposed);
                var packet=new Packet(start,data);track.Packets.Enqueue(packet);track.Latest=packet;track.LastEnd=start+duration;
                while(track.Packets.Count>0 && track.Packets.Peek().Start<clock.Elapsed.TotalSeconds-history-20)track.Packets.Dequeue();
            }
        };
        track.Capture.RecordingStopped+=(_,_)=>{track.Available=false;StateChanged?.Invoke();};
        try{track.Capture.StartRecording();track.Available=true;}catch{track.Dispose();throw;}
    }
    internal sealed record Endpoint(string Id,string Title,bool IsInput);
    internal static Endpoint[] Endpoints(){using var devices=new MMDeviceEnumerator();var items=new List<Endpoint>();foreach(var flow in new[]{DataFlow.Render,DataFlow.Capture})foreach(var device in devices.EnumerateAudioEndPoints(flow,DeviceState.Active))using(device)items.Add(new(device.ID,device.FriendlyName,flow==DataFlow.Capture));return items.ToArray();}
    internal void SetSources(IReadOnlyList<AudioSourceSetting> sources){lock(matrixGate){var wanted=sources.DistinctBy(s=>s.Id).Take(6).Where(s=>s.Id!=desktop.Device?.ID&&s.Id!=mic.Device?.ID).ToArray();foreach(string id in extra.Keys.Where(id=>!wanted.Any(s=>s.Id==id)).ToArray()){extra[id].Dispose();extra.Remove(id);}foreach(var source in wanted){if(extra.ContainsKey(source.Id))continue;var track=new Track();try{SetTrack(track,true,source.IsInput,source.Id);track.Title=source.Title.Length>160?source.Title[..160]:source.Title;extra.Add(source.Id,track);}catch(Exception e){track.Dispose();PrivateLog.Write("audio.log",source.Title+": "+e.Message);}}}StateChanged?.Invoke();}
    internal sealed record Meter(string Id,string Title,double Peak,bool Enabled,bool Muted,bool Available);
    internal Meter[] Meters(){(string Id,Track Track,string Title)[] tracks;lock(matrixGate)tracks=new[]{("desktop",desktop,"Desktop Audio"),("mic",mic,"Microphone")}.Concat(extra.Select(p=>(p.Key,p.Value,p.Value.Title))).ToArray();return tracks.Select(item=>{var t=item.Track;Packet? packet;lock(t.Gate)packet=t.Latest;double peak=0;if(t.Enabled&&!t.Muted&&packet is not null&&clock.Elapsed.TotalSeconds-t.LastEnd<.5){byte[] data=packet.Bytes;for(int i=0;i+1<data.Length;i+=32)peak=Math.Max(peak,Math.Abs((int)(short)(data[i]|data[i+1]<<8))/32768d);}return new Meter(item.Id,item.Title,peak,t.Enabled,t.Muted,t.Available);}).ToArray();}
    internal void MuteLane(string id,bool muted){lock(matrixGate){Track? track=id=="desktop"?desktop:id=="mic"?mic:extra.GetValueOrDefault(id);if(track is not null)track.Enabled=!muted;}StateChanged?.Invoke();}
    internal sealed record AudioTrackFile(string Path,string Title);
    internal List<AudioTrackFile> Snapshot(double start,double seconds,string prefix)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);var files=new List<AudioTrackFile>();int index=0;
        (Track,string)[] tracks;lock(matrixGate)tracks=new[]{(desktop,"Desktop Audio"),(mic,"Microphone")}.Concat(extra.Values.Select(t=>(t,t.Title))).ToArray();
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
