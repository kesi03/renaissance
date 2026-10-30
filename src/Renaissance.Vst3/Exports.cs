using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Renaissance.Vst3;

internal static unsafe class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "GetPluginFactory", CallConvs = [typeof(CallConvCdecl)])]
    public static IntPtr GetPluginFactory()
    {
        try { return Factory.Instance(); }
        catch (Exception ex)
        {
            Log.Write("GetPluginFactory failed: " + ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "InitDll", CallConvs = [typeof(CallConvCdecl)])]
    public static byte InitDll() => 1;

    [UnmanagedCallersOnly(EntryPoint = "ExitDll", CallConvs = [typeof(CallConvCdecl)])]
    public static byte ExitDll() => 1;
}

/// <summary>IPluginFactory3: exposes the instrument and effect versions of Renaissance as two VST3 classes.</summary>
internal static unsafe class Factory
{
    private const string Vendor = "Renaissance";
    private static string Version => Renaissance.Core.AppVersion.FourPart;
    private static IntPtr s_block;
    private static int s_refs;

    private static readonly (byte[] Cid, string Name, string SubCategories, bool Instrument)[] Classes =
    [
        (Tuids.InstrumentClass, "Renaissance", "Instrument|Synth", true),
        (Tuids.EffectClass, "Renaissance FX", "Fx", false),
    ];

    public static IntPtr Instance()
    {
        if (s_block == IntPtr.Zero)
        {
            var vtable = Com.Vtable(
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)&QueryInterface,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&AddRef,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&Release,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)&GetFactoryInfo,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&CountClasses,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int>)&GetClassInfo,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, IntPtr*, int>)&CreateInstance,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int>)&GetClassInfo2,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int>)&GetClassInfoUnicode,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&SetHostContext);
            var block = (IntPtr*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(IntPtr)));
            block[0] = (IntPtr)vtable;
            s_block = (IntPtr)block;
        }
        Interlocked.Increment(ref s_refs);
        return s_block;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QueryInterface(IntPtr self, byte* iid, IntPtr* obj)
    {
        if (Tuids.Eq(iid, Tuids.FUnknown) || Tuids.Eq(iid, Tuids.IPluginFactory)
            || Tuids.Eq(iid, Tuids.IPluginFactory2) || Tuids.Eq(iid, Tuids.IPluginFactory3))
        {
            Interlocked.Increment(ref s_refs);
            *obj = s_block;
            return R.Ok;
        }
        *obj = IntPtr.Zero;
        return R.NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint AddRef(IntPtr self) => (uint)Interlocked.Increment(ref s_refs);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Release(IntPtr self) => (uint)Math.Max(0, Interlocked.Decrement(ref s_refs));

    // PFactoryInfo: vendor[64], url[256], email[128], int32 flags
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetFactoryInfo(IntPtr self, byte* info)
    {
        Com.PutAscii(info, 64, Vendor);
        Com.PutAscii(info + 64, 256, "https://github.com/renaissance");
        Com.PutAscii(info + 64 + 256, 128, "");
        *(int*)(info + 64 + 256 + 128) = 16; // kUnicode
        return R.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CountClasses(IntPtr self) => Classes.Length;

    // PClassInfo: cid[16], int32 cardinality, category[32], name[64]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetClassInfo(IntPtr self, int index, byte* info)
    {
        if ((uint)index >= Classes.Length) return R.InvalidArgument;
        var c = Classes[index];
        for (var i = 0; i < 16; i++) info[i] = c.Cid[i];
        *(int*)(info + 16) = 0x7FFFFFFF;
        Com.PutAscii(info + 20, 32, "Audio Module Class");
        Com.PutAscii(info + 52, 64, c.Name);
        return R.Ok;
    }

    // PClassInfo2 adds: uint32 classFlags @116, subCategories[128] @120, vendor[64] @248, version[64] @312, sdkVersion[64] @376
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetClassInfo2(IntPtr self, int index, byte* info)
    {
        if ((uint)index >= Classes.Length) return R.InvalidArgument;
        var c = Classes[index];
        for (var i = 0; i < 16; i++) info[i] = c.Cid[i];
        *(int*)(info + 16) = 0x7FFFFFFF;
        Com.PutAscii(info + 20, 32, "Audio Module Class");
        Com.PutAscii(info + 52, 64, c.Name);
        *(uint*)(info + 116) = 0;
        Com.PutAscii(info + 120, 128, c.SubCategories);
        Com.PutAscii(info + 248, 64, Vendor);
        Com.PutAscii(info + 312, 64, Version);
        Com.PutAscii(info + 376, 64, "VST 3.7");
        return R.Ok;
    }

    // PClassInfoW: cid[16], cardinality @16, category[32] @20, char16 name[64] @52, classFlags @180,
    // subCategories[128] @184, char16 vendor[64] @312, char16 version[64] @440, char16 sdkVersion[64] @568
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetClassInfoUnicode(IntPtr self, int index, byte* info)
    {
        if ((uint)index >= Classes.Length) return R.InvalidArgument;
        var c = Classes[index];
        for (var i = 0; i < 16; i++) info[i] = c.Cid[i];
        *(int*)(info + 16) = 0x7FFFFFFF;
        Com.PutAscii(info + 20, 32, "Audio Module Class");
        Com.PutUtf16((char*)(info + 52), 64, c.Name);
        *(uint*)(info + 180) = 0;
        Com.PutAscii(info + 184, 128, c.SubCategories);
        Com.PutUtf16((char*)(info + 312), 64, Vendor);
        Com.PutUtf16((char*)(info + 440), 64, Version);
        Com.PutUtf16((char*)(info + 568), 64, "VST 3.7");
        return R.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetHostContext(IntPtr self, IntPtr context) => R.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CreateInstance(IntPtr self, byte* cid, byte* iid, IntPtr* obj)
    {
        *obj = IntPtr.Zero;
        try
        {
            foreach (var c in Classes)
            {
                if (!Tuids.Eq(cid, c.Cid)) continue;
                var component = new Vst3Component(c.Instrument);
                var result = component.Query(iid, obj);
                component.Release(); // the creation reference; Query added the caller's
                return result;
            }
            return R.NoInterface;
        }
        catch (Exception ex)
        {
            Log.Write("CreateInstance failed: " + ex);
            return R.InternalError;
        }
    }
}
