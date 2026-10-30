using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Renaissance.Core.Models;
using Renaissance.Scanner;
using Renaissance.UI.Services;
using Serilog;

namespace Renaissance.UI.ViewModels;

public sealed partial class ScanViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<PluginInfo, Task> _openPlugin;
    private readonly List<PluginRowViewModel> _all = [];
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _newFolderText = "";
    [ObservableProperty] private string? _selectedFolder;
    [ObservableProperty] private bool _testEditors = true;
    [ObservableProperty] private bool _hidePopups = true;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _selectedCategory = PluginCategories.All;
    [ObservableProperty] private bool _failedOnly;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusText = "Add a plugin folder, then press Scan.";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMax = 1;
    [ObservableProperty] private PluginRowViewModel? _selectedPlugin;

    public ScanViewModel(AppServices services, Func<PluginInfo, Task> openPlugin)
    {
        _services = services;
        _openPlugin = openPlugin;

        foreach (var folder in _services.Database.GetScanLocations()) Folders.Add(folder);
        if (Folders.Count == 0) AddDefaultFolders();
        Reload();
    }

    public ObservableCollection<string> Folders { get; } = [];
    public ObservableCollection<PluginRowViewModel> Plugins { get; } = [];
    public IReadOnlyList<string> Categories => PluginCategories.Filters;
    public bool IsIdle => !IsScanning;
    public string Summary => $"{Plugins.Count} of {_all.Count} plugins";

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();
    partial void OnFailedOnlyChanged(bool value) => ApplyFilter();

    private void AddDefaultFolders()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[]
        {
            @"C:\VSTPlugins",
            Path.Combine(pf, "VSTPlugins"),
            Path.Combine(pf86, "VSTPlugins"),
            Path.Combine(pf, "Steinberg", "VSTPlugins"),
            Path.Combine(pf86, "Steinberg", "VSTPlugins"),
            Path.Combine(pf, "Common Files", "VST2"),
            Path.Combine(pf86, "Native Instruments", "B4 II", "VSTPlugins"),
        };
        foreach (var folder in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            _services.Database.AddScanLocation(folder);
            Folders.Add(folder);
        }
    }

    // ---------------------------------------------------------------- list

    public void Reload()
    {
        var selectedPath = SelectedPlugin?.Info.Path;
        _all.Clear();
        _all.AddRange(_services.Database.GetPlugins().Select(p => new PluginRowViewModel(p)));
        ApplyFilter();
        SelectedPlugin = Plugins.FirstOrDefault(p => p.Info.Path == selectedPath);
    }

    private void UpsertRow(PluginInfo info)
    {
        var existing = _all.FirstOrDefault(r => string.Equals(r.Info.Path, info.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is null) _all.Add(new PluginRowViewModel(info));
        else existing.Update(info);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        IEnumerable<PluginRowViewModel> query = _all;

        if (FailedOnly) query = query.Where(r => r.IsFailed);
        if (SelectedCategory == PluginCategories.Instruments) query = query.Where(r => r.Info.Type == PluginCategories.Instruments);
        else if (SelectedCategory == PluginCategories.Effects)
            query = query.Where(r => r.Info.Type is not (PluginCategories.Instruments or PluginCategories.Midi));
        else if (SelectedCategory != PluginCategories.All) query = query.Where(r => r.Info.Type == SelectedCategory);

        if (search.Length > 0)
            query = query.Where(r =>
                r.Info.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Info.Vendor.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Info.Type.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Info.Tags.Contains(search, StringComparison.OrdinalIgnoreCase));

        var selected = SelectedPlugin?.Info.Path;
        Plugins.Clear();
        foreach (var row in query.OrderBy(r => r.Info.Name, StringComparer.OrdinalIgnoreCase)) Plugins.Add(row);
        SelectedPlugin = Plugins.FirstOrDefault(p => p.Info.Path == selected);
        OnPropertyChanged(nameof(Summary));
    }

    // ---------------------------------------------------------------- folders

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var folder = await _services.Dialogs.PickFolderAsync("Choose a VST plugin folder");
        if (folder is not null) NewFolderText = folder;
    }

    [RelayCommand]
    private void AddFolder()
    {
        var path = NewFolderText.Trim().Trim('"');
        if (path.Length == 0) return;
        if (Folders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase))) return;
        _services.Database.AddScanLocation(path);
        Folders.Add(path);
        NewFolderText = "";
        if (!Directory.Exists(path)) StatusText = $"Note: '{path}' is not reachable right now.";
    }

    [RelayCommand]
    private void RemoveFolder()
    {
        if (SelectedFolder is null) return;
        _services.Database.RemoveScanLocation(SelectedFolder);
        Folders.Remove(SelectedFolder);
    }

    // ---------------------------------------------------------------- scanning

    [RelayCommand]
    private Task ScanAsync() => RunScanAsync(full: false);

    [RelayCommand]
    private Task RescanAllAsync() => RunScanAsync(full: true);

    [RelayCommand]
    private void Stop() => CancelScan();

    public void CancelScan()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>"Scan Failed" shows only plugins that failed, so problems are easy to review.</summary>
    [RelayCommand]
    private void ScanFailed() => FailedOnly = !FailedOnly;

    private async Task RunScanAsync(bool full)
    {
        if (IsScanning) return;
        if (Folders.Count == 0)
        {
            StatusText = "Add a plugin folder first.";
            return;
        }

        _cts = new CancellationTokenSource();
        IsScanning = true;
        ProgressValue = 0;
        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressMax = Math.Max(1, p.Total);
            ProgressValue = p.Current;
            StatusText = p.Total > 0 ? $"[{p.Current}/{p.Total}] {p.Message}" : p.Message;
            if (p.Result is not null) UpsertRow(p.Result);
        });

        try
        {
            var options = new ScanOptions { TestEditors = TestEditors, HidePopups = HidePopups, Full = full };
            var summary = await _services.Scanner.ScanAsync(Folders.ToList(), options, progress, _cts.Token);
            StatusText = summary.Cancelled
                ? "Scan stopped."
                : $"Scan complete: {summary.Scanned} scanned, {summary.Skipped} unchanged, {summary.Failed} failed, {summary.Missing} missing.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Scan failed");
            StatusText = "Scan failed: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
            Reload();
        }
    }

    // ---------------------------------------------------------------- row actions

    [RelayCommand]
    private async Task OpenPluginAsync(PluginRowViewModel? row)
    {
        row ??= SelectedPlugin;
        if (row is null || IsScanning) return;
        if (row.Info.Status is PluginStatus.Missing or PluginStatus.Disabled)
        {
            StatusText = row.Info.Status == PluginStatus.Missing ? "That plugin file is missing." : "That plugin is disabled.";
            return;
        }
        await _openPlugin(row.Info);
    }

    [RelayCommand]
    private void ToggleFavourite()
    {
        if (SelectedPlugin is not { } row) return;
        row.Info.Favourite = !row.Info.Favourite;
        SaveMetadata(row);
    }

    [RelayCommand]
    private void ToggleDisabled()
    {
        if (SelectedPlugin is not { } row) return;
        var disable = row.Info.Status != PluginStatus.Disabled;
        _services.Database.SetStatus(row.Info.Path, disable ? PluginStatus.Disabled : PluginStatus.Ok, disable ? "Disabled by user." : "");
        Reload();
    }

    [RelayCommand]
    private async Task EditTagsAsync()
    {
        if (SelectedPlugin is not { } row) return;
        var tags = await _services.Dialogs.PromptAsync("Tags", "Comma separated tags for " + row.Info.Name, row.Info.Tags);
        if (tags is null) return;
        row.Info.Tags = tags;
        SaveMetadata(row);
    }

    [RelayCommand]
    private async Task SetIconAsync()
    {
        if (SelectedPlugin is not { } row) return;
        var file = await _services.Dialogs.PickOpenFileAsync("Choose a plugin icon",
            new FileTypeFilter("Images", "*.png", "*.svg", "*.ico"));
        if (file is null) return;
        row.Info.IconPath = file;
        SaveMetadata(row);
    }

    [RelayCommand]
    private void RemoveFromList()
    {
        if (SelectedPlugin is not { } row) return;
        _services.Database.DeletePlugin(row.Info.Path);
        Reload();
    }

    private void SaveMetadata(PluginRowViewModel row)
    {
        _services.Database.SaveMetadata(row.Info.Id, row.Info.IconPath, row.Info.Favourite, row.Info.Tags);
        row.Update(row.Info);
        ApplyFilter();
    }
}
