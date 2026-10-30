using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using Renaissance.Core;
using Renaissance.Core.Ipc;
using Serilog;

namespace Renaissance.Host;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? pipeName = null;
        string? dpiMode = null;
        var parentPid = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--pipe") pipeName = args[i + 1];
            else if (args[i] == "--parent") int.TryParse(args[i + 1], out parentPid);
            else if (args[i] == "--dpi") dpiMode = args[i + 1];
        }
        if (pipeName is null)
        {
            Console.Error.WriteLine("Renaissance plugin host. Not intended to be started manually (missing --pipe).");
            return 1;
        }

        // A crashing plugin must terminate quietly: no Windows Error Reporting dialog.
        Native.SetErrorMode(Native.SEM_FAILCRITICALERRORS | Native.SEM_NOGPFAULTERRORBOX);
        Native.SetDpiMode(dpiMode);

        var bits = IntPtr.Size == 8 ? "host64" : "host32";
        LogSetup.Configure(bits);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled host exception");
        Log.Information("{Host} started (pid {Pid}, parent {Parent})", bits, Environment.ProcessId, parentPid);

        if (parentPid > 0) WatchParent(parentPid);

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000);

        var outbound = Channel.CreateUnbounded<HostResponse>(new UnboundedChannelOptions { SingleReader = true });
        var writer = Task.Run(async () =>
        {
            await foreach (var message in outbound.Reader.ReadAllAsync())
                await PipeMessageIO.WriteAsync(pipe, message);
        });

        var thread = new PluginThread();
        thread.Start();
        var session = new HostSession(thread, m => outbound.Writer.TryWrite(m));

        try
        {
            while (true)
            {
                var request = await PipeMessageIO.ReadAsync<HostRequest>(pipe);
                if (request is null) break;

                if (request.Command == HostCommands.Shutdown)
                {
                    await session.HandleAsync(new HostRequest { Command = HostCommands.Unload });
                    outbound.Writer.TryWrite(new HostResponse { Id = request.Id });
                    break;
                }

                var pending = session.HandleAsync(request);
                _ = pending.ContinueWith(t => outbound.Writer.TryWrite(t.Result), TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Log.Information("Pipe closed: {Message}", ex.Message);
        }

        outbound.Writer.TryComplete();
        await Task.WhenAny(writer, Task.Delay(1000));
        thread.Stop();
        Log.Information("{Host} exiting", bits);
        await Log.CloseAndFlushAsync();
        // Terminate without unwinding native plugin code that may be in an unsafe state.
        Environment.Exit(0);
        return 0;
    }

    private static void WatchParent(int pid)
    {
        try
        {
            var parent = Process.GetProcessById(pid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => Environment.Exit(2);
            if (parent.HasExited) Environment.Exit(2);
        }
        catch (ArgumentException)
        {
            Environment.Exit(2);
        }
    }
}
