using System.IO;
using System.Windows.Media.Imaging;
namespace Rewind;
internal static class ScreenshotCapture
{
    private static readonly SemaphoreSlim Gate=new(1,1);
    internal static async Task<string> SaveAsync(string? display=null)
    {
        if(!await Gate.WaitAsync(0))throw new InvalidOperationException("A screenshot is already saving.");
        string? temp=null;
        try
        {
            string folder=ReplayEngine.SaveFolder;
            Directory.CreateDirectory(folder);
            string name="Screenshot_"+DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")+"_"+Guid.NewGuid().ToString("N")[..6];
            string final=Path.Combine(folder,name+".png");
            temp=Path.Combine(folder,name+".saving.png");
            await ReplayEngine.RunToolAsync("flick-screenshot",display is null?[temp]:[temp,display],TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            using(var stream=File.OpenRead(temp))
            {
                var frame=new PngBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
                if(frame.PixelWidth<=0||frame.PixelHeight<=0)throw new IOException("Screenshot was empty.");
            }
            File.Move(temp,final);
            return final;
        }
        finally
        {
            try{if(temp is not null)File.Delete(temp);}
            catch(IOException){}
            finally{Gate.Release();}
        }
    }
}
