using System.IO;
using System.Runtime.InteropServices;

namespace Rewind;

internal static class DxgiDisplays
{
    internal sealed record Target(string DeviceName,int AdapterIndex,int OutputIndex,int Width,int Height);
    internal sealed record Option(string Id,string DeviceName,string Label,bool Primary)
    {
        public override string ToString()=>Label;
    }
    private const uint ActivePaths=2;
    [StructLayout(LayoutKind.Sequential)] private struct DisplayId {public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)] private struct PathEndpoint {public DisplayId Adapter;public uint Id;}
    [StructLayout(LayoutKind.Explicit,Size=72)] private struct DisplayPath
    {
        [FieldOffset(0)] public PathEndpoint Source;
        [FieldOffset(20)] public PathEndpoint Target;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoHeader {public uint Type,Size;public PathEndpoint Device;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct SourceInfo
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct MonitorInfo
    {
        public DeviceInfoHeader Header;
        public uint Flags,Technology;
        public ushort Manufacturer,Product;
        public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string Model;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Path;
    }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags,ref uint pathCount,[Out] DisplayPath[] paths,ref uint modeCount,IntPtr modes,IntPtr topology);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] private static extern int GetSourceInfo(ref SourceInfo info);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] private static extern int GetMonitorInfo(ref MonitorInfo info);

    internal static Option[] Options()
    {
        var names=MonitorNames();
        // Number only the connected displays. Path priority can put the primary first.
        return System.Windows.Forms.Screen.AllScreens.OrderBy(screen=>DisplayOrdinal(screen.DeviceName)).Select((screen,index)=>{
            string name=screen.DeviceName;
            names.TryGetValue(name,out var monitor);
            string label=$"Display {index+1}";
            if(!string.IsNullOrWhiteSpace(monitor.Model))label+=" ("+monitor.Model+")";
            return new Option(string.IsNullOrWhiteSpace(monitor.Path)?name:monitor.Path,name,label,screen.Primary);
        }).OrderBy(option=>!option.Primary).ThenBy(option=>option.Label,StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int DisplayOrdinal(string name)
    {
        int prefix=name.LastIndexOf("DISPLAY",StringComparison.OrdinalIgnoreCase);
        return prefix>=0&&int.TryParse(name.AsSpan(prefix+7),out int number)?number:int.MaxValue;
    }

    private static Dictionary<string,(string Model,string Path)> MonitorNames()
    {
        var names=new Dictionary<string,(string,string)>(StringComparer.OrdinalIgnoreCase);
        for(int attempt=0;attempt<3;attempt++)
        {
            if(GetDisplayConfigBufferSizes(ActivePaths,out uint paths,out uint modes)!=0||paths==0)break;
            var entries=new DisplayPath[paths];
            // DISPLAYCONFIG_MODE_INFO includes a native union and is 64 bytes on Windows x64.
            var modeBuffer=Marshal.AllocHGlobal(checked((int)modes*64));
            try
            {
                int result=QueryDisplayConfig(ActivePaths,ref paths,entries,ref modes,modeBuffer,IntPtr.Zero);
                if(result==122)continue; // Topology changed between the two calls.
                if(result!=0)break;
                foreach(var entry in entries.Take((int)paths))
                {
                    var source=new SourceInfo{Header=new(){Type=1,Size=(uint)Marshal.SizeOf<SourceInfo>(),Device=entry.Source}};
                    var monitor=new MonitorInfo{Header=new(){Type=2,Size=(uint)Marshal.SizeOf<MonitorInfo>(),Device=entry.Target}};
                    if(GetSourceInfo(ref source)==0&&GetMonitorInfo(ref monitor)==0&&!string.IsNullOrEmpty(source.Name))
                        names.TryAdd(source.Name,(monitor.Model,monitor.Path));
                }
                break;
            }
            finally{Marshal.FreeHGlobal(modeBuffer);}
        }
        return names;
    }

    internal static Option? Choose(IEnumerable<Option> options,string? preferred)
        =>options.FirstOrDefault(option=>string.Equals(option.Id,preferred,StringComparison.OrdinalIgnoreCase))
            ??options.FirstOrDefault(option=>option.Primary);
    private const int NotFound=unchecked((int)0x887A0002);
    [StructLayout(LayoutKind.Sequential)] private struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct OutputDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string DeviceName;
        public Rect Bounds;public int Attached,Rotation;public IntPtr Monitor;
    }
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid id,out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Enumerate(IntPtr owner,uint index,out IntPtr child);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Describe(IntPtr output,out OutputDescription description);
    private static T Method<T>(IntPtr instance,int slot) where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance),slot*IntPtr.Size));

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct AdapterDescription{[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Name;public uint Vendor,Device,Subsystem,Revision;public UIntPtr Video,System,Shared;public long Luid;public uint Flags;}
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DescribeAdapter(IntPtr adapter,out AdapterDescription description);
    internal static (uint Vendor,long Luid,string Name) Adapter(int index){var id=new Guid("770AAE78-F26F-4DBA-A829-253C83D1B387");Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref id,out var factory));try{Marshal.ThrowExceptionForHR(Method<Enumerate>(factory,12)(factory,(uint)index,out var adapter));try{Marshal.ThrowExceptionForHR(Method<DescribeAdapter>(adapter,10)(adapter,out var d));return(d.Vendor,d.Luid,d.Name);}finally{Marshal.Release(adapter);}}finally{Marshal.Release(factory);}}
    // DXGI output indices are local to each adapter, not Windows display numbers.
    internal static Target[] EnumerateDisplays()
    {
        var id=new Guid("770AAE78-F26F-4DBA-A829-253C83D1B387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref id,out var factory));
        var result=new List<Target>();
        try
        {
            var adapters=Method<Enumerate>(factory,12); // IDXGIFactory1::EnumAdapters1
            for(uint a=0;;a++)
            {
                int hr=adapters(factory,a,out var adapter);if(hr==NotFound)break;Marshal.ThrowExceptionForHR(hr);
                try
                {
                    var outputs=Method<Enumerate>(adapter,7); // IDXGIAdapter::EnumOutputs
                    for(uint o=0;;o++)
                    {
                        hr=outputs(adapter,o,out var output);if(hr==NotFound)break;Marshal.ThrowExceptionForHR(hr);
                        try
                        {
                            Marshal.ThrowExceptionForHR(Method<Describe>(output,7)(output,out var desc));
                            if(desc.Attached!=0)result.Add(new Target(desc.DeviceName,(int)a,(int)o,desc.Bounds.Right-desc.Bounds.Left,desc.Bounds.Bottom-desc.Bounds.Top));
                        }
                        finally{Marshal.Release(output);}
                    }
                }
                finally{Marshal.Release(adapter);}
            }
        }
        finally{Marshal.Release(factory);}
        return result.ToArray();
    }
    internal static Target Select(IEnumerable<Target> targets,string name)=>targets.FirstOrDefault(t=>string.Equals(t.DeviceName,name,StringComparison.OrdinalIgnoreCase))
        ??throw new InvalidOperationException($"Could not map capture display {name} to a DXGI output. Refresh the display list and try again.");
    internal static Target Resolve(string? selected)
    {
        string name=selected??System.Windows.Forms.Screen.PrimaryScreen?.DeviceName??throw new InvalidOperationException("No primary display is available.");
        return Select(EnumerateDisplays(),name);
    }
    internal static int WriteDisplayInfo()
    {
        Directory.CreateDirectory(ReplayEngine.DataFolder);
        File.WriteAllText(Path.Combine(ReplayEngine.DataFolder,"display-info.json"),System.Text.Json.JsonSerializer.Serialize(new{Displays=EnumerateDisplays(),SelectedPrimary=Resolve(null)},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        return 0;
    }
}
