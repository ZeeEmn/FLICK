using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rewind;
internal sealed record ClipCard(string Path,string Name,DateTime Created,ImageSource? Thumbnail);
// File enumeration, JPEG decoding and thumbnail creation never run on the UI thread.
internal sealed class ClipCatalog
{
    private ClipCard[] snapshot=[];
    private Task? refreshTask;
    private bool pending;
    private readonly object gate=new();
    private readonly Dictionary<string,(DateTime Stamp,ImageSource Image)> images=new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<ClipCard> Snapshot=>Volatile.Read(ref snapshot);
    internal event Action? Changed;
    internal static string ThumbnailPath(string path){
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant())))[..24];
        return System.IO.Path.Combine(ReplayEngine.DataFolder,".cache","thumbnails",key+".jpg");
    }
    internal static IEnumerable<string> Folders(){
        yield return ReplayEngine.SaveFolder;
        foreach(string folder in StorageLocations.Roots())if(!string.Equals(folder,ReplayEngine.SaveFolder,StringComparison.OrdinalIgnoreCase))yield return LocalPaths.Require(folder);
        string legacy=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"REWIND");
        if(!string.Equals(legacy,ReplayEngine.SaveFolder,StringComparison.OrdinalIgnoreCase))yield return LocalPaths.Require(legacy);
    }
    internal Task RefreshAsync(){lock(gate){pending=true;return refreshTask??=Task.Run(RefreshLoopAsync);}}
    private async Task RefreshLoopAsync(){
        try{while(true){
            lock(gate)pending=false;
            var cards=await Task.Run(()=>{
                var files=Folders().Where(Directory.Exists).SelectMany(d=>Directory.EnumerateFiles(d,"*.mp4")).Where(p=>!p.EndsWith(".saving.mp4",StringComparison.OrdinalIgnoreCase)).Select(p=>new FileInfo(p)).OrderByDescending(f=>f.LastWriteTimeUtc).Take(120).ToArray();
                var rows=new List<ClipCard>();var keep=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var file in files){
                    string thumb=ThumbnailPath(file.FullName);keep.Add(thumb);ImageSource? image=null;
                    if(File.Exists(thumb))try{DateTime stamp=File.GetLastWriteTimeUtc(thumb);if(images.TryGetValue(thumb,out var cached)&&cached.Stamp==stamp)image=cached.Image;
                        else{var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.CreateOptions=BitmapCreateOptions.IgnoreImageCache;bitmap.DecodePixelWidth=320;bitmap.UriSource=new Uri(thumb);bitmap.EndInit();bitmap.Freeze();image=bitmap;images[thumb]=(stamp,image);}}catch(IOException){}catch(NotSupportedException){}
                    rows.Add(new(file.FullName,System.IO.Path.GetFileNameWithoutExtension(file.Name),file.LastWriteTime,image));
                }
                foreach(string old in images.Keys.Where(k=>!keep.Contains(k)).ToArray())images.Remove(old);
                return rows.ToArray();
            });
            Volatile.Write(ref snapshot,cards);Changed?.Invoke();
            lock(gate){if(!pending){refreshTask=null;return;}}
        }}catch(Exception e){PrivateLog.Write("catalog.log",e.Message);lock(gate)refreshTask=null;}
    }
}
