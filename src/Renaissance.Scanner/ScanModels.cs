using Renaissance.Core.Models;

namespace Renaissance.Scanner;

public sealed record ScanOptions
{
    /// <summary>Open and close each plugin's editor to prove it works.</summary>
    public bool TestEditors { get; init; } = true;

    /// <summary>Suppress splash, activation and nag windows created by plugins.</summary>
    public bool HidePopups { get; init; } = true;

    /// <summary>Ignore the incremental cache and re-probe every plugin.</summary>
    public bool Full { get; init; }

    public TimeSpan PluginTimeout { get; init; } = TimeSpan.FromSeconds(45);
}

public sealed record ScanProgress(int Current, int Total, string Path, string Message, PluginInfo? Result);

public sealed record ScanSummary(int Scanned, int Skipped, int Ok, int Failed, int Missing, bool Cancelled);
