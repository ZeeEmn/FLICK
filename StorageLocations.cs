using System.IO;
namespace Rewind;
internal static class StorageLocations
{
    private static string? current;
    private static readonly object Gate=new();
    private static readonly List<string> previous=new();
    internal static string Default=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"FLICK");
    internal static string Current=>Volatile.Read(ref current)??Default;
    internal static void Set(string? path){string target=LocalPaths.Require(path??Default);for(var directory=new DirectoryInfo(target);directory is not null;directory=directory.Parent)if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Choose a local folder without directory links.");Directory.CreateDirectory(target);string? probe=null;try{probe=Path.Combine(target,".flick-write-"+Guid.NewGuid().ToString("N"));using(var file=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None))file.WriteByte(0);}finally{if(probe is not null)File.Delete(probe);}lock(Gate){if(!previous.Contains(Current,StringComparer.OrdinalIgnoreCase))previous.Add(Current);while(previous.Count>8)previous.RemoveAt(0);Volatile.Write(ref current,target);}}
    internal static string[] Roots(){lock(Gate)return previous.Append(Current).Append(Default).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();}
}
