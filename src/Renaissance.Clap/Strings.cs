using System.Runtime.InteropServices;
using System.Text;

namespace Renaissance.Clap;

/// <summary>UTF-8 helpers for the unmanaged strings CLAP hands across the ABI.</summary>
internal static unsafe class Strings
{
    /// <summary>Allocates a process-lifetime UTF-8 string. CLAP descriptors and extension ids live until deinit.</summary>
    public static byte* Utf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var p = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(p, bytes.Length));
        p[bytes.Length] = 0;
        return p;
    }

    /// <summary>Copies a UTF-8 string into a fixed CLAP char buffer and null-terminates it.</summary>
    public static void Write(byte* destination, int capacity, string value)
    {
        if (destination is null || capacity <= 0) return;
        var bytes = Encoding.UTF8.GetBytes(value);
        var count = Math.Min(bytes.Length, capacity - 1);
        for (var i = 0; i < count; i++) destination[i] = bytes[i];
        destination[count] = 0;
    }

    /// <summary>Compares a null-terminated UTF-8 C string with a managed string without allocating.</summary>
    public static bool Equals(byte* value, string expected)
    {
        if (value is null) return false;
        var bytes = Encoding.UTF8.GetBytes(expected);
        for (var i = 0; i < bytes.Length; i++)
            if (value[i] != bytes[i]) return false;
        return value[bytes.Length] == 0;
    }
}

/// <summary>Process-lifetime strings used by the CLAP descriptor and extension lookups.</summary>
internal static unsafe class ClapStrings
{
    public static readonly byte* PluginFactoryId = Strings.Utf8(ClapConstants.PluginFactoryId);

    public static readonly byte* ExtAudioPorts = Strings.Utf8(ClapConstants.ExtAudioPorts);
    public static readonly byte* ExtNotePorts = Strings.Utf8(ClapConstants.ExtNotePorts);
    public static readonly byte* ExtParams = Strings.Utf8(ClapConstants.ExtParams);
    public static readonly byte* ExtState = Strings.Utf8(ClapConstants.ExtState);
    public static readonly byte* ExtGui = Strings.Utf8(ClapConstants.ExtGui);
    public static readonly byte* ExtLatency = Strings.Utf8(ClapConstants.ExtLatency);

    public static readonly byte* PortStereo = Strings.Utf8(ClapConstants.PortStereo);
    public static readonly byte* WindowApiWin32 = Strings.Utf8(ClapConstants.WindowApiWin32);
}
