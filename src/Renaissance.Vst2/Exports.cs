using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Renaissance.Vst2;

/// <summary>Native entry points. The DAW calls VSTPluginMain exactly like it would for a C++ plugin.</summary>
internal static unsafe class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "VSTPluginMain", CallConvs = [typeof(CallConvCdecl)])]
    public static IntPtr VSTPluginMain(IntPtr hostCallback) => Create(hostCallback);

    [UnmanagedCallersOnly(EntryPoint = "main", CallConvs = [typeof(CallConvCdecl)])]
    public static IntPtr Main(IntPtr hostCallback) => Create(hostCallback);

    private static IntPtr Create(IntPtr hostCallback)
    {
        try
        {
            return Vst2Instance.Create(hostCallback);
        }
        catch (Exception ex)
        {
            Log.Write("Create failed: " + ex);
            return IntPtr.Zero;
        }
    }
}

internal static class Log
{
    public static void Write(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Renaissance", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "vst2.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

internal static unsafe class ModuleInfo
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(IntPtr module, char* buffer, uint size);

    /// <summary>Full path of this DLL (not the host process).</summary>
    public static string? Path()
    {
        const uint fromAddress = 0x4, unchangedRefCount = 0x2;
        var anchor = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)&Exports.VSTPluginMain;
        if (!GetModuleHandleExW(fromAddress | unchangedRefCount, anchor, out var module)) return null;
        var buffer = stackalloc char[1024];
        var len = GetModuleFileNameW(module, buffer, 1024);
        return len == 0 ? null : new string(buffer, 0, (int)len);
    }
}
