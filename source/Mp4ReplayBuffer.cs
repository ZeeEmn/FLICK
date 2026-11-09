using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Rewind;

// Keeps complete fragmented-MP4 boxes, never a ring of arbitrary video bytes.
// Incomplete writes cannot become saved fragments. Save snapshots share immutable packets.
internal sealed class Mp4ReplayBuffer : Stream
{
    internal record Track(uint Id, uint Scale, bool Video, uint DefaultFlags);
    internal record Timing(uint TrackId, ulong Time, int Position, bool Wide);
    internal record Fragment(byte[] Moof, byte[] Mdat, long OriginalOffset,
        double Start, double End, bool StartsVideoKeyframe, Timing[] Timings);
    internal record Snapshot(byte[] Header, Fragment[] Fragments, double Origin, double Seconds);
    private readonly object gate = new();
    private readonly List<byte[]> headers = [];
    private readonly List<(long Offset,byte[] Data)> prefixBoxes = [];
    private readonly Dictionary<uint, Track> tracks = [];
    private readonly Dictionary<uint,ulong> nextTimes = [];
    private readonly LinkedList<Fragment> fragments = [];
    private readonly byte[] boxHeader = new byte[16];
    private int headerRead, headerNeeded=8, incomingRead;
    private byte[]? incoming;
    private byte[]? currentMoof;
    private long moofOffset, consumed, position, retained, totalWritten;
    private int seconds = 60;
    private readonly long MaxBytes=MemoryPolicy.VideoLimit(MemoryPolicy.Available());
    public event Action? Updated;
    public int HistorySeconds { get { lock(gate) return seconds; } set { lock(gate) { seconds=Math.Clamp(value,1,300); Prune(); } } }
    public (double Seconds, long Bytes) Statistics { get { lock(gate) return (fragments.Count==0 ? 0 : Math.Max(0,fragments.Last!.Value.End-fragments.First!.Value.Start),retained); } }
    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => totalWritten;
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer,int offset,int count) => throw new NotSupportedException();
    public override long Seek(long offset,SeekOrigin origin)
    {
        long target=origin switch{SeekOrigin.Begin=>offset,SeekOrigin.Current=>position+offset,SeekOrigin.End=>Length+offset,_=>-1};
        long prefixEnd=prefixBoxes.Count==0?0:prefixBoxes[^1].Offset+prefixBoxes[^1].Data.Length;
        if(target==Length || (target>=0 && target<=prefixEnd)){position=target;return target;}
        throw new NotSupportedException("The fragmented replay writer attempted to rewrite expired media.");
    }
    public override void SetLength(long value) { if(value!=position) throw new NotSupportedException(); }

    public override void Write(byte[] buffer,int offset,int count)
    {
        bool changed=false;
        lock(gate)
        {
            if(position<Length)
            {
                int copied=0;
                while(copied<count)
                {
                    var prefix=prefixBoxes.FirstOrDefault(b=>position>=b.Offset && position<b.Offset+b.Data.Length);
                    if(prefix.Data is null)throw new NotSupportedException("Unexpected rewrite outside the MP4 header.");
                    int at=checked((int)(position-prefix.Offset)),n=Math.Min(count-copied,prefix.Data.Length-at);
                    Buffer.BlockCopy(buffer,offset+copied,prefix.Data,at,n);copied+=n;position+=n;
                }
                return;
            }
            position+=count;
            totalWritten+=count;
            int cursor=offset,end=checked(offset+count);
            while(cursor<end)
            {
                if(incoming is null)
                {
                    int n=Math.Min(headerNeeded-headerRead,end-cursor);
                    Buffer.BlockCopy(buffer,cursor,boxHeader,headerRead,n);cursor+=n;headerRead+=n;
                    if(headerRead<headerNeeded)continue;
                    uint small=U32(boxHeader,0);
                    if(small==1 && headerNeeded==8){headerNeeded=16;continue;}
                    ulong size=small==1?U64(boxHeader,8):small;
                    if(size<(ulong)headerNeeded || size>(ulong)Math.Min(64L*1024*1024,MaxBytes/2))throw new InvalidDataException("Unsupported or unbounded MP4 box size.");
                    incoming=new byte[(int)size];
                    Buffer.BlockCopy(boxHeader,0,incoming,0,headerRead);incomingRead=headerRead;
                }
                int copy=Math.Min(incoming.Length-incomingRead,end-cursor);
                Buffer.BlockCopy(buffer,cursor,incoming,incomingRead,copy);cursor+=copy;incomingRead+=copy;
                if(incomingRead==incoming.Length)
                {
                    Process(incoming,consumed);
                    consumed+=incoming.Length;incoming=null;incomingRead=0;headerRead=0;headerNeeded=8;changed=true;
                }
            }
        }
        if(changed) Updated?.Invoke();
    }

    private void Process(byte[] box,long offset)
    {
        string type=Type(box,0);
        if(fragments.Count==0 && currentMoof is null && type is not "moof" and not "mdat")
        {
            if(prefixBoxes.Sum(b=>b.Data.Length)+box.Length>4*1024*1024)throw new InvalidDataException("Oversized initialization prefix.");
            prefixBoxes.Add((offset,box));
        }
        if(type is "ftyp" or "moov")
        {
            if(headers.Sum(h=>h.Length)+box.Length>4*1024*1024) throw new InvalidDataException("Oversized MP4 initialization.");
            headers.Add(box);
            if(type=="moov") ParseTracks(box);
        }
        else if(type=="moof") { currentMoof=box; moofOffset=offset; }
        else if(type=="mdat" && currentMoof is not null)
        {
            var f=ParseFragment(currentMoof,box,moofOffset);
            fragments.AddLast(f);
            retained+=f.Moof.Length+f.Mdat.Length;
            currentMoof=null;
            Prune();
        }
    }

    private void Prune()
    {
        if(fragments.Count==0)return;
        double cutoff=fragments.Last!.Value.End-seconds-10;
        while(fragments.Count>1 && (fragments.First!.Value.End<cutoff || retained>MaxBytes))
        {
            var first=fragments.First!.Value;
            retained-=first.Moof.Length+first.Mdat.Length;
            fragments.RemoveFirst();
        }
    }

    public Snapshot TakeSnapshot(int requestedSeconds)
    {
        lock(gate)
        {
            if(tracks.Count==0 || fragments.Count==0) throw new InvalidOperationException("Replay is warming up. Wait for a complete video fragment.");
            var all=fragments.ToArray();
            double end=all.Max(f=>f.End), cutoff=end-Math.Clamp(requestedSeconds,1,300);
            int start=-1;
            // A preceding keyframe is useful preroll, but exporting it makes the clip
            // longer than requested. Keep a decodable start INSIDE the time window.
            start=Array.FindIndex(all,f=>f.StartsVideoKeyframe && f.Start>=cutoff-.000001);
            // Sub-GOP requests and startup may contain no later keyframe. Keep
            // the latest decodable fragment; the save muxer bounds its duration.
            if(start<0)start=Array.FindLastIndex(all,f=>f.StartsVideoKeyframe);
            if(start<0)throw new InvalidOperationException("No verified keyframe is buffered yet. Wait a moment and save again. No file was saved.");
            var selected=all[start..];
            double origin=selected.SelectMany(f=>f.Timings).Min(t=>t.Time/(double)tracks[t.TrackId].Scale);
            return new Snapshot(headers.SelectMany(b=>b).ToArray(),selected,origin,Math.Max(0,end-origin));
        }
    }

    public async Task WriteSnapshotAsync(Snapshot snapshot,string path)
    {
        // A bounded caller queue limits pinned snapshots. Packet data is shared until each write.
        await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true);
        await file.WriteAsync(snapshot.Header);
        foreach(var f in snapshot.Fragments)
        {
            byte[] moof=(byte[])f.Moof.Clone();
            foreach(var t in f.Timings)
            {
                if(t.Position<0)continue;
                ulong shift=(ulong)Math.Floor(snapshot.Origin*tracks[t.TrackId].Scale);
                ulong time=t.Time>=shift?t.Time-shift:0;
                if(t.Wide)BinaryPrimitives.WriteUInt64BigEndian(moof.AsSpan(t.Position,8),time);
                else BinaryPrimitives.WriteUInt32BigEndian(moof.AsSpan(t.Position,4),checked((uint)time));
            }
            foreach(int traf in Children(moof,0).Where(p=>Type(moof,p)=="traf"))
                foreach(int tfhd in Children(moof,traf).Where(p=>Type(moof,p)=="tfhd"))
                    if((Flags(moof,tfhd)&1)!=0)
                    {
                        ulong original=U64(moof,tfhd+16);
                        long delta=checked((long)original)-f.OriginalOffset;
                        BinaryPrimitives.WriteUInt64BigEndian(moof.AsSpan(tfhd+16,8),checked((ulong)(file.Position+delta)));
                    }
            await file.WriteAsync(moof);
            await file.WriteAsync(f.Mdat);
        }
        await file.FlushAsync();
        file.Flush(true);
    }

    private void ParseTracks(byte[] moov)
    {
        foreach(int trak in Children(moov,0).Where(p=>Type(moov,p)=="trak"))
        {
            int tkhd=Child(moov,trak,"tkhd"),mdia=Child(moov,trak,"mdia");
            int mdhd=Child(moov,mdia,"mdhd"),hdlr=Child(moov,mdia,"hdlr");
            uint id=U32(moov,tkhd+(moov[tkhd+8]==1?28:20));
            uint scale=U32(moov,mdhd+(moov[mdhd+8]==1?28:20));
            if(scale==0)throw new InvalidDataException("Invalid track timescale.");
            tracks[id]=new Track(id,scale,Encoding.ASCII.GetString(moov,hdlr+16,4)=="vide",0);
        }
        foreach(int mvex in Children(moov,0).Where(p=>Type(moov,p)=="mvex"))
            foreach(int trex in Children(moov,mvex).Where(p=>Type(moov,p)=="trex"))
            {
                uint id=U32(moov,trex+12);
                if(tracks.TryGetValue(id,out var t)) tracks[id]=t with{DefaultFlags=U32(moov,trex+28)};
            }
    }

    private Fragment ParseFragment(byte[] moof,byte[] mdat,long offset)
    {
        List<Timing> timings=[];
        double start=double.MaxValue,end=0;
        bool keyframe=false;
        foreach(int traf in Children(moof,0).Where(p=>Type(moof,p)=="traf"))
        {
            int tfhd=Child(moof,traf,"tfhd");
            uint id=U32(moof,tfhd+12);
            if(!tracks.TryGetValue(id,out var track))throw new InvalidDataException("Unknown MP4 track.");
            int tfdt=Children(moof,traf).FirstOrDefault(p=>Type(moof,p)=="tfdt",-1);
            int tfxd=Children(moof,traf).FirstOrDefault(p=>Type(moof,p)=="uuid" && U32(moof,p)>=36 && Convert.ToHexString(moof.AsSpan(p+8,16))=="6D1D9B0542D544E680E2141DAFF757B2",-1);
            bool wide=tfdt>=0?moof[tfdt+8]==1:tfxd>=0&&moof[tfxd+24]==1;
            int timePosition=tfdt>=0?tfdt+12:tfxd>=0?tfxd+28:-1;
            ulong time=timePosition>=0?(wide?U64(moof,timePosition):U32(moof,timePosition)):nextTimes.GetValueOrDefault(id);
            timings.Add(new Timing(id,time,timePosition,wide));
            int hf=Flags(moof,tfhd),pos=tfhd+16;
            if((hf&1)!=0)pos+=8;
            if((hf&2)!=0)pos+=4;
            uint duration=0,defaultFlags=track.DefaultFlags;
            if((hf&8)!=0){duration=U32(moof,pos);pos+=4;}
            if((hf&16)!=0)pos+=4;
            if((hf&32)!=0)defaultFlags=U32(moof,pos);
            ulong ticks=0;
            bool first=true;
            foreach(int trun in Children(moof,traf).Where(p=>Type(moof,p)=="trun"))
            {
                int flags=Flags(moof,trun),p=trun+16;
                uint count=U32(moof,trun+12),firstFlags=defaultFlags;
                if(count>100000)throw new InvalidDataException("Oversized fragment sample count.");
                if((flags&1)!=0)p+=4;
                if((flags&4)!=0){firstFlags=U32(moof,p);p+=4;}
                for(uint i=0;i<count;i++)
                {
                    uint d=duration,sampleFlags=i==0?firstFlags:defaultFlags;
                    if((flags&256)!=0){d=U32(moof,p);p+=4;}
                    if((flags&512)!=0)p+=4;
                    if((flags&1024)!=0){sampleFlags=U32(moof,p);p+=4;}
                    if((flags&2048)!=0)p+=4;
                    if(first && track.Video)keyframe=(sampleFlags&0x10000)==0;
                    first=false;ticks+=d;
                }
            }
            double a=time/(double)track.Scale,b=(time+ticks)/(double)track.Scale;
            nextTimes[id]=time+ticks;
            start=Math.Min(start,a);end=Math.Max(end,b);
        }
        if(timings.Count==0)throw new InvalidDataException("Fragment has no timestamped tracks.");
        return new Fragment(moof,mdat,offset,start,end,keyframe,timings.ToArray());
    }

    internal static IEnumerable<int> Children(byte[] bytes,int parent)
    {
        int end=checked(parent+(int)U32(bytes,parent)),p=parent+8;
        if(end>bytes.Length)throw new InvalidDataException("Truncated MP4 container.");
        while(p<end)
        {
            if(end-p<8)throw new InvalidDataException("Truncated MP4 header.");
            uint size=U32(bytes,p);
            if(size<8 || size>end-p)throw new InvalidDataException("Invalid child MP4 box.");
            yield return p;p+=(int)size;
        }
    }
    private static int Child(byte[] b,int parent,string type)=>Children(b,parent).FirstOrDefault(p=>Type(b,p)==type,-1) is int p && p>=0?p:throw new InvalidDataException("Missing MP4 "+type);
    internal static uint U32(byte[] b,int p)=>BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p,4));
    private static ulong U64(byte[] b,int p)=>BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p,8));
    internal static string Type(byte[] b,int p)=>Encoding.ASCII.GetString(b,p+4,4);
    private static int Flags(byte[] b,int p)=>(b[p+9]<<16)|(b[p+10]<<8)|b[p+11];
}
