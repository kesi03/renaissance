using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Renaissance.UI.Controls;

/// <summary>
/// Hosts a native child window (HWND). Renaissance asks the isolated plugin host process to re-parent the
/// original VST2 editor window into this container.
/// </summary>
public sealed class PluginEditorHost : NativeControlHost
{
    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    public event Action<IntPtr>? HandleReady;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows()) return base.CreateNativeControlCore(parent);

        var hwnd = CreateWindowExW(0, "Static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 100, 100, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the editor container window.");

        Dispatcher.UIThread.Post(() => HandleReady?.Invoke(hwnd));
        return new PlatformHandle(hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (OperatingSystem.IsWindows()) DestroyWindow(control.Handle);
        else base.DestroyNativeControlCore(control);
    }
}
