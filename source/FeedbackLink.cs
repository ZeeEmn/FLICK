using System.Runtime.InteropServices;
namespace Rewind;
internal static class FeedbackLink
{
    internal const string Url="https://github.com/ZeeEmn/FLICK/issues/";
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr ShellExecute(IntPtr window,string operation,string file,string? parameters,string? directory,int show);
    internal static void Open(){if(ShellExecute(IntPtr.Zero,"open",Url,null,null,1).ToInt64()<=32)throw new InvalidOperationException("Windows could not open the default browser.");}
}
