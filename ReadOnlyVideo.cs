using System.Globalization;
using System.IO;
using System.Text.Json;
namespace Rewind;
internal static class ReadOnlyVideo
{
    internal static string LocalFile(string path){string full=LocalPaths.Require(path);if(!File.Exists(full))throw new FileNotFoundException("The video was not found.",full);for(FileSystemInfo? item=new FileInfo(full);item is not null;item=item is FileInfo file?file.Directory:((DirectoryInfo)item).Parent)if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Choose a local video without file or directory links.");return full;}
    internal static async Task<ClipFiles.Info> ProbeAsync(string path,CancellationToken cancellation=default){
        path=LocalFile(path);string json=await ReplayEngine.RunToolAsync("ffprobe",new[]{"-v","error","-show_entries","format=duration:stream=index,codec_type:stream_tags=title,handler_name","-of","json",path},TimeSpan.FromSeconds(30),cancellation);
        using var doc=JsonDocument.Parse(json);double duration=double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);var streams=doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();if(!double.IsFinite(duration)||duration<=0||!streams.Any(s=>s.GetProperty("codec_type").GetString()=="video"))throw new InvalidDataException("The selected file has no supported, timed video stream.");int ordinal=0;var audio=streams.Where(s=>s.GetProperty("codec_type").GetString()=="audio").Select(s=>new ClipFiles.AudioTrack(s.GetProperty("index").GetInt32(),ordinal++,s.TryGetProperty("tags",out var tags)&&tags.TryGetProperty("title",out var title)?title.GetString()!:s.TryGetProperty("tags",out var ht)&&ht.TryGetProperty("handler_name",out var handler)?handler.GetString()!:"Audio "+ordinal)).ToArray();return new(duration,Array.Empty<double>(),audio);
    }
}
