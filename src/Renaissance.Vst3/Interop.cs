using System.Runtime.InteropServices;
using System.Text;

namespace Renaissance.Vst3;

/// <summary>VST3 result codes (Windows / COM values).</summary>
internal static class R
{
    public const int Ok = 0;
    public const int False = 1;
    public const int NoInterface = unchecked((int)0x80004002);
    public const int NotImplemented = unchecked((int)0x80004001);
    public const int InvalidArgument = unchecked((int)0x80070057);
    public const int InternalError = unchecked((int)0x80004005);
}

/// <summary>Interface IDs, encoded exactly as Steinberg's INLINE_UID does on Windows (COM-compatible layout).</summary>
internal static unsafe class Tuids
{
    public static readonly byte[] FUnknown = T(0x00000000, 0x00000000, 0xC0000000, 0x00000046);
    public static readonly byte[] IPluginBase = T(0x22888DDB, 0x156E45AE, 0x8358B348, 0x08190625);
    public static readonly byte[] IComponent = T(0xE831FF31, 0xF2D54301, 0x928EBBEE, 0x25697802);
    public static readonly byte[] IAudioProcessor = T(0x42043F99, 0xB7DA453C, 0xA569E79D, 0x9AAEC33D);
    public static readonly byte[] IProcessContextRequirements = T(0x2A654303, 0xEF764E3D, 0x95B5FE83, 0x730EF6D0);
    public static readonly byte[] IEditController = T(0xDCD7BBE3, 0x7742448D, 0xA874AACC, 0x979C759E);
    public static readonly byte[] IMidiMapping = T(0xDF0FF9F7, 0x49B74669, 0xB63AB732, 0x7ADBF5E5);
    public static readonly byte[] IPlugView = T(0x5BC32507, 0xD06049EA, 0xA6151B52, 0x2B755B29);
    public static readonly byte[] IPluginFactory = T(0x7A4D811C, 0x52114A1F, 0xAED9D2EE, 0x0B43BF9F);
    public static readonly byte[] IPluginFactory2 = T(0x0007B650, 0xF24B4C0B, 0xA464EDB9, 0xF00B2ABB);
    public static readonly byte[] IPluginFactory3 = T(0x4555A2AB, 0xC1234E57, 0x9B122910, 0x36878931);

    // Renaissance's own class ids ("Renaissance" / "Renaissance FX" spelled in ASCII).
    public static readonly byte[] InstrumentClass = T(0x52656E61, 0x69737361, 0x6E636549, 0x4E535452);
    public static readonly byte[] EffectClass = T(0x52656E61, 0x69737361, 0x6E636546, 0x58000000);

    public static byte[] T(uint l1, uint l2, uint l3, uint l4) =>
    [
        (byte)l1, (byte)(l1 >> 8), (byte)(l1 >> 16), (byte)(l1 >> 24),
        (byte)(l2 >> 16), (byte)(l2 >> 24), (byte)l2, (byte)(l2 >> 8),
        (byte)(l3 >> 24), (byte)(l3 >> 16), (byte)(l3 >> 8), (byte)l3,
        (byte)(l4 >> 24), (byte)(l4 >> 16), (byte)(l4 >> 8), (byte)l4,
    ];

    public static bool Eq(byte* p, byte[] tuid) => new ReadOnlySpan<byte>(p, 16).SequenceEqual(tuid);
}

/// <summary>Helpers for building COM-style objects out of native memory.</summary>
internal static unsafe class Com
{
    /// <summary>Allocates an interface block: [vtable pointer][GCHandle of the managed owner].</summary>
    public static IntPtr NewBlock(void** vtable, GCHandle owner)
    {
        var block = (IntPtr*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(IntPtr)));
        block[0] = (IntPtr)vtable;
        block[1] = GCHandle.ToIntPtr(owner);
        return (IntPtr)block;
    }

    public static T Owner<T>(IntPtr self) where T : class =>
        (T)GCHandle.FromIntPtr(((IntPtr*)self)[1]).Target!;

    public static void** Vtable(params IntPtr[] functions)
    {
        var table = (void**)NativeMemory.Alloc((nuint)(functions.Length * sizeof(void*)));
        for (var i = 0; i < functions.Length; i++) table[i] = (void*)functions[i];
        return table;
    }

    public static void PutAscii(byte* dest, int capacity, string text)
    {
        new Span<byte>(dest, capacity).Clear();
        var bytes = Encoding.ASCII.GetBytes(text);
        var n = Math.Min(bytes.Length, capacity - 1);
        for (var i = 0; i < n; i++) dest[i] = bytes[i];
    }

    public static void PutUtf16(char* dest, int capacity, string text)
    {
        new Span<char>(dest, capacity).Clear();
        var n = Math.Min(text.Length, capacity - 1);
        for (var i = 0; i < n; i++) dest[i] = text[i];
    }

    // Calls through a COM vtable slot of an object implemented by the host.
    public static void** VtableOf(IntPtr obj) => *(void***)obj;

    public static uint AddRef(IntPtr obj) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, uint>)VtableOf(obj)[1])(obj);

    public static uint Release(IntPtr obj) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, uint>)VtableOf(obj)[2])(obj);
}

internal static class Log
{
    public static void Write(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Renaissance", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "vst3.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
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

    public static string? Path()
    {
        const uint fromAddress = 0x4, unchangedRefCount = 0x2;
        var anchor = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&Exports.GetPluginFactory;
        if (!GetModuleHandleExW(fromAddress | unchangedRefCount, anchor, out var module)) return null;
        var buffer = stackalloc char[1024];
        var len = GetModuleFileNameW(module, buffer, 1024);
        return len == 0 ? null : new string(buffer, 0, (int)len);
    }
}
