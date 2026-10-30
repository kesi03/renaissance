using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Renaissance.Core.Session;
using Renaissance.PresetEngine;
using Renaissance.UI.Services;
using Serilog;

namespace Renaissance.UI.ViewModels;

public enum PatchKind { Program, Preset }

public sealed record PatchItem(PatchKind Kind, string Display, string Group, int Index = -1, string? Path = null);

public sealed partial class PluginViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppServices _services;
    private readonly Func<Task> _returnToScan;
    private string? _currentPresetPath;
    private readonly List<PatchItem> _allPatches = [];
    private bool _disposed;

    [ObservableProperty] private string _patchName = "";
    [ObservableProperty] private string _patchFilter = "";
    [ObservableProperty] private string _banner = "";
    [ObservableProperty] private double _editorWidth = 640;
    [ObservableProperty] private double _editorHeight = 400;
    [ObservableProperty] private bool _editorReady;

    public PluginViewModel(PluginSession session, AppServices services, Func<Task> returnToScan)
    {
        Session = session;
        _services = services;
        _returnToScan = returnToScan;

        session.Crashed += reason => Post(() => Banner = $"The plugin crashed ({reason}). Restarting and restoring its state…");
        session.Recovered += () => Post(() =>
        {
            Banner = "Plugin restarted and state restored.";
            RefreshPatches();
        });
        session.Faulted += () => Post(() => Banner = "The plugin keeps crashing and was stopped. Unload it and check the crash log.");
        session.ParametersRefreshed += () => Post(RefreshPatchName);
        session.EditorResizeRequested += (w, h) => Post(() =>
        {
            EditorWidth = w / _scale;
            EditorHeight = h / _scale;
        });

        RefreshPatches();
    }

    public PluginSession Session { get; }
    public ObservableCollection<PatchItem> FilteredPatches { get; } = [];

    public string Title => Session.Info.Name;
    public string Subtitle => string.Join("   ·   ", new[]
    {
        string.IsNullOrWhiteSpace(Session.Metadata?.VersionString) ? null : "Version " + Session.Metadata!.VersionString,
        string.IsNullOrWhiteSpace(Session.Metadata?.Vendor) ? Session.Info.Vendor : Session.Metadata!.Vendor,
        Session.Info.BitsLabel,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public bool HasEditor => Session.Metadata?.HasEditor ?? false;
    public bool HasNoEditor => !HasEditor;
    public bool CanDelete => _currentPresetPath is not null;

    partial void OnPatchFilterChanged(string value) => ApplyPatchFilter();

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    // ---------------------------------------------------------------- editor

    // The plugin host reports editor sizes in physical pixels; layout is in device-independent units.
    private double _scale = 1;

    public async Task AttachEditorAsync(IntPtr parentHandle, double scale = 1)
    {
        if (!HasEditor || _disposed) return;
        _scale = scale > 0 ? scale : 1;
        try
        {
            var (w, h) = await Session.OpenEditorAsync(parentHandle.ToInt64());
            EditorWidth = Math.Max(64, w / _scale);
            EditorHeight = Math.Max(64, h / _scale);
            EditorReady = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Editor failed to open");
            Banner = "The plugin editor could not be opened: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- patches

    private void RefreshPatches()
    {
        _allPatches.Clear();
        var meta = Session.Metadata;
        if (meta is not null)
        {
            var presets = _services.Presets.List(meta.Vendor, string.IsNullOrWhiteSpace(meta.Name) ? Session.Info.Name : meta.Name, meta.UniqueId);
            var recent = _services.Presets.GetRecent(meta.UniqueId);
            foreach (var path in recent)
                _allPatches.Add(new PatchItem(PatchKind.Preset, System.IO.Path.GetFileNameWithoutExtension(path), "Recent", Path: path));
            foreach (var p in presets)
                _allPatches.Add(new PatchItem(PatchKind.Preset, p.Name, "Preset", Path: p.Path));
            for (var i = 0; i < Session.Programs.Count; i++)
                _allPatches.Add(new PatchItem(PatchKind.Program, $"{i + 1:000}  {Session.Programs[i]}", "Program", Index: i));
        }
        ApplyPatchFilter();
        RefreshPatchName();
    }

    private void ApplyPatchFilter()
    {
        var filter = PatchFilter.Trim();
        FilteredPatches.Clear();
        foreach (var p in _allPatches)
            if (filter.Length == 0 || p.Display.Contains(filter, StringComparison.OrdinalIgnoreCase))
                FilteredPatches.Add(p);
    }

    private void RefreshPatchName()
    {
        if (_currentPresetPath is not null)
        {
            PatchName = System.IO.Path.GetFileNameWithoutExtension(_currentPresetPath);
        }
        else
        {
            var i = Session.CurrentProgram;
            PatchName = i >= 0 && i < Session.Programs.Count ? $"{i + 1:000}  {Session.Programs[i]}" : "(no program)";
        }
        OnPropertyChanged(nameof(CanDelete));
    }

    [RelayCommand]
    private async Task SelectPatchAsync(PatchItem? item)
    {
        if (item is null) return;
        await Guard(async () =>
        {
            if (item.Kind == PatchKind.Program)
            {
                _currentPresetPath = null;
                await Session.SelectProgramAsync(item.Index);
            }
            else if (item.Path is not null)
            {
                await ApplyPresetFileAsync(item.Path);
            }
            RefreshPatchName();
        });
    }

    private async Task ApplyPresetFileAsync(string path)
    {
        var preset = _services.Presets.Load(path);
        await Session.ApplyRPresetAsync(preset);
        _currentPresetPath = path;
        _services.Presets.AddRecent(Session.Metadata!.UniqueId, path);
        Session.State.PresetPath = path;
        RefreshPatches();
    }

    // ---------------------------------------------------------------- actions menu

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_currentPresetPath is null)
        {
            await SaveAsAsync();
            return;
        }
        await Guard(async () =>
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(_currentPresetPath);
            var preset = await Session.CaptureRPresetAsync(name);
            _services.Presets.Save(preset, _currentPresetPath);
            Banner = $"Saved '{name}'.";
        });
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var suggested = _currentPresetPath is null
            ? Session.Programs.ElementAtOrDefault(Session.CurrentProgram)?.Trim() ?? "New Preset"
            : System.IO.Path.GetFileNameWithoutExtension(_currentPresetPath);
        var name = await _services.Dialogs.PromptAsync("Save As", "Preset name:", suggested);
        if (name is null) return;
        await Guard(async () =>
        {
            var preset = await Session.CaptureRPresetAsync(name);
            var path = _services.Presets.Save(preset);
            _currentPresetPath = path;
            _services.Presets.AddRecent(Session.Metadata!.UniqueId, path);
            Session.State.PresetPath = path;
            RefreshPatches();
            Banner = $"Saved '{name}'.";
        });
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var file = await _services.Dialogs.PickOpenFileAsync("Load preset", new FileTypeFilter("Renaissance presets", "*.rpreset"));
        if (file is null) return;
        await Guard(() => ApplyPresetFileAsync(file));
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        var current = _currentPresetPath is null
            ? Session.Programs.ElementAtOrDefault(Session.CurrentProgram)?.Trim() ?? ""
            : System.IO.Path.GetFileNameWithoutExtension(_currentPresetPath);
        var name = await _services.Dialogs.PromptAsync("Rename", "New name:", current);
        if (name is null) return;
        await Guard(async () =>
        {
            if (_currentPresetPath is not null) _currentPresetPath = _services.Presets.Rename(_currentPresetPath, name);
            else await Session.RenameProgramAsync(name);
            RefreshPatches();
        });
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_currentPresetPath is null)
        {
            await _services.Dialogs.ShowMessageAsync("Delete", "Only Renaissance presets can be deleted. Plugin programs are part of the plugin.");
            return;
        }
        var name = System.IO.Path.GetFileNameWithoutExtension(_currentPresetPath);
        if (!await _services.Dialogs.ConfirmAsync("Delete preset", $"Delete '{name}'?")) return;
        await Guard(() =>
        {
            _services.Presets.Delete(_currentPresetPath);
            _currentPresetPath = null;
            RefreshPatches();
            return Task.CompletedTask;
        });
    }

    private static readonly FileTypeFilter FxpFilter = new("VST programs", "*.fxp");
    private static readonly FileTypeFilter FxbFilter = new("VST banks", "*.fxb");

    [RelayCommand]
    private async Task ImportFxpAsync()
    {
        var file = await _services.Dialogs.PickOpenFileAsync("Import FXP", FxpFilter);
        if (file is null) return;
        await Guard(async () =>
        {
            await Session.ApplyFxpAsync(FxpFile.Load(file));
            _currentPresetPath = null;
            RefreshPatches();
        });
    }

    [RelayCommand]
    private async Task ExportFxpAsync()
    {
        var file = await _services.Dialogs.PickSaveFileAsync("Export FXP", PresetStore.Sanitize(PatchName), "fxp", FxpFilter);
        if (file is null) return;
        await Guard(async () => (await Session.CaptureFxpAsync(_currentPresetPath is null ? null : PatchName)).Save(file));
    }

    [RelayCommand]
    private async Task ImportFxbAsync()
    {
        var file = await _services.Dialogs.PickOpenFileAsync("Import FXB", FxbFilter);
        if (file is null) return;
        await Guard(async () =>
        {
            await Session.ApplyFxbAsync(FxbFile.Load(file));
            _currentPresetPath = null;
            RefreshPatches();
        });
    }

    [RelayCommand]
    private async Task ExportFxbAsync()
    {
        var file = await _services.Dialogs.PickSaveFileAsync("Export FXB", PresetStore.Sanitize(Title), "fxb", FxbFilter);
        if (file is null) return;
        await Guard(async () => (await Session.CaptureFxbAsync()).Save(file));
    }

    // ---------------------------------------------------------------- navigation

    [RelayCommand]
    private void ShowParams()
    {
        _services.Dialogs.ShowParams(new ParamsViewModel(Session));
    }

    [RelayCommand]
    private Task BackAsync() => _returnToScan();

    [RelayCommand]
    private Task UnloadAsync() => _returnToScan();

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Plugin action failed");
            await _services.Dialogs.ShowMessageAsync("Renaissance", ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        (_services.Dialogs as DialogService)?.CloseParams();
        await Session.DisposeAsync();
    }
}
