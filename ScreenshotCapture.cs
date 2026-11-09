using System.IO;
namespace Rewind;
internal static class ScreenshotCapture
{
    private static readonly SemaphoreSlim Gate=new(1,1);
    internal static async Task<string> SaveAsync(){if(!await Gate.WaitAsync(0))throw new InvalidOperationException("A screenshot is already saving.");string? temp=null;try{string folder=ReplayEngine.SaveFolder;Directory.CreateDirectory(folder);string name="Screenshot_"+DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")+"_"+Guid.NewGuid().ToString("N")[..6];string final=Path.Combine(folder,name+".png");temp=Path.Combine(folder,name+".saving.png");await ReplayEngine.RunToolAsync("flick-screenshot",new[]{temp},TimeSpan.FromSeconds(12));var size=new FileInfo(temp).Length;if(size<64)throw new IOException("Screenshot was empty.");File.Move(temp,final);return final;}finally{if(temp is not null)try{File.Delete(temp);}catch(IOException){}Gate.Release();}}
}
