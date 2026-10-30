using System.Text.Json;

namespace Renaissance.PresetEngine;

/// <summary>Native Renaissance preset (.rpreset), stored as JSON.</summary>
public sealed class RPreset
{
    public const string Extension = ".rpreset";

    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public string PluginName { get; set; } = "";
    public string Vendor { get; set; } = "";
    public int UniqueId { get; set; }
    public int PluginVersion { get; set; }
    public string ProgramName { get; set; } = "";
    public float[] Parameters { get; set; } = [];
    public byte[]? Chunk { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool Favourite { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static RPreset FromJson(string json) =>
        JsonSerializer.Deserialize<RPreset>(json, Options) ?? throw new InvalidDataException("Empty preset.");

    public static RPreset Load(string path) => FromJson(File.ReadAllText(path));
    public void Save(string path) => File.WriteAllText(path, ToJson());
}
