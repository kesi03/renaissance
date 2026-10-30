using CommunityToolkit.Mvvm.ComponentModel;
using Renaissance.Core;
using Renaissance.Core.Models;
using Renaissance.Core.Session;
using Renaissance.Core.Wrapper;
using Renaissance.UI.Services;
using Serilog;

namespace Renaissance.UI.ViewModels;

/// <summary>Hosts the two primary views: Scan View and Plugin View.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty]
    private ObservableObject _current;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Scan = new ScanViewModel(services, info => OpenAsync(info));
        _current = Scan;
    }

    public ScanViewModel Scan { get; }

    /// <summary>Raised after a plugin has loaded (embedded mode connects its audio bridge here).</summary>
    public event Action<PluginSession>? SessionLoaded;

    /// <summary>Raised after the plugin view was closed and the plugin unloaded.</summary>
    public event Action? SessionUnloaded;

    public async Task OpenAsync(PluginInfo info, PluginState? restore = null)
    {
        if (Current is PluginViewModel) return;

        var session = new PluginSession(info, _services.Database);
        try
        {
            Scan.StatusText = $"Loading {info.Name}…";
            await session.LoadAsync(restore);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load {Plugin}", info.Path);
            await session.DisposeAsync();
            var status = ex is Core.Hosting.HostCrashedException or TimeoutException ? PluginStatus.Crashed : PluginStatus.Warning;
            _services.Database.SetStatus(info.Path, status, ex.Message);
            Scan.Reload();
            Scan.StatusText = $"Could not load {info.Name}: {ex.Message}";
            await _services.Dialogs.ShowMessageAsync("Plugin failed to load", $"{info.Name}\n\n{ex.Message}");
            return;
        }

        var vm = new PluginViewModel(session, _services, ReturnToScanAsync);
        Current = vm;
        Scan.StatusText = $"{info.Name} loaded.";
        SessionLoaded?.Invoke(session);
    }

    /// <summary>Restores a plugin from DAW project state, replacing whatever is loaded now.</summary>
    public async Task OpenStateAsync(PluginState state)
    {
        if (Current is PluginViewModel loaded)
        {
            if (string.Equals(loaded.Session.Info.Path, state.PluginPath, StringComparison.OrdinalIgnoreCase))
            {
                await loaded.Session.RestoreStateAsync(state);
                return;
            }
            await ReturnToScanAsync();
        }
        if (!File.Exists(state.PluginPath))
        {
            Scan.StatusText = $"The plugin from the saved project is missing: {state.PluginPath}";
            return;
        }
        await OpenAsync(WrapperEngine.DescribeFile(state.PluginPath), state);
    }

    private async Task ReturnToScanAsync()
    {
        if (Current is PluginViewModel plugin)
        {
            Current = Scan;
            await plugin.DisposeAsync();
            SessionUnloaded?.Invoke();
        }
        Scan.Reload();
    }

    public void Shutdown()
    {
        if (Current is PluginViewModel plugin)
        {
            try { plugin.Session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(8)); }
            catch (Exception ex) { Log.Warning(ex, "Shutdown unload failed"); }
        }
        Scan.CancelScan();
    }
}
