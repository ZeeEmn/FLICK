using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
namespace Rewind;
internal static class SecurityBootstrap
{
    [DllImport("kernel32.dll")] private static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
    private static readonly HashSet<string> Verified=new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string,string>? expected;
    internal static void Initialize(){LocalPaths.Require(AppContext.BaseDirectory);SetDefaultDllDirectories(0x800);SetDllDirectory("");
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Rewind.GoldToolHashes.json")??throw new IOException("Tool integrity manifest is missing.");expected=JsonSerializer.Deserialize<Dictionary<string,string>>(stream)??throw new IOException("Tool manifest is invalid.");foreach(string name in expected.Keys)if(!WgcCapture.IsTool(name))Verify(Path.Combine(AppContext.BaseDirectory,"tools",name));
    }
    internal static void Verify(string path){
        string name=Path.GetFileName(path);lock(Verified){if(Verified.Contains(name))return;if(expected is null||!expected.TryGetValue(name,out string? wanted)||!File.Exists(path))throw new IOException("Required release tool is missing: "+name+". Repair the installation, or extract the complete portable ZIP, then run FLICK.exe.");using var file=File.OpenRead(path);string actual=Convert.ToHexString(SHA256.HashData(file));if(!string.Equals(wanted,actual,StringComparison.OrdinalIgnoreCase))throw new IOException("Release tool integrity check failed: "+name);Verified.Add(name);}
    }
}
