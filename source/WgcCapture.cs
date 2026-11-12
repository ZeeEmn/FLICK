using System.Diagnostics;

namespace Rewind;

internal static class WgcCapture
{
    internal const string Dxgi = "Native Desktop";
    internal const string Beta = "Windows Graphics Capture (Beta)";
    internal const string Worker = "flick-wgc-capture";
    internal const string Bridge = "rewind_wgc_bridge.dll";

    internal static bool IsTool(string name) => name is Worker + ".exe" or Bridge;
    internal static string Normalize(string? backend) => backend == Beta ? Beta : Dxgi;

    internal static string[] Arguments(CapturePlan plan, EncoderChoice encoder)
    {
        string filter = $"rewindwgc=primary=1:width={plan.SourceWidth}:height={plan.SourceHeight}:fps={plan.Fps}:spin_us=0";
        string postFilter = QualityProfiles.Filter(plan, encoder);
        if (postFilter.Length != 0) filter += "," + postFilter;
        return ["-hide_banner", "-loglevel", "warning", "-nostats",
            "-init_hw_device", $"d3d11va=rewind:{encoder.Adapter}", "-filter_hw_device", "rewind",
            "-filter_complex", filter + "[captured]", "-map", "[captured]"];
    }

    internal static void StopAcquisition(Process worker) =>
        DesktopCapture.SignalEvent($"Local\\REWIND.WgcStop.{worker.Id}");
}
