using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Rewind;

internal sealed record UpdateAsset(Uri Url,long Size,string Sha256);

internal static class UpdateInstaller
{
    internal const long MaximumBytes=512L*1024*1024;
    private static string Folder=>Path.Combine(ReplayEngine.DataFolder,"updates");
    private static string ResultPath=>Path.Combine(Folder,"install-result.txt");

    internal static Task<string> DownloadAsync(UpdateAsset asset,IProgress<double> progress,CancellationToken cancellation)
        =>Task.Run(async()=>{
            using var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(10)};
            using var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            string directory=Path.Combine(Folder,Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string file=Path.Combine(directory,"FLICK-Setup.msi");
            try
            {
                await DownloadCoreAsync(client,asset,file,progress,deadline.Token).ConfigureAwait(false);
                return file;
            }
            catch
            {
                RemoveDownload(file);
                throw;
            }
        },cancellation);

    internal static async Task DownloadCoreAsync(HttpClient client,UpdateAsset asset,string file,IProgress<double> progress,CancellationToken cancellation)
    {
        Uri url=asset.Url;
        HttpResponseMessage? response=null;
        try
        {
            for(int redirects=0;redirects<=4;redirects++)
            {
                if(url.Scheme!="https"||!url.IsDefaultPort||url.UserInfo.Length!=0||
                    (redirects==0?url.Host!="github.com":url.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com")))
                    throw new InvalidDataException("The installer download redirected outside GitHub.");
                response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,cancellation).ConfigureAwait(false);
                if((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))break;
                if(redirects==4||response.Headers.Location is not { } target)
                    throw new InvalidDataException("GitHub returned an invalid installer redirect.");
                url=new Uri(url,target);
                response.Dispose();response=null;
            }
            response!.EnsureSuccessStatusCode();
            if(asset.Size<=0||asset.Size>MaximumBytes||response.Content.Headers.ContentLength is long length&&length!=asset.Size)
                throw new InvalidDataException("The installer size does not match the release.");

            await using var input=await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            await using var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true);
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer=new byte[65536];
            long total=0;
            var clock=Stopwatch.StartNew();
            int count;
            while((count=await input.ReadAsync(buffer,cancellation).ConfigureAwait(false))!=0)
            {
                total+=count;
                if(total>asset.Size)throw new InvalidDataException("The installer size does not match the release.");
                hash.AppendData(buffer,0,count);
                await output.WriteAsync(buffer.AsMemory(0,count),cancellation).ConfigureAwait(false);
                if(clock.ElapsedMilliseconds>=150){progress.Report((double)total/asset.Size);clock.Restart();}
            }
            if(total!=asset.Size||!Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer failed SHA-256 verification.");
            progress.Report(1);
        }
        finally{response?.Dispose();}
    }

    internal static void Start(string file,UpdateAsset asset,bool launchOnLogin)
    {
        using var current=Process.GetCurrentProcess();
        using var registry=Registry.CurrentUser.OpenSubKey(@"Software\FLICK\Installer");
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe"))
        {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Folder};
        using var script=typeof(UpdateInstaller).Assembly.GetManifestResourceStream("Rewind.ApplyUpdate.ps1")!;
        using var reader=new StreamReader(script);
        foreach(string argument in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(reader.ReadToEnd()))})
            start.ArgumentList.Add(argument);
        start.Environment["FLICK_UPDATE_MSI"]=file;
        start.Environment["FLICK_UPDATE_SHA256"]=asset.Sha256;
        start.Environment["FLICK_UPDATE_RESULT"]=ResultPath;
        start.Environment["FLICK_UPDATE_PID"]=current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["FLICK_UPDATE_START"]=current.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["FLICK_UPDATE_EXECUTABLE"]=Environment.ProcessPath!;
        start.Environment["FLICK_UPDATE_STARTUP"]=launchOnLogin?"1":"0";
        start.Environment["FLICK_UPDATE_DESKTOP"]=registry?.GetValue("DesktopShortcut") is 1?"1":"0";
        using var worker=Process.Start(start)??throw new IOException("Could not start the FLICK update installer.");
    }

    internal static string? ReadResult()
    {
        try
        {
            if(!File.Exists(ResultPath))return null;
            string result=File.ReadAllText(ResultPath).Trim();
            File.Delete(ResultPath);
            return result switch
            {
                "0"=>null,
                "3010"=>"The FLICK update was installed. Restart Windows to finish.",
                "1618"=>"Another installation was in progress. Close it and try the FLICK update again.",
                "1638"=>"A different version is already installed. Check which version is running.",
                _=>"Windows Installer could not finish the update ("+result+"). You can retry from Check for updates."
            };
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return null;}
    }

    internal static void RemoveDownload(string file)
    {
        try{File.Delete(file);Directory.Delete(Path.GetDirectoryName(file)!);}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){}
    }
}
