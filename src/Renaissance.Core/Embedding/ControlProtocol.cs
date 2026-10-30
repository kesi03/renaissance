using System.Runtime.InteropServices;

namespace Renaissance.Core.Embedding;

/// <summary>
/// Line-based text protocol between the DAW-facing wrapper (server) and the embedded Renaissance app (client).
/// Deliberately reflection-free so it works inside a NativeAOT wrapper DLL.
/// </summary>
public static class ControlProtocol
{
    // wrapper -> app
    public const string Config = "CONFIG";      // CONFIG <sampleRate> <blockSize>
    public const string GetState = "GETSTATE";  // GETSTATE <id>
    public const string SetState = "SETSTATE";  // SETSTATE <base64>
    public const string Close = "CLOSE";

    // app -> wrapper
    public const string Ready = "READY";        // READY <hwnd>
    public const string Audio = "AUDIO";        // AUDIO <bridgeName> <inputs> <outputs> <maxFrames>
    public const string NoAudio = "NOAUDIO";
    public const string State = "STATE";        // STATE <id> <base64>
    public const string Resize = "RESIZE";      // RESIZE <width> <height>

    public const string ArgEmbedded = "--embedded";
    public const string ArgControl = "--control";
    public const string ArgDpi = "--dpi";
    public const string ToAppSuffix = ".w2a";
    public const string FromAppSuffix = ".a2w";
    public const string AppPathEnvironmentVariable = "RENAISSANCE_APP";
}

internal static class EmbedLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Renaissance", "logs");
            Directory.CreateDirectory(dir);
            lock (Gate)
                File.AppendAllText(Path.Combine(dir, "wrapper.log"), $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

internal static unsafe class EmbedNative
{
    public const uint WS_POPUP = 0x80000000, WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000,
        WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;
    public const int SW_HIDE = 0, GWL_STYLE = -16;

    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern int SetWindowLongW(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

    /// <summary>DPI mode of the calling (DAW) process: "unaware", "system" or "permonitor". Null if it can't be determined.</summary>
    public static string? CurrentDpiMode()
    {
        try
        {
            // DPI_AWARENESS: 0 = unaware, 1 = system aware, 2 = per-monitor aware
            return GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) switch
            {
                0 => "unaware",
                1 => "system",
                2 => "permonitor",
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }
}
