using System.Collections.Concurrent;

namespace Renaissance.Host;

/// <summary>
/// Dedicated STA thread that owns the plugin and its editor window. Plugins expect a classic Win32
/// message loop on the thread that created their UI, so commands are marshalled here.
/// </summary>
internal sealed class PluginThread
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;

    public PluginThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Renaissance.PluginThread" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Invoked on the plugin thread roughly every 20ms (editor idle, popup suppression).</summary>
    public Action? OnIdle { get; set; }

    public void Start()
    {
        _thread.Start();
        _started.Wait();
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        _wake.Set();
        return tcs.Task;
    }

    public void Stop()
    {
        _stop = true;
        _wake.Set();
    }

    public static void Pump()
    {
        while (Native.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
    }

    private void Run()
    {
        _started.Set();
        var handles = new[] { _wake.SafeWaitHandle.DangerousGetHandle() };
        while (!_stop)
        {
            Pump();
            while (_queue.TryDequeue(out var action)) action();
            try { OnIdle?.Invoke(); } catch { }
            Native.MsgWaitForMultipleObjects(1, handles, false, 20, Native.QS_ALLINPUT);
        }
    }
}
