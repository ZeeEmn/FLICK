using System.IO;
using System.Runtime.InteropServices;

namespace Rewind;

internal static class DxgiDisplays
{
    internal sealed record Target(string DeviceName,int AdapterIndex,int OutputIndex,int Width,int Height);
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
