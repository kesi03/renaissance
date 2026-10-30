using System.Text.Json;

namespace Renaissance.Core.Models;

/// <summary>
/// Plugin state maintained by Renaissance itself (not solely by the hosted plugin).
/// Serialised into the DAW project so it survives save/load and host restarts.
/// </summary>
public sealed class PluginState
{
    public int FormatVersion { get; set; } = 1;
    public string PluginPath { get; set; } = "";
    public PluginArchitecture Architecture { get; set; } = PluginArchitecture.Unknown;
    public int UniqueId { get; set; }
    public string PluginName { get; set; } = "";
    public int Program { get; set; }
    public string ProgramName { get; set; } = "";
    public float[] Parameters { get; set; } = [];
    public byte[]? Chunk { get; set; }
    public string? PresetPath { get; set; }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static PluginState FromBytes(ReadOnlySpan<byte> data) =>
        JsonSerializer.Deserialize<PluginState>(data) ?? throw new InvalidDataException("Empty plugin state.");

    public PluginState Clone() => new()
    {
        FormatVersion = FormatVersion,
        PluginPath = PluginPath,
        Architecture = Architecture,
        UniqueId = UniqueId,
        PluginName = PluginName,
        Program = Program,
        ProgramName = ProgramName,
        Parameters = (float[])Parameters.Clone(),
        Chunk = Chunk is null ? null : (byte[])Chunk.Clone(),
        PresetPath = PresetPath,
    };
}
