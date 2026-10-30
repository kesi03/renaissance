using Renaissance.Core.Models;
using Renaissance.PluginDatabase;
using Xunit;

namespace Renaissance.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "renaissance-db-" + Guid.NewGuid().ToString("N"));
    private readonly RenaissanceDatabase _db;

    public DatabaseTests() => _db = new RenaissanceDatabase(Path.Combine(_dir, "test.db"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static PluginInfo Make(string path = @"C:\VST\Synth1.dll") => new()
    {
        Name = "Synth1", Vendor = "Ichiro Toda", Version = "1.13", Type = PluginCategories.Instruments,
        Architecture = PluginArchitecture.X86, HostType = HostType.Host32, Path = path, Status = PluginStatus.Ok,
        UniqueId = 1234, NumParameters = 32, NumPrograms = 128, HasEditor = true, IsSynth = true,
        FileSize = 1000, LastWriteUtc = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), LastScanUtc = DateTime.UtcNow,
    };

    [Fact]
    public void Upsert_inserts_then_updates_same_path()
    {
        var id1 = _db.UpsertPlugin(Make());
        var changed = Make();
        changed.Version = "1.14";
        var id2 = _db.UpsertPlugin(changed);

        Assert.Equal(id1, id2);
        var all = _db.GetPlugins();
        Assert.Single(all);
        Assert.Equal("1.14", all[0].Version);
        Assert.Equal(PluginArchitecture.X86, all[0].Architecture);
        Assert.Equal(HostType.Host32, all[0].HostType);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), all[0].LastWriteUtc);
    }

    [Fact]
    public void Path_lookup_is_case_insensitive()
    {
        _db.UpsertPlugin(Make());
        Assert.NotNull(_db.GetPluginByPath(@"c:\vst\SYNTH1.DLL"));
    }

    [Fact]
    public void Metadata_survives_rescan_upsert()
    {
        var id = _db.UpsertPlugin(Make());
        _db.SaveMetadata(id, @"C:\icons\s.png", favourite: true, tags: "lead, analog");
        _db.UpsertPlugin(Make());

        var p = _db.GetPlugins().Single();
        Assert.True(p.Favourite);
        Assert.Equal("lead, analog", p.Tags);
        Assert.Equal(@"C:\icons\s.png", p.IconPath);
    }

    [Fact]
    public void Deleting_plugin_cascades_metadata()
    {
        var id = _db.UpsertPlugin(Make());
        _db.SaveMetadata(id, null, true, "x");
        _db.DeletePlugin(Make().Path);
        Assert.Empty(_db.GetPlugins());
    }

    [Fact]
    public void Scan_locations_are_unique_and_removable()
    {
        _db.AddScanLocation(@"C:\VSTPlugins");
        _db.AddScanLocation(@"c:\vstplugins");
        _db.AddScanLocation(@"\\nas\plugins");
        Assert.Equal(2, _db.GetScanLocations().Count);

        _db.RemoveScanLocation(@"C:\VSTPlugins");
        Assert.Equal([@"\\nas\plugins"], _db.GetScanLocations());
    }

    [Fact]
    public void Crash_is_logged_and_marks_plugin_crashed()
    {
        _db.UpsertPlugin(Make());
        _db.Record(new CrashRecord(Make().Path, "Synth1", DateTime.UtcNow, "boom"));

        Assert.Equal(PluginStatus.Crashed, _db.GetPlugins().Single().Status);
        var history = _db.GetCrashes(Make().Path);
        Assert.Single(history);
        Assert.Equal("boom", history[0].Message);
    }

    [Fact]
    public void Disabled_status_is_not_overwritten_by_crash()
    {
        _db.UpsertPlugin(Make());
        _db.SetStatus(Make().Path, PluginStatus.Disabled, "user");
        _db.Record(new CrashRecord(Make().Path, "Synth1", DateTime.UtcNow, "boom"));
        Assert.Equal(PluginStatus.Disabled, _db.GetPlugins().Single().Status);
    }
}
