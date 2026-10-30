using Renaissance.Core.Hosting;
using Renaissance.Core.Ipc;
using Renaissance.Core.Models;
using Renaissance.PluginDatabase;
using Serilog;

namespace Renaissance.Scanner;

/// <summary>
/// Discovers VST2 plugins. The scanner never loads a plugin itself (Rule 4): every candidate is probed in a
/// throw-away Host32/Host64 process so a bad plugin can only ever kill its own probe.
/// </summary>
public sealed class PluginScanner
{
    private readonly RenaissanceDatabase _db;
    private readonly ILogger _log = Log.ForContext<PluginScanner>();

    public PluginScanner(RenaissanceDatabase db) => _db = db;

    public static IEnumerable<string> EnumerateCandidates(IEnumerable<string> folders)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.dll", options))
                if (seen.Add(file)) yield return file;
        }
    }

    public async Task<ScanSummary> ScanAsync(
        IEnumerable<string> folders,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var folderList = folders.ToList();
        progress?.Report(new ScanProgress(0, 0, "", "Searching for plugins…", null));

        var candidates = await Task.Run(() =>
            EnumerateCandidates(folderList)
                .Select(f => (Path: f, Pe: PeInspector.Inspect(f)))
                .Where(x => x.Pe is { IsPe: true, ExportsVstEntry: true, Architecture: not PluginArchitecture.Unknown })
                .ToList(), ct).ConfigureAwait(false);

        int scanned = 0, skipped = 0, ok = 0, failed = 0, index = 0;
        var cancelled = false;

        foreach (var (path, pe) in candidates)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            index++;

            var info = new FileInfo(path);
            var existing = _db.GetPluginByPath(path);
            if (existing is { Status: PluginStatus.Disabled }
                || (!options.Full && existing is not null && existing.Status != PluginStatus.Missing
                    && existing.FileSize == info.Length && existing.LastWriteUtc == info.LastWriteTimeUtc))
            {
                skipped++;
                progress?.Report(new ScanProgress(index, candidates.Count, path, "Up to date", existing));
                continue;
            }

            progress?.Report(new ScanProgress(index, candidates.Count, path, "Scanning " + System.IO.Path.GetFileName(path), null));
            PluginInfo result;
            try
            {
                result = await ProbeAsync(path, pe.Architecture, info, options, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            scanned++;
            if (result.Status is PluginStatus.Ok or PluginStatus.Warning) ok++; else failed++;

            _db.UpsertPlugin(result);
            if (result.Status == PluginStatus.Crashed)
                _db.Record(new CrashRecord(result.Path, result.Name, DateTime.UtcNow, result.Details));
            progress?.Report(new ScanProgress(index, candidates.Count, path, result.Status.ToString(), _db.GetPluginByPath(path)));
        }

        var missing = MarkMissing();
        return new ScanSummary(scanned, skipped, ok, failed, missing, cancelled);
    }

    private int MarkMissing()
    {
        var count = 0;
        foreach (var plugin in _db.GetPlugins())
        {
            if (plugin.Status is PluginStatus.Missing or PluginStatus.Disabled || File.Exists(plugin.Path)) continue;
            _db.SetStatus(plugin.Path, PluginStatus.Missing, "File no longer exists.");
            count++;
        }
        return count;
    }

    private async Task<PluginInfo> ProbeAsync(string path, PluginArchitecture arch, FileInfo file, ScanOptions options, CancellationToken ct)
    {
        var info = new PluginInfo
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            Path = path,
            Architecture = arch,
            HostType = PluginInfo.HostFor(arch),
            FileSize = file.Length,
            LastWriteUtc = file.LastWriteTimeUtc,
            LastScanUtc = DateTime.UtcNow,
        };

        await using var client = new HostProcessClient(arch);
        using var registration = ct.Register(client.Kill);
        try
        {
            await client.StartAsync(ct).ConfigureAwait(false);
            var load = await client.SendAsync(
                new HostRequest { Command = HostCommands.Load, Path = path, Flag = options.HidePopups },
                options.PluginTimeout).ConfigureAwait(false);

            if (!load.Ok || load.Metadata is null)
            {
                info.Status = PluginStatus.Warning;
                info.Details = load.Error ?? "Plugin could not be loaded.";
                return info;
            }

            Apply(info, load.Metadata);

            if (options.TestEditors && load.Metadata.HasEditor)
            {
                var test = await client.SendAsync(new HostRequest { Command = HostCommands.EditorTest }, options.PluginTimeout).ConfigureAwait(false);
                if (!test.Ok)
                {
                    info.Status = PluginStatus.Warning;
                    info.Details = "Editor test failed: " + test.Error;
                }
                else info.Details = "Loaded and editor opened successfully.";
            }
            else info.Details = load.Metadata.HasEditor ? "Loaded successfully." : "Loaded successfully (no editor).";

            await client.SendAsync(new HostRequest { Command = HostCommands.Unload }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (HostCrashedException ex)
        {
            ct.ThrowIfCancellationRequested();
            info.Status = PluginStatus.Crashed;
            info.Details = "Plugin crashed its host process. " + ex.Message;
            _log.Warning("{Plugin} crashed during scan", path);
        }
        catch (TimeoutException ex)
        {
            client.Kill();
            info.Status = PluginStatus.Crashed;
            info.Details = "Plugin hung during scan. " + ex.Message;
            _log.Warning("{Plugin} hung during scan", path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested();
            info.Status = PluginStatus.Warning;
            info.Details = ex.Message;
        }
        return info;
    }

    private static void Apply(PluginInfo info, PluginMetadata m)
    {
        info.Name = string.IsNullOrWhiteSpace(m.Name) ? info.Name : m.Name;
        info.Vendor = m.Vendor;
        info.Version = m.VersionString;
        info.Type = PluginClassifier.Classify(m);
        info.UniqueId = m.UniqueId;
        info.NumParameters = m.NumParameters;
        info.NumPrograms = m.NumPrograms;
        info.NumInputs = m.NumInputs;
        info.NumOutputs = m.NumOutputs;
        info.HasEditor = m.HasEditor;
        info.IsSynth = m.IsSynth;
        info.Status = PluginStatus.Ok;
    }
}
