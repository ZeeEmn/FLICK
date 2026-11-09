using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rewind;
internal enum UpdateState { Current, Available, Unavailable }
internal sealed record UpdateResult(UpdateState State,string Message,string? Tag=null)
{
    internal string? ReleaseUrl=>Tag is null?null:"https://github.com/ZeeEmn/FLICK/releases/tag/"+Uri.EscapeDataString(Tag);
}
internal static class UpdateChecker
{
    internal const string Endpoint="https://api.github.com/repos/ZeeEmn/FLICK/releases/latest";
    internal static Version CurrentVersion {get {var v=Assembly.GetExecutingAssembly().GetName().Version!;return new(v.Major,v.Minor,Math.Max(0,v.Build));}}
    private static readonly object Gate=new();
    private static Task<UpdateResult>? running;
    internal static Task<UpdateResult> CheckAsync(CancellationToken cancellation){
        lock(Gate){if(running is {IsCompleted:false})return running;
            // HTTP/TLS initialization and all network work stay off capture and UI threads.
            return running=Task.Run(async()=>{
                using var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(4)};
                using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(4)};
                return await CheckCoreAsync(client,CurrentVersion,cancellation).ConfigureAwait(false);
            },CancellationToken.None);
        }
    }
    internal static async Task<UpdateResult> CheckCoreAsync(HttpClient client,Version current,CancellationToken cancellation){
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try{
            using var request=new HttpRequestMessage(HttpMethod.Get,Endpoint);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");request.Headers.UserAgent.ParseAdd("FLICK/"+current.ToString(3));
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false);
            if(response.StatusCode==HttpStatusCode.NotFound)return new(UpdateState.Unavailable,"No public release is listed on GitHub yet.");
            if(!response.IsSuccessStatusCode)return new(UpdateState.Unavailable,"Update check unavailable (GitHub HTTP "+(int)response.StatusCode+"). Try again later.");
            if(response.Content.Headers.ContentLength>65536)return new(UpdateState.Unavailable,"GitHub returned an unexpectedly large update response.");
            using var stream=await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);using var bytes=new MemoryStream();byte[] block=new byte[4096];
            int count;while((count=await stream.ReadAsync(block,deadline.Token).ConfigureAwait(false))>0){if(bytes.Length+count>65536)return new(UpdateState.Unavailable,"GitHub returned an unexpectedly large update response.");bytes.Write(block,0,count);}
            using var json=JsonDocument.Parse(bytes.ToArray(),new JsonDocumentOptions{MaxDepth=12});var root=json.RootElement;
            if(root.TryGetProperty("draft",out var draft)&&draft.GetBoolean()||root.TryGetProperty("prerelease",out var pre)&&pre.GetBoolean())return new(UpdateState.Unavailable,"No stable update is listed.");
            if(!root.TryGetProperty("tag_name",out var tagValue)||tagValue.ValueKind!=JsonValueKind.String)return new(UpdateState.Unavailable,"The release version could not be read.");
            string tag=tagValue.GetString()!;
            if(tag.Length>40||!Regex.IsMatch(tag,@"\Av?\d+\.\d+\.\d+\z",RegexOptions.CultureInvariant)||!Version.TryParse(tag.TrimStart('v'),out var version))return new(UpdateState.Unavailable,"The release version format is unsupported.");
            return version>current?new(UpdateState.Available,"FLICK "+version.ToString(3)+" is available.",tag):new(UpdateState.Current,"FLICK "+current.ToString(3)+" is up to date.");
        }catch(OperationCanceledException){return new(UpdateState.Unavailable,cancellation.IsCancellationRequested?"Update check cancelled.":"Update check timed out. Recording is unaffected.");}
        catch(Exception e) when(e is HttpRequestException or IOException or JsonException or InvalidOperationException or FormatException){return new(UpdateState.Unavailable,"Unable to check for updates. Check your connection and try again.");}
    }
}
