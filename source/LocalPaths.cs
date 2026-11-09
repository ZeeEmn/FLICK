using System.IO;
namespace Rewind;
internal static class LocalPaths
{
    internal static string Require(string path){string full=Path.GetFullPath(path);if(full.StartsWith(@"\\",StringComparison.Ordinal)||new DriveInfo(Path.GetPathRoot(full)!).DriveType==DriveType.Network)throw new IOException("FLICK only accepts local storage paths.");return full;}
}
