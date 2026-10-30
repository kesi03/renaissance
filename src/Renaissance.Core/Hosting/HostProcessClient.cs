using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Renaissance.Core.Ipc;
using Renaissance.Core.Models;
using Serilog;

namespace Renaissance.Core.Hosting;

/// <summary>
/// Owns one isolated Host32/Host64 process and the named-pipe connection to it.
/// A host crash never propagates: pending calls fail with <see cref="HostCrashedException"/>
/// and <see cref="Exited"/> is raised so the owner can recover.
/// </summary>
public sealed class HostProcessClient : IAsyncDisposable
{
    private readonly PluginArchitecture _architecture;
    private readonly ILogger _log = Log.ForContext<HostProcessClient>();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<HostResponse>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private int _nextId;
    private int _goneRaised;
    private volatile bool _intentionalShutdown;

    public HostProcessClient(PluginArchitecture architecture) => _architecture = architecture;

    public bool IsAlive { get; private set; }
    public int ProcessId => _process?.Id ?? 0;

    /// <summary>"unaware" | "system" | "permonitor". Set when hosting inside a DAW so editor windows match the DAW's DPI mode.</summary>
    public static string? DpiMode { get; set; }

    public event Action<HostResponse>? EventReceived;
    public event Action<string>? Exited;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var pipeName = "Renaissance.Host." + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var exe = HostLocator.Find(_architecture);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--parent");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        if (DpiMode is { Length: > 0 } dpi)
        {
            psi.ArgumentList.Add("--dpi");
            psi.ArgumentList.Add(dpi);
        }

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start host process.");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => OnGone($"Host process exited unexpectedly (code {SafeExitCode()}).");
        _log.Information("Started {Host} (pid {Pid})", Path.GetFileName(exe), _process.Id);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await _pipe.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
            throw new TimeoutException("Host process did not connect in time.");
        }

        IsAlive = true;
        _ = Task.Run(ReadLoopAsync);
    }

    private int SafeExitCode()
    {
        try { return _process?.ExitCode ?? -1; } catch { return -1; }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var msg = await PipeMessageIO.ReadAsync<HostResponse>(_pipe!, _cts.Token).ConfigureAwait(false);
                if (msg is null) break;
                if (msg.Id == 0 && msg.Event is not null)
                {
                    try { EventReceived?.Invoke(msg); }
                    catch (Exception ex) { _log.Warning(ex, "Event handler failed"); }
                }
                else if (_pending.TryRemove(msg.Id, out var tcs))
                {
                    tcs.TrySetResult(msg);
                }
            }
            OnGone("Host connection closed.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
        {
            OnGone("Host connection lost: " + ex.Message);
        }
    }

    private void OnGone(string reason)
    {
        if (Interlocked.Exchange(ref _goneRaised, 1) != 0) return;
        IsAlive = false;
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(new HostCrashedException(reason));
        if (_intentionalShutdown) return;
        _log.Error("Host gone: {Reason}", reason);
        try { Exited?.Invoke(reason); }
        catch (Exception ex) { _log.Warning(ex, "Exited handler failed"); }
    }

    public async Task<HostResponse> SendAsync(HostRequest request, TimeSpan? timeout = null)
    {
        if (!IsAlive) throw new HostCrashedException("Host is not running.");
        request.Id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = tcs;
        try
        {
            await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try { await PipeMessageIO.WriteAsync(_pipe!, request, _cts.Token).ConfigureAwait(false); }
            finally { _writeLock.Release(); }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(request.Id, out _);
            throw new HostCrashedException("Host connection lost: " + ex.Message);
        }

        var limit = timeout ?? TimeSpan.FromSeconds(20);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(limit)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            _pending.TryRemove(request.Id, out _);
            throw new TimeoutException($"Host did not answer '{request.Command}' within {limit.TotalSeconds:0}s.");
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Sends a request and throws <see cref="PluginHostException"/> if the host reports failure.</summary>
    public async Task<HostResponse> CallAsync(HostRequest request, TimeSpan? timeout = null)
    {
        var response = await SendAsync(request, timeout).ConfigureAwait(false);
        if (!response.Ok) throw new PluginHostException(response.Error ?? "Host reported an error.");
        return response;
    }

    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public async Task ShutdownAsync()
    {
        _intentionalShutdown = true;
        if (IsAlive)
        {
            try { await SendAsync(new HostRequest { Command = HostCommands.Shutdown }, TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or HostCrashedException) { }
        }
        try
        {
            if (_process is not null && !_process.WaitForExit(2000)) Kill();
        }
        catch (InvalidOperationException) { }
        IsAlive = false;
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _cts.Cancel();
        _pipe?.Dispose();
        _process?.Dispose();
        _writeLock.Dispose();
    }
}
