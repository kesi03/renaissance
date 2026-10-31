using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Renaissance.Clap;

/// <summary>
/// Managed implementations of the three function pointers stored in the native <c>clap_entry</c>
/// (see <c>clap_entry.c</c>). CLAP requires <c>clap_entry</c> itself to be an exported data symbol,
/// which NativeAOT cannot produce from C#, so a tiny C shim owns the struct and forwards to these.
/// </summary>
internal static unsafe class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "renaissance_clap_init", CallConvs = [typeof(CallConvCdecl)])]
    public static byte Init(byte* pluginPath)
    {
        try
        {
            Log.Write("entry init: " + (pluginPath is null ? "<null>" : Marshal.PtrToStringUTF8((IntPtr)pluginPath)));
            return 1;
        }
        catch (Exception ex)
        {
            Log.Write("entry init failed: " + ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "renaissance_clap_deinit", CallConvs = [typeof(CallConvCdecl)])]
    public static void Deinit()
    {
        Log.Write("entry deinit");
    }

    [UnmanagedCallersOnly(EntryPoint = "renaissance_clap_get_factory", CallConvs = [typeof(CallConvCdecl)])]
    public static void* GetFactory(byte* factoryId)
    {
        try
        {
            if (factoryId is null) return null;
            if (Strings.Equals(factoryId, ClapConstants.PluginFactoryId)) return ClapFactory.Get();

            Log.Write("unknown factory: " + Marshal.PtrToStringUTF8((IntPtr)factoryId));
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("get_factory failed: " + ex);
            return null;
        }
    }
}
