using System.Text.Json;

namespace Renaissance.PresetEngine;

public sealed record PresetEntry(string Name, string Path, DateTime ModifiedUtc);

/// <summary>Stores .rpreset files in per-plugin folders and tracks recently used presets.</summary>
public sealed class PresetStore
{
    private readonly string _root;
    private readonly object _recentLock = new();

    public PresetStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public string FolderFor(string vendor, string pluginName, int uniqueId)
    {
        var folder = Path.Combine(_root, Sanitize(vendor), $"{Sanitize(pluginName)}-{uniqueId:X8}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public IReadOnlyList<PresetEntry> List(string vendor, string pluginName, int uniqueId)
    {
        var folder = FolderFor(vendor, pluginName, uniqueId);
        return Directory.EnumerateFiles(folder, "*" + RPreset.Extension)
            .Select(f => new PresetEntry(System.IO.Path.GetFileNameWithoutExtension(f), f, File.GetLastWriteTimeUtc(f)))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string Save(RPreset preset, string? existingPath = null)
    {
        var path = existingPath ?? System.IO.Path.Combine(
            FolderFor(preset.Vendor, preset.PluginName, preset.UniqueId),
            Sanitize(preset.Name) + RPreset.Extension);
        preset.Save(path);
        return path;
    }

    public RPreset Load(string path) => RPreset.Load(path);

    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public string Rename(string path, string newName)
    {
        var preset = RPreset.Load(path);
        preset.Name = newName;
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, Sanitize(newName) + RPreset.Extension);
        if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
            throw new IOException($"A preset named '{newName}' already exists.");
        preset.Save(target);
        if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
        return target;
    }

    private string RecentFile => System.IO.Path.Combine(_root, "recent.json");

    public IReadOnlyList<string> GetRecent(int uniqueId)
    {
        lock (_recentLock)
        {
            return ReadRecent().TryGetValue(uniqueId.ToString(), out var list)
                ? list.Where(File.Exists).ToList()
                : [];
        }
    }

    public void AddRecent(int uniqueId, string path, int max = 8)
    {
        lock (_recentLock)
        {
            var all = ReadRecent();
            var key = uniqueId.ToString();
            var list = all.TryGetValue(key, out var l) ? l : [];
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            all[key] = list;
            File.WriteAllText(RecentFile, JsonSerializer.Serialize(all));
        }
    }

    private Dictionary<string, List<string>> ReadRecent()
    {
        try
        {
            return File.Exists(RecentFile)
                ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(RecentFile)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unnamed";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Unnamed" : cleaned;
    }
}
