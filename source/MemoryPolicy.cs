using System.Runtime.InteropServices;
namespace Rewind;
internal sealed record ReplayMemoryPlan(int Seconds,int MaximumSeconds,long VideoBytes);

internal static class MemoryPolicy
{
    private const long MiB=1024L*1024;
    private const long ReserveBytes=96*MiB;
    private const int AudioHeadroomSeconds=20;
    private const long AudioBytesPerSecond=7*192_000L+96_000L;
    [StructLayout(LayoutKind.Sequential)] private struct Status{public uint Length,Load;public ulong TotalPhys,AvailPhys,TotalPage,AvailPage,TotalVirtual,AvailVirtual,Extended;}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GlobalMemoryStatusEx(ref Status status);
    internal static long Available(){var s=new Status{Length=(uint)Marshal.SizeOf<Status>()};return GlobalMemoryStatusEx(ref s)?(long)Math.Min(s.AvailPhys,(ulong)long.MaxValue):2L*1024*1024*1024;}
    internal static long Budget(long available)=>Math.Min(6*1024*MiB,Math.Max(0,available)/3);

    internal static ReplayMemoryPlan Plan(int requested,string? profile,long available)
    {
        long videoRate=QualityProfiles.Get(profile).Bitrate/8;
        // Audio lanes can change during recording. Budget for all eight lanes.
        // A save can pin old packets as the live buffer fills with new ones.
        long bytesPerHistory=Math.Max(0,Budget(available)-ReserveBytes)/2;
        int maximum=(int)Math.Clamp(bytesPerHistory/(videoRate+AudioBytesPerSecond)-AudioHeadroomSeconds,0,300);
        if(maximum==0)return new(0,0,0);
        int seconds=Math.Clamp(requested,1,maximum);
        long audioBytes=(seconds+AudioHeadroomSeconds)*AudioBytesPerSecond;
        return new(seconds,maximum,bytesPerHistory-audioBytes);
    }
}
internal sealed class SaveAdmission
{
    private int active;
    internal bool TryEnter()=>Interlocked.CompareExchange(ref active,1,0)==0;
    internal void Exit()=>Volatile.Write(ref active,0);
}
