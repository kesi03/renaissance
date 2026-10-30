namespace Renaissance.Core.Hosting;

public sealed class HostCrashedException : Exception
{
    public HostCrashedException(string message) : base(message) { }
}

public sealed class PluginHostException : Exception
{
    public PluginHostException(string message) : base(message) { }
}
