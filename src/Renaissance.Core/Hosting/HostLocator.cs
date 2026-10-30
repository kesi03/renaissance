using Renaissance.Core.Models;

namespace Renaissance.Core.Hosting;

public static class HostLocator
{
    public const string EnvironmentVariable = "RENAISSANCE_HOSTS";

    public static string Find(PluginArchitecture architecture)
    {
        var exe = architecture == PluginArchitecture.X86 ? "Renaissance.Host32.exe" : "Renaissance.Host64.exe";
        var folder = architecture == PluginArchitecture.X86 ? "Host32" : "Host64";

        var roots = new List<string>();
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env)) roots.Add(env);
        roots.Add(Path.Combine(AppContext.BaseDirectory, "Hosts"));
        roots.Add(AppContext.BaseDirectory);

        foreach (var root in roots)
        {
            foreach (var candidate in new[] { Path.Combine(root, folder, exe), Path.Combine(root, exe) })
                if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{exe} was not found next to the application (looked in {string.Join(", ", roots)}).");
    }
}
