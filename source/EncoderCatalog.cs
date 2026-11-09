using System.Collections.Concurrent;
using System.Diagnostics;
namespace Rewind;
internal sealed record EncoderChoice(string Name,string Codec,bool Cpu,bool Download,int Adapter);
internal static class EncoderCatalog
{
    private static readonly ConcurrentDictionary<string,EncoderChoice> Cache=new();
    internal static IEnumerable<string> Candidates(uint vendor,string codec){string suffix=vendor switch{0x10de=>"nvenc",0x1002=>"amf",0x8086=>"qsv",_=>""};if(suffix.Length>0){yield return (codec=="hevc"?"hevc_":"h264_")+suffix;if(codec=="hevc")yield return "h264_"+suffix;}yield return "libx264";}
    internal static EncoderChoice Detect(string codec){
        var display=DxgiDisplays.Resolve(null);var gpu=DxgiDisplays.Adapter(display.AdapterIndex);string key=$"{gpu.Luid}:{codec}";
        return Cache.GetOrAdd(key,_=>{foreach(string name in Candidates(gpu.Vendor,codec)){var choice=new EncoderChoice(name,name.StartsWith("hevc_",StringComparison.Ordinal)?"hevc":"h264",name=="libx264",name=="libx264"||name.EndsWith("_qsv",StringComparison.Ordinal),display.AdapterIndex);if(Probe(choice))return choice;}throw new InvalidOperationException("No usable encoder initialized. Update the graphics driver and check available memory.");});
    }
    internal static void Reset()=>Cache.Clear();
    internal static bool Probe(EncoderChoice c){
        var args=new List<string>{"-v","error","-nostdin"};
        if(!c.Download)args.AddRange(["-init_hw_device",$"d3d11va=rewind:{c.Adapter}","-filter_hw_device","rewind"]);
        string pixels=c.Download?(c.Name.EndsWith("_qsv",StringComparison.Ordinal)?"format=nv12":"format=yuv420p"):"format=bgra,hwupload";
        args.AddRange(["-filter_complex","color=size=1920x1080:rate=60:duration=0.05,"+pixels+"[probe]","-map","[probe]","-frames:v","2"]);
        args.AddRange(CodecArguments(c,3000000));args.AddRange(["-f","null","-"]);
        try{ReplayEngine.RunToolAsync("rewind-desktop-capture",args,TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();return true;}catch(Exception e){PrivateLog.Write("encoder.log",c.Name+": "+e.Message);return false;}
    }
    internal static string[] CodecArguments(EncoderChoice c,int rate){
        var a=new List<string>{"-an","-c:v",c.Name,"-fps_mode","passthrough","-enc_time_base","filter"};
        if(c.Name.EndsWith("_nvenc",StringComparison.Ordinal))a.AddRange(["-preset","p4","-profile:v",c.Codec=="hevc"?"main":"high","-rc","vbr","-b:v",rate.ToString(System.Globalization.CultureInfo.InvariantCulture),"-g","120","-bf","0","-rc-lookahead","0","-multipass","disabled","-surfaces","4","-delay","0"]);
        else if(c.Name.EndsWith("_amf",StringComparison.Ordinal))a.AddRange(["-usage","lowlatency","-quality","speed","-b:v",rate.ToString(System.Globalization.CultureInfo.InvariantCulture),"-g","120","-bf","0"]);
        else if(c.Name.EndsWith("_qsv",StringComparison.Ordinal))a.AddRange(["-preset","veryfast","-b:v",rate.ToString(System.Globalization.CultureInfo.InvariantCulture),"-g","120","-bf","0"]);
        else a.AddRange(["-preset","ultrafast","-tune","zerolatency","-threads","2","-b:v",rate.ToString(System.Globalization.CultureInfo.InvariantCulture),"-g","120","-bf","0"]);
        return a.ToArray();
    }
    internal static string? AcquisitionFilter(EncoderChoice c)=>c.Download?(c.Cpu?"hwdownload,format=bgra,format=yuv420p":"hwdownload,format=bgra,format=nv12"):null;
    internal static string[] OutputArguments(EncoderChoice c,int rate){var a=new List<string>();a.AddRange(CodecArguments(c,rate));a.AddRange(["-movflags","+frag_keyframe+empty_moov+default_base_moof","-f","mp4","pipe:1"]);return a.ToArray();}
}
internal static class PrivateLog
{
    internal static void Write(string file,string text){try{string dir=ReplayEngine.DataFolder;System.IO.Directory.CreateDirectory(dir);string path=System.IO.Path.Combine(dir,file);if(System.IO.File.Exists(path)&&new System.IO.FileInfo(path).Length>2*1024*1024)System.IO.File.WriteAllText(path,"");System.IO.File.AppendAllText(path,DateTimeOffset.Now.ToString("O")+" "+text+Environment.NewLine);}catch(System.IO.IOException){}catch(UnauthorizedAccessException){}}
}
