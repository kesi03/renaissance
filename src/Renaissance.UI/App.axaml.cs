using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Renaissance.UI.Services;
using Renaissance.UI.ViewModels;
using Renaissance.UI.Views;

namespace Renaissance.UI;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            window.Title = "Renaissance " + Renaissance.Core.AppVersion.Text;
            Serilog.Log.Information("Renaissance {Version} starting", Renaissance.Core.AppVersion.Text);
            var services = new AppServices(new DialogService(window));
            var main = new MainViewModel(services);
            window.DataContext = main;
            window.Closing += (_, _) => main.Shutdown();
            desktop.MainWindow = window;

            var embedded = EmbeddedController.Parse(desktop.Args ?? []);
            if (desktop.Args?.Contains("--compact") == true && embedded is null)
            {
                EmbeddedController.ApplyCompactStyle(window);
                window.Width = 1000;
                window.Height = 700;
                window.MinWidth = 0;
                window.MinHeight = 0;
            }
            if (embedded is not null)
            {
                Serilog.Log.Information("Embedded mode starting (pipe {Pipe})", embedded.PipeName);
                window.Opened += (_, _) => Serilog.Log.Information("Embedded window opened");
                EmbeddedController.PrepareWindow(window);
                new EmbeddedController(window, main, embedded.PipeName).Start();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
