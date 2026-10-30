using Renaissance.Core.Ipc;

namespace Renaissance.Host;

/// <summary>Owns the plugin editor window. Created hidden; Renaissance.UI asks us to re-parent it into its own view.</summary>
internal sealed class EditorController
{
    private readonly Vst2Plugin _plugin;
    private IntPtr _hwnd;
    private int _width, _height;

    public EditorController(Vst2Plugin plugin) => _plugin = plugin;

    public IntPtr Handle => _hwnd;
    public bool IsOpen => _hwnd != IntPtr.Zero;

    public (int Width, int Height) Open()
    {
        if (IsOpen) return (_width, _height);
        if (!_plugin.HasEditor) throw new InvalidOperationException("Plugin has no editor.");

        _hwnd = Native.CreateWindowExW(Native.WS_EX_TOOLWINDOW, "Static", "Renaissance Editor",
            Native.WS_POPUP | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS,
            -32000, -32000, 100, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create editor window.");

        _plugin.OpenEditor(_hwnd);
        (_width, _height) = _plugin.GetEditorRect();
        if (_width <= 0 || _height <= 0) (_width, _height) = (640, 480);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, -32000, -32000, _width, _height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        return (_width, _height);
    }

    public void Attach(IntPtr parent)
    {
        if (!IsOpen) return;
        Native.SetParent(_hwnd, parent);
        var style = (int)(Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS);
        Native.SetWindowLongW(_hwnd, Native.GWL_STYLE, style);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, _width, _height,
            Native.SWP_NOZORDER | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
    }

    public void Resize(int width, int height)
    {
        if (!IsOpen || width <= 0 || height <= 0) return;
        _width = width;
        _height = height;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    public void Idle()
    {
        if (IsOpen) _plugin.EditorIdle();
    }

    public void Close()
    {
        if (!IsOpen) return;
        try { _plugin.CloseEditor(); } finally
        {
            Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    /// <summary>Scanner validation: opens the editor hidden, lets it idle, then closes it.</summary>
    public void Test()
    {
        var wasOpen = IsOpen;
        Open();
        for (var i = 0; i < 10; i++)
        {
            Idle();
            PluginThread.Pump();
            Thread.Sleep(25);
        }
        if (!wasOpen) Close();
    }
}
