using System.Runtime.InteropServices;

namespace Renaissance.Host;

internal static unsafe class Native
{
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_CLIPCHILDREN = 0x02000000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const int GWL_STYLE = -16;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int SW_HIDE = 0;
    public const uint PM_REMOVE = 1;
    public const uint QS_ALLINPUT = 0x04FF;
    public const uint WM_CLOSE = 0x0010;
    public const uint SEM_FAILCRITICALERRORS = 1;
    public const uint SEM_NOGPFAULTERRORBOX = 2;
    public const uint LOAD_WITH_ALTERED_SEARCH_PATH = 8;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll")]
    public static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    [DllImport("user32.dll")]
    public static extern int GetWindowLongW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    public static extern int SetWindowLongW(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    public static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint ms, uint mask);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hwnd, char* buffer, int max);

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    public static void SetDpiMode(string? mode)
    {
        // DPI_AWARENESS_CONTEXT handles: -1 unaware, -2 system, -4 per-monitor v2
        var context = mode switch
        {
            "unaware" => -1,
            "system" => -2,
            _ => -4,
        };
        try { SetProcessDpiAwarenessContext((IntPtr)context); } catch { }
    }
}
