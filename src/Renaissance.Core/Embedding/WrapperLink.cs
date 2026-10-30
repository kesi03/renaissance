using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using Renaissance.Core.Audio;

namespace Renaissance.Core.Embedding;

/// <summary>
/// DAW-side half of the embedded architecture. Shared by the VST2 and VST3 wrappers (Rules 1, 2, 9).
/// It launches the Renaissance app in embedded mode (the app owns the plugin session and its isolated host),
/// attaches to the shared-memory audio bridge the app creates, relays DAW state, and parents the app window
/// into the DAW's editor window. Reflection-free, so it is safe inside a NativeAOT DLL.
/// </summary>
public sealed class WrapperLink : IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<string> _outbox = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]>> _stateRequests = new();
    private readonly Action<int, int>? _onResize;
    private NamedPipeServerStream? _toApp;
    private NamedPipeServerStream? _fromApp;
    private Process? _process;
    private AudioBridge? _bridge;
    private byte[] _lastState = [];
    private IntPtr _uiWindow;
    private IntPtr _parent;
    private int _width = 1000, _height = 700;
    private int _nextId;
    private volatile bool _disposed;

    public WrapperLink(Action<int, int>? onResizeRequested = null) => _onResize = onResizeRequested;

    public AudioBridge? Bridge => Volatile.Read(ref _bridge);
    public int EditorWidth => _width;
    public int EditorHeight => _height;
    public bool IsConnected { get; private set; }

    public static string? FindApp()
    {
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable(ControlProtocol.AppPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);

        var self = ModuleDirectory();
        if (self is not null)
        {
            candidates.Add(Path.Combine(self, "App", "Renaissance.exe"));
            candidates.Add(Path.Combine(self, "Renaissance.exe"));

            // VST3 bundle layout: <root>\Renaissance.vst3\Contents\x86_64-win\Renaissance.vst3, with <root>\App beside the bundle.
            var up = new DirectoryInfo(self);
            for (var i = 0; i < 4 && up.Parent is not null; i++)
            {
                up = up.Parent;
                candidates.Add(Path.Combine(up.FullName, "App", "Renaissance.exe"));
            }
        }
        var configured = Path.Combine(AppPaths.Root, "app-path.txt");
        if (File.Exists(configured)) candidates.Add(File.ReadAllText(configured).Trim());

        // Where the installer puts the app: %LOCALAPPDATA%\Renaissance\App
        candidates.Add(Path.Combine(AppPaths.Root, "App", "Renaissance.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Folder containing the wrapper binary (set by the wrapper before <see cref="Start"/>).</summary>
    public static string? WrapperDirectory { get; set; }

    private static string? ModuleDirectory() => WrapperDirectory ?? AppContext.BaseDirectory;

    public void Start()
    {
        var app = FindApp();
        if (app is null)
        {
            EmbedLog.Write("Renaissance.exe not found; the wrapper will be silent.");
            return;
        }

        var pipeName = "Renaissance.Ctl." + Guid.NewGuid().ToString("N");
        _toApp = new NamedPipeServerStream(pipeName + ControlProtocol.ToAppSuffix, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None);
        _fromApp = new NamedPipeServerStream(pipeName + ControlProtocol.FromAppSuffix, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
        var dpi = EmbedNative.CurrentDpiMode();
        var psi = new ProcessStartInfo(app)
        {
            UseShellExecute = true, // don't inherit the DAW's stdio handles
            WorkingDirectory = Path.GetDirectoryName(app)!,
            Arguments = $"{ControlProtocol.ArgEmbedded} {ControlProtocol.ArgControl} {pipeName}"
                + (dpi is null ? "" : $" {ControlProtocol.ArgDpi} {dpi}"),
        };
        EmbedLog.Write("DAW DPI mode: " + (dpi ?? "unknown"));
        _process = Process.Start(psi);
        EmbedLog.Write($"Started {app} (pid {_process?.Id})");

        var thread = new Thread(ReadLoop) { IsBackground = true, Name = "Renaissance.WrapperLink" };
        thread.Start();
        EmbedLog.Write("link thread started");
    }

    // ---------------------------------------------------------------- outbound

    public void Configure(double sampleRate, int blockSize)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{ControlProtocol.Config} {sampleRate:0.###} {blockSize}");
        TrySend(line);
    }

    /// <summary>Called when the DAW loads a project (effSetChunk / IComponent::setState).</summary>
    public void SetState(byte[] data)
    {
        _lastState = data;
        if (data.Length == 0) return;
        var line = ControlProtocol.SetState + " " + Convert.ToBase64String(data);
        TrySend(line);
    }

    /// <summary>Called when the DAW saves a project. Falls back to the last known state if the app can't answer.</summary>
    public byte[] GetState()
    {
        if (!IsConnected) return _lastState;
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stateRequests[id] = tcs;
        TrySend($"{ControlProtocol.GetState} {id}");
        try
        {
            if (tcs.Task.Wait(4000))
            {
                var data = tcs.Task.Result;
                if (data.Length > 0) _lastState = data;
            }
        }
        catch (AggregateException)
        {
        }
        finally { _stateRequests.TryRemove(id, out _); }
        return _lastState;
    }

    /// <summary>Never blocks the caller (DAW thread): lines are queued and written by a dedicated thread.</summary>
    private bool TrySend(string line)
    {
        try
        {
            _outbox.Add(line);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- editor embedding

    public void AttachEditor(IntPtr parent)
    {
        _parent = parent;
        Reparent();
    }

    /// <summary>The host resized its editor window (e.g. IPlugView::onSize): fit the embedded window to it.</summary>
    public void ResizeEditorWindow(int width, int height)
    {
        _width = Math.Max(200, width);
        _height = Math.Max(150, height);
        var ui = _uiWindow;
        if (ui != IntPtr.Zero && _parent != IntPtr.Zero && EmbedNative.IsWindow(ui))
            EmbedNative.SetWindowPos(ui, IntPtr.Zero, 0, 0, _width, _height, EmbedNative.SWP_NOZORDER | EmbedNative.SWP_NOACTIVATE);
    }

    public void DetachEditor()
    {
        _parent = IntPtr.Zero;
        var ui = _uiWindow;
        if (ui == IntPtr.Zero || !EmbedNative.IsWindow(ui)) return;
        EmbedNative.ShowWindow(ui, EmbedNative.SW_HIDE);
        EmbedNative.SetParent(ui, IntPtr.Zero);
        EmbedNative.SetWindowLongW(ui, EmbedNative.GWL_STYLE, unchecked((int)EmbedNative.WS_POPUP));
    }

    private void Reparent()
    {
        var ui = _uiWindow;
        if (ui == IntPtr.Zero || !EmbedNative.IsWindow(ui)) return;
        if (_parent == IntPtr.Zero)
        {
            EmbedNative.ShowWindow(ui, EmbedNative.SW_HIDE);
            return;
        }
        EmbedNative.SetParent(ui, _parent);
        EmbedNative.SetWindowLongW(ui, EmbedNative.GWL_STYLE,
            unchecked((int)(EmbedNative.WS_CHILD | EmbedNative.WS_VISIBLE | EmbedNative.WS_CLIPSIBLINGS | EmbedNative.WS_CLIPCHILDREN)));
        EmbedNative.SetWindowPos(ui, IntPtr.Zero, 0, 0, _width, _height,
            EmbedNative.SWP_NOZORDER | EmbedNative.SWP_FRAMECHANGED | EmbedNative.SWP_SHOWWINDOW);
    }

    // ---------------------------------------------------------------- inbound

    private void ReadLoop()
    {
        try
        {
            EmbedLog.Write("waiting for app on pipes");
            _toApp!.WaitForConnection();
            EmbedLog.Write("w2a pipe connected");
            _fromApp!.WaitForConnection();
            IsConnected = true;
            EmbedLog.Write("App connected.");

            // Writing happens on its own thread so neither side can deadlock by writing while the other is writing.
            var writerThread = new Thread(WriteLoop) { IsBackground = true, Name = "Renaissance.WrapperLink.Writer" };
            writerThread.Start();

            using var reader = new StreamReader(_fromApp, new UTF8Encoding(false));
            while (!_disposed && reader.ReadLine() is { } line)
            {
                EmbedLog.Write("app -> wrapper: " + (line.Length > 80 ? line[..80] + "…" : line));
                Handle(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            EmbedLog.Write("Link error: " + ex);
        }
        finally
        {
            IsConnected = false;
            foreach (var tcs in _stateRequests.Values) tcs.TrySetResult([]);
            if (!_disposed) EmbedLog.Write("App disconnected.");
        }
    }

    private void WriteLoop()
    {
        try
        {
            using var writer = new StreamWriter(_toApp!, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            foreach (var line in _outbox.GetConsumingEnumerable())
            {
                writer.WriteLine(line);
                EmbedLog.Write("wrapper -> app: " + (line.Length > 40 ? line[..40] + "…" : line));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private void Handle(string line)
    {
        var parts = line.Split(' ');
        try
        {
            switch (parts[0])
            {
                case ControlProtocol.Ready:
                    _uiWindow = (IntPtr)long.Parse(parts[1], CultureInfo.InvariantCulture);
                    Reparent();
                    break;

                case ControlProtocol.Audio:
                {
                    var bridge = AudioBridge.Attach(parts[1], int.Parse(parts[4]), int.Parse(parts[2]), int.Parse(parts[3]));
                    SwapBridge(bridge);
                    EmbedLog.Write($"Audio attached: {parts[1]} in={parts[2]} out={parts[3]} frames={parts[4]}");
                    break;
                }

                case ControlProtocol.NoAudio:
                    SwapBridge(null);
                    break;

                case ControlProtocol.State:
                    if (_stateRequests.TryGetValue(int.Parse(parts[1]), out var tcs))
                        tcs.TrySetResult(parts.Length > 2 ? Convert.FromBase64String(parts[2]) : []);
                    break;

                case ControlProtocol.Resize:
                    _width = Math.Max(200, int.Parse(parts[1]));
                    _height = Math.Max(150, int.Parse(parts[2]));
                    var ui = _uiWindow;
                    if (ui != IntPtr.Zero && _parent != IntPtr.Zero && EmbedNative.IsWindow(ui))
                        EmbedNative.SetWindowPos(ui, IntPtr.Zero, 0, 0, _width, _height, EmbedNative.SWP_NOZORDER | EmbedNative.SWP_NOACTIVATE);
                    _onResize?.Invoke(_width, _height);
                    break;
            }
        }
        catch (Exception ex)
        {
            EmbedLog.Write($"Bad message '{line}': {ex.Message}");
        }
    }

    private void SwapBridge(AudioBridge? next)
    {
        var old = Interlocked.Exchange(ref _bridge, next);
        if (old is null) return;
        // The audio thread may still be inside the old bridge; release it a moment later.
        _ = Task.Delay(1500).ContinueWith(_ => old.Dispose());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachEditor();
        TrySend(ControlProtocol.Close);
        _outbox.CompleteAdding();
        try
        {
            if (_process is { HasExited: false } && !_process.WaitForExit(3000)) _process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }
        _toApp?.Dispose();
        _fromApp?.Dispose();
        SwapBridge(null);
    }
}
