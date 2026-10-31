using System.Runtime.InteropServices;

namespace Renaissance.Clap;

internal static unsafe class ModuleInfo
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(IntPtr module, char* buffer, uint size);

    /// <summary>Full path of this CLAP DLL (not the host process).</summary>
    public static string? Path()
    {
        const uint fromAddress = 0x4, unchangedRefCount = 0x2;
        var anchor = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte>)&Exports.Init;
        if (!GetModuleHandleExW(fromAddress | unchangedRefCount, anchor, out var module)) return null;
        var buffer = stackalloc char[1024];
        var len = GetModuleFileNameW(module, buffer, 1024);
        return len == 0 ? null : new string(buffer, 0, (int)len);
    }
}
