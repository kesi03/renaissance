using Renaissance.Core;
using Renaissance.PluginDatabase;
using Renaissance.PresetEngine;
using Renaissance.Scanner;

namespace Renaissance.UI.Services;

public sealed class AppServices
{
    public AppServices(IDialogService dialogs)
    {
        AppPaths.EnsureCreated();
        Dialogs = dialogs;
        Database = new RenaissanceDatabase(AppPaths.Database);
        Presets = new PresetStore(AppPaths.Presets);
        Scanner = new PluginScanner(Database);
    }

    public IDialogService Dialogs { get; }
    public RenaissanceDatabase Database { get; }
    public PresetStore Presets { get; }
    public PluginScanner Scanner { get; }
}
