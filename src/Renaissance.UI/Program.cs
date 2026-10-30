using Avalonia;
using Renaissance.Core;
using Renaissance.Core.Embedding;
using Renaissance.Core.Hosting;
using Serilog;

namespace Renaissance.UI;

internal static class Program
{
    /// <summary>
    /// Embedded in a DAW we must share the DAW's DPI mode or the editor is mis-scaled and mis-placed.
    /// The wrapper detects the DAW's mode and passes it as --dpi; RENAISSANCE_DPI overrides it for experiments.
    /// </summary>
    private static string EmbeddedDpiMode(string[] args)
    {
        var env = Environment.GetEnvironmentVariable("RENAISSANCE_DPI");
        if (!string.IsNullOrWhiteSpace(env)) return env.ToLowerInvariant();
        var i = Array.IndexOf(args, ControlProtocol.ArgDpi);
        return i >= 0 && i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : "unaware";
    }

    private static Win32DpiAwareness ToAvalonia(string mode) => mode switch
    {
        "system" => Win32DpiAwareness.SystemDpiAware,
        "permonitor" => Win32DpiAwareness.PerMonitorDpiAware,
        _ => Win32DpiAwareness.Unaware,
    };

    [STAThread]
    public static void Main(string[] args)
    {
        LogSetup.Configure("renaissance");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");

        var embedded = args.Contains(ControlProtocol.ArgEmbedded);
        var dpiMode = "permonitor";
        if (embedded)
        {
            dpiMode = EmbeddedDpiMode(args);
            HostProcessClient.DpiMode = dpiMode;
            Log.Information("Embedded DPI mode: {Mode}", dpiMode);
        }

        try
        {
            BuildAvaloniaApp(embedded, dpiMode).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(false, "permonitor");

    private static AppBuilder BuildAvaloniaApp(bool embedded, string dpiMode)
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
        if (embedded) builder = builder.With(new Win32PlatformOptions { DpiAwareness = ToAvalonia(dpiMode) });
        return builder;
    }
}
