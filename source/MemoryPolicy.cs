using System.Runtime.InteropServices;
namespace Rewind;
internal static class MemoryPolicy
{
    [StructLayout(LayoutKind.Sequential)] private struct Status{public uint Length,Load;public ulong TotalPhys,AvailPhys,TotalPage,AvailPage,TotalVirtual,AvailVirtual,Extended;}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GlobalMemoryStatusEx(ref Status status);
    internal static long Available(){var s=new Status{Length=(uint)Marshal.SizeOf<Status>()};return GlobalMemoryStatusEx(ref s)?(long)Math.Min(s.AvailPhys,(ulong)long.MaxValue):2L*1024*1024*1024;}
    internal static long Budget(long available)=>Math.Min(4L*1024*1024*1024,Math.Max(0,available)/4);
    internal static long VideoLimit(long available){long budget=Budget(available);if(budget<128L*1024*1024)throw new InvalidOperationException("Not enough available RAM to start replay safely.");return Math.Min(768L*1024*1024,(budget-96L*1024*1024)/2);}
    internal static int Rate(string? profile)=>QualityProfiles.Get(profile).Bitrate;
    internal static int ClampSeconds(int requested,string? profile,long available){
        long bytesPerSecond=Rate(profile)/8+288000;long budget=Budget(available);long usable=Math.Max(0,budget-96L*1024*1024);
        long max=Math.Min(300,Math.Min(usable/(2*bytesPerSecond)-20,VideoLimit(available)/(Rate(profile)/8)-10));
        if(max<1)throw new InvalidOperationException("Not enough available RAM for this replay quality. Close other applications or choose a smaller quality profile.");
        return (int)Math.Clamp((long)requested,1,max);
    }
}
internal sealed class SaveAdmission
{
    private int active;
    internal bool TryEnter()=>Interlocked.CompareExchange(ref active,1,0)==0;
    internal void Exit()=>Volatile.Write(ref active,0);
}
