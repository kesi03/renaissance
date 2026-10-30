using System.Diagnostics;
using Renaissance.Core.Hosting;
using Renaissance.Core.Ipc;
using Renaissance.Core.Models;
using Xunit;

namespace Renaissance.Tests;

/// <summary>Talks to a real Renaissance.Host64 process (no VST plugin required).</summary>
public class HostIntegrationTests
{
    public HostIntegrationTests() =>
        Environment.SetEnvironmentVariable(HostLocator.EnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "Hosts"));

    [Fact]
    public async Task Host_starts_answers_ping_and_shuts_down()
    {
        await using var client = new HostProcessClient(PluginArchitecture.X64);
        await client.StartAsync();

        var response = await client.SendAsync(new HostRequest { Command = HostCommands.Ping }, TimeSpan.FromSeconds(10));
        Assert.True(response.Ok);
        Assert.True(client.IsAlive);

        var pid = client.ProcessId;
        await client.ShutdownAsync();
        await Task.Delay(300);
        Assert.DoesNotContain(Process.GetProcesses(), p => p.Id == pid && p.ProcessName.StartsWith("Renaissance.Host"));
    }

    [Fact]
    public async Task Loading_a_non_plugin_reports_an_error_without_killing_the_host()
    {
        await using var client = new HostProcessClient(PluginArchitecture.X64);
        await client.StartAsync();

        var notAPlugin = Path.Combine(AppContext.BaseDirectory, "Renaissance.Core.dll");
        var response = await client.SendAsync(new HostRequest { Command = HostCommands.Load, Path = notAPlugin }, TimeSpan.FromSeconds(20));

        Assert.False(response.Ok);
        Assert.False(string.IsNullOrWhiteSpace(response.Error));
        var ping = await client.SendAsync(new HostRequest { Command = HostCommands.Ping }, TimeSpan.FromSeconds(10));
        Assert.True(ping.Ok);
    }

    [Fact]
    public async Task Commands_without_a_plugin_fail_gracefully()
    {
        await using var client = new HostProcessClient(PluginArchitecture.X64);
        await client.StartAsync();
        var response = await client.SendAsync(new HostRequest { Command = HostCommands.GetParameters }, TimeSpan.FromSeconds(10));
        Assert.False(response.Ok);
        Assert.Contains("No plugin", response.Error);
    }

    [Fact]
    public async Task Killing_the_host_is_detected_and_fails_pending_calls_instead_of_crashing_us()
    {
        await using var client = new HostProcessClient(PluginArchitecture.X64);
        var exited = new TaskCompletionSource<string>();
        client.Exited += reason => exited.TrySetResult(reason);
        await client.StartAsync();

        client.Kill();

        var reason = await Task.WhenAny(exited.Task, Task.Delay(5000)) == exited.Task ? await exited.Task : null;
        Assert.NotNull(reason);
        Assert.False(client.IsAlive);
        await Assert.ThrowsAsync<HostCrashedException>(() => client.SendAsync(new HostRequest { Command = HostCommands.Ping }));
    }

    [Fact]
    public void Pe_inspector_reads_architecture_and_exports_without_loading()
    {
        var host = Path.Combine(AppContext.BaseDirectory, "Hosts", "Host64", "Renaissance.Host64.dll");
        var info = PeInspector.Inspect(host);
        Assert.True(info.IsPe);
        Assert.False(info.ExportsVstEntry);

        var notPe = Path.Combine(AppContext.BaseDirectory, "Renaissance.Core.deps.json");
        Assert.False(PeInspector.Inspect(notPe).IsPe);
        Assert.False(PeInspector.Inspect(Path.Combine(AppContext.BaseDirectory, "missing.dll")).IsPe);
    }

    [Fact]
    public void Pe_inspector_detects_native_exports_and_x64()
    {
        var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var info = PeInspector.Inspect(kernel32);
        Assert.True(info.IsPe);
        Assert.Equal(PluginArchitecture.X64, info.Architecture);
        Assert.Contains("LoadLibraryW", info.Exports);
    }
}
