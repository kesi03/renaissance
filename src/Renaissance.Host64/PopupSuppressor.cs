using System.Diagnostics;

namespace Renaissance.Host;

/// <summary>
/// Hides splash screens, nag screens and activation dialogs that plugins create as top-level windows.
/// Runs on its own thread because plugins often block the plugin thread while showing them.
/// </summary>
internal sealed unsafe class PopupSuppressor
{
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_HIDEWINDOW = 0x0080, SWP_ASYNCWINDOWPOS = 0x4000;

    private static readonly uint s_pid = (uint)Environment.ProcessId;
    private long _activeUntil;
    private Func<IntPtr>? _editorHandle;

    public PopupSuppressor()
    {
        var thread = new Thread(Loop) { IsBackground = true, Name = "Renaissance.PopupSuppressor" };
        thread.Start();
    }

    public void SetEditorProvider(Func<IntPtr> provider) => _editorHandle = provider;

    public void ActivateIndefinitely() => Interlocked.Exchange(ref _activeUntil, long.MaxValue);

    public void ActivateFor(TimeSpan duration) =>
        Interlocked.Exchange(ref _activeUntil, Environment.TickCount64 + (long)duration.TotalMilliseconds);

    public void Deactivate() => Interlocked.Exchange(ref _activeUntil, 0);

    private void Loop()
    {
        while (true)
        {
            Thread.Sleep(30);
            if (Environment.TickCount64 >= Interlocked.Read(ref _activeUntil)) continue;
            try { Sweep(); } catch { }
        }
    }

    private void Sweep()
    {
        var editor = _editorHandle?.Invoke() ?? IntPtr.Zero;
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != s_pid || hwnd == editor || !Native.IsWindowVisible(hwnd) || Native.GetParent(hwnd) != IntPtr.Zero)
                return true;

            var buffer = stackalloc char[64];
            var len = Native.GetClassNameW(hwnd, buffer, 64);
            var cls = new string(buffer, 0, len);
            if (cls == "#32768" || cls.StartsWith("tooltips", StringComparison.OrdinalIgnoreCase)) return true;

            Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | SWP_HIDEWINDOW | SWP_ASYNCWINDOWPOS);
            if (cls == "#32770") Native.PostMessageW(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }
}
