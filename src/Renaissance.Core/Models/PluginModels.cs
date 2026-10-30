namespace Renaissance.Core.Models;

public enum PluginStatus { Ok, Warning, Crashed, Missing, Disabled }

public enum PluginArchitecture { X86, X64, Unknown }

public enum HostType { Host32, Host64 }

public static class PluginCategories
{
    public const string All = "All";
    public const string Instruments = "Instruments";
    public const string Effects = "Effects";
    public const string Dynamics = "Dynamics";
    public const string EQ = "EQ";
    public const string Delay = "Delay";
    public const string Reverb = "Reverb";
    public const string Utility = "Utility";
    public const string Midi = "MIDI";
    public const string Analyzer = "Analyzer";

    public static readonly IReadOnlyList<string> Filters =
        [All, Instruments, Effects, Dynamics, EQ, Delay, Reverb, Utility, Midi, Analyzer];
}

public sealed class PluginInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Version { get; set; } = "";
    public string Type { get; set; } = PluginCategories.Effects;
    public PluginArchitecture Architecture { get; set; } = PluginArchitecture.Unknown;
    public HostType HostType { get; set; } = HostType.Host64;
    public string Path { get; set; } = "";
    public PluginStatus Status { get; set; } = PluginStatus.Ok;
    public string Details { get; set; } = "";
    public int UniqueId { get; set; }
    public int NumParameters { get; set; }
    public int NumPrograms { get; set; }
    public int NumInputs { get; set; }
    public int NumOutputs { get; set; }
    public bool HasEditor { get; set; }
    public bool IsSynth { get; set; }
    public long FileSize { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public DateTime LastScanUtc { get; set; }

    public string? IconPath { get; set; }
    public bool Favourite { get; set; }
    public string Tags { get; set; } = "";

    public string BitsLabel => Architecture switch
    {
        PluginArchitecture.X86 => "32-bit",
        PluginArchitecture.X64 => "64-bit",
        _ => "?",
    };

    public static HostType HostFor(PluginArchitecture arch) =>
        arch == PluginArchitecture.X86 ? HostType.Host32 : HostType.Host64;
}

/// <summary>Metadata read from a VST2 plugin by a host process.</summary>
public sealed class PluginMetadata
{
    public string Name { get; set; } = "";
    public string Product { get; set; } = "";
    public string Vendor { get; set; } = "";
    public int VendorVersion { get; set; }
    public int VstVersion { get; set; }
    public int UniqueId { get; set; }
    public int NumParameters { get; set; }
    public int NumPrograms { get; set; }
    public int NumInputs { get; set; }
    public int NumOutputs { get; set; }
    public int Flags { get; set; }
    public int Category { get; set; }
    public bool HasEditor { get; set; }
    public bool IsSynth { get; set; }
    public bool ProgramChunks { get; set; }
    public bool CanReplacing { get; set; }
    public bool ReceivesMidi { get; set; }
    public bool SendsMidi { get; set; }

    public string VersionString => FormatVersion(VendorVersion);

    public static string FormatVersion(int v)
    {
        if (v <= 0) return "";
        if (v < 1000) return v.ToString();
        var s = $"{v / 1000}.{v / 100 % 10}.{v / 10 % 10}.{v % 10}";
        while (s.EndsWith(".0")) s = s[..^2];
        return s;
    }
}

public sealed class ParameterInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Display { get; set; } = "";
    public float Value { get; set; }
    public bool Automatable { get; set; } = true;

    public string DisplayWithLabel => string.IsNullOrWhiteSpace(Label) ? Display : $"{Display} {Label}";
}

public sealed record CrashRecord(string PluginPath, string PluginName, DateTime TimeUtc, string Message);

public interface ICrashLog
{
    void Record(CrashRecord record);
}
