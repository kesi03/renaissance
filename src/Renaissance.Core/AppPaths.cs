using Serilog;

namespace Renaissance.Core;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Renaissance");

    public static string Database => Path.Combine(Root, "renaissance.db");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Presets => Path.Combine(Root, "presets");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Presets);
    }
}

public static class LogSetup
{
    public static void Configure(string name)
    {
        AppPaths.EnsureCreated();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppPaths.Logs, name + "-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();
    }
}
