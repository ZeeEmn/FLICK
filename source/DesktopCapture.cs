using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Rewind;

// Acquisition-specific launch/reporting only. Encoder arguments and replay buffer are reused unchanged.
internal static class DesktopCapture
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr OpenEvent(uint access,bool inherit,string name);
    [DllImport("kernel32.dll")] private static extern bool SetEvent(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    internal static void Signal(Process worker,string kind){IntPtr signal=OpenEvent(2,false,$"Local\\REWIND.Desktop{kind}.{worker.Id}");if(signal!=IntPtr.Zero){try{SetEvent(signal);}finally{CloseHandle(signal);}}}
    internal static void StopAcquisition(Process worker)=>Signal(worker,"Stop");
    internal static string[] Arguments(int width=1920,int height=1080,int delay=0,bool trace=false,int spinUs=0,string? postFilter=null,int fps=60){
        int adapter=DxgiDisplays.Resolve(null).AdapterIndex;
        return ["-hide_banner","-loglevel","warning","-nostats","-init_hw_device",$"d3d11va=rewind:{adapter}","-filter_hw_device","rewind",
           "-filter_complex",$"rewinddesktop=width={width}:height={height}:fps={fps}:spin_us={spinUs}{(delay>0?$":test_delay_ms={delay}":"")}{(trace?":trace=1":"")}{(string.IsNullOrEmpty(postFilter)?"":","+postFilter)}[captured]","-map","[captured]"];
    }
}
