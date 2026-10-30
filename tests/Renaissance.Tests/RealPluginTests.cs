using Renaissance.Core.Models;
using Renaissance.Core.Session;
using Renaissance.PluginDatabase;
using Renaissance.Scanner;
using Xunit;

namespace Renaissance.Tests;

/// <summary>
/// Runs against real, installed 32-bit VST2 plugins (Native Instruments B4 II). Each test is a no-op on machines
/// where the plugins are not installed.
/// </summary>
[Collection("RealPlugins")]
public sealed class RealPluginTests : IDisposable
{
    private const string B4Folder = @"C:\Program Files (x86)\Native Instruments\B4 II\VSTPlugins";
    private static readonly string B4Synth = Path.Combine(B4Folder, "B4 II.dll");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "renaissance-real-" + Guid.NewGuid().ToString("N"));

    public RealPluginTests() =>
        Environment.SetEnvironmentVariable(Core.Hosting.HostLocator.EnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "Hosts"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static bool Installed => File.Exists(B4Synth);

    [Fact]
    public async Task Scanner_finds_both_32bit_plugins_through_Host32()
    {
        if (!Installed) return;
        var db = new RenaissanceDatabase(Path.Combine(_dir, "scan.db"));
        var scanner = new PluginScanner(db);

        var summary = await scanner.ScanAsync([B4Folder], new ScanOptions { TestEditors = false, HidePopups = true });

        Assert.False(summary.Cancelled);
        var plugins = db.GetPlugins();
        Assert.Equal(2, plugins.Count);
        Assert.All(plugins, p =>
        {
            Assert.Equal(PluginArchitecture.X86, p.Architecture);
            Assert.Equal(HostType.Host32, p.HostType);
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.True(p.Status is PluginStatus.Ok or PluginStatus.Warning or PluginStatus.Crashed, p.Details);
        });

        var synth = plugins.Single(p => p.Path.EndsWith("B4 II.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PluginStatus.Ok, synth.Status);
        Assert.Contains("Native Instruments", synth.Vendor, StringComparison.OrdinalIgnoreCase);
        Assert.True(synth.IsSynth);
        Assert.Equal(PluginCategories.Instruments, synth.Type);
        Assert.True(synth.NumParameters > 0);
    }

    [Fact]
    public async Task Incremental_scan_skips_unchanged_plugins()
    {
        if (!Installed) return;
        var db = new RenaissanceDatabase(Path.Combine(_dir, "inc.db"));
        var scanner = new PluginScanner(db);
        var options = new ScanOptions { TestEditors = false };

        await scanner.ScanAsync([B4Folder], options);
        var second = await scanner.ScanAsync([B4Folder], options);
        var full = await scanner.ScanAsync([B4Folder], options with { Full = true });

        Assert.Equal(0, second.Scanned);
        Assert.Equal(2, second.Skipped);
        Assert.Equal(2, full.Scanned);
    }

    [Fact]
    public async Task Session_loads_plugin_reads_parameters_programs_and_roundtrips_state()
    {
        if (!Installed) return;
        var info = new PluginInfo
        {
            Name = "B4 II", Path = B4Synth, Architecture = PluginArchitecture.X86, HostType = HostType.Host32,
        };

        await using var session = new PluginSession(info) { HidePopups = true };
        await session.LoadAsync();

        Assert.NotNull(session.Metadata);
        Assert.True(session.Parameters.Count > 0);
        Assert.Equal(session.Metadata!.NumParameters, session.Parameters.Count);

        // FXP capture/apply roundtrip through the real plugin.
        var fxp = await session.CaptureFxpAsync("Test");
        Assert.Equal(session.Metadata.UniqueId, fxp.FxId);
        var bytes = fxp.ToBytes();
        await session.ApplyFxpAsync(PresetEngine.FxpFile.FromBytes(bytes));

        // Parameter change is reflected and kept in Renaissance's own maintained state.
        await session.SetParameterAsync(0, 0.25f);
        var state = await session.SnapshotAsync();
        Assert.Equal(0.25f, state.Parameters[0], 2);

        var restored = PluginState.FromBytes(state.ToBytes());
        Assert.Equal(B4Synth, restored.PluginPath);
        Assert.Equal(PluginArchitecture.X86, restored.Architecture);
    }

    [Fact]
    public async Task Host_crash_is_survived_and_state_is_restored_on_automatic_restart()
    {
        if (!Installed) return;
        var info = new PluginInfo { Name = "B4 II", Path = B4Synth, Architecture = PluginArchitecture.X86, HostType = HostType.Host32 };
        var crashes = new List<CrashRecord>();

        await using var session = new PluginSession(info, new CollectingCrashLog(crashes)) { HidePopups = true };
        await session.LoadAsync();
        await session.SetParameterAsync(0, 0.75f);
        await session.SnapshotAsync();

        var recovered = new TaskCompletionSource();
        session.Recovered += () => recovered.TrySetResult();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("Renaissance.Host32")) p.Kill();

        Assert.Same(recovered.Task, await Task.WhenAny(recovered.Task, Task.Delay(30000)));
        Assert.Single(crashes);
        await session.ReloadParametersAsync();
        Assert.Equal(0.75f, session.Parameters[0].Value, 2);
    }

    private sealed class CollectingCrashLog(List<CrashRecord> list) : ICrashLog
    {
        public void Record(CrashRecord record) => list.Add(record);
    }
}
