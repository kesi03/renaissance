using System.IO.Pipes;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Renaissance.Core.Embedding;
using Renaissance.Core.Models;
using Renaissance.Core.Session;
using Renaissance.UI.ViewModels;
using Serilog;

namespace Renaissance.UI.Services;

/// <summary>
/// UI-side counterpart of <see cref="WrapperLink"/>. When Renaissance runs inside a DAW this app owns the plugin
/// session; this class tells the wrapper DLL where the audio bridge is, reports the window to embed, and
/// answers the DAW's save/load requests.
/// </summary>
public sealed class EmbeddedController
{
    private readonly Window _window;
    private readonly MainViewModel _main;
    private readonly string _pipeName;
    private readonly System.Collections.Concurrent.BlockingCollection<string> _outbox = new();
    private NamedPipeClientStream? _in;
    private NamedPipeClientStream? _out;
    private double _sampleRate = 44100;
    private int _blockSize = 512;
    private long _hwnd;
    private PluginViewModel? _watched;

    public EmbeddedController(Window window, MainViewModel main, string pipeName)
    {
        _window = window;
        _main = main;
        _pipeName = pipeName;

        main.SessionLoaded += session => _ = AttachAudioAsync(session);
        main.SessionUnloaded += () =>
        {
            Send(ControlProtocol.NoAudio);
            SendResize(1000, 700);
        };
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.Current)) return;
            Watch(main.Current as PluginViewModel);
        };
    }

    public static ControlOptions? Parse(string[] args)
    {
        if (!args.Contains(ControlProtocol.ArgEmbedded)) return null;
        var i = Array.IndexOf(args, ControlProtocol.ArgControl);
        return i >= 0 && i + 1 < args.Length ? new ControlOptions(args[i + 1]) : null;
    }

    public sealed record ControlOptions(string PipeName);

    /// <summary>Make the main window suitable for being re-parented into a DAW editor.</summary>
    public static void PrepareWindow(Window window)
    {
        window.SystemDecorations = SystemDecorations.None;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.MinWidth = 0;
        window.MinHeight = 0;
        window.Width = 1000;
        window.Height = 700;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(-32000, -32000);
        ApplyCompactStyle(window);
    }

    /// <summary>Smaller fonts and tighter controls. Also usable standalone via the --compact argument for design work.</summary>
    public static void ApplyCompactStyle(Window window)
    {
        window.FontSize = 12;
        Application.Current!.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Renaissance/"))
        {
            Source = new Uri("/Styles/Compact.axaml", UriKind.Relative),
        });
    }
    public void Start()
    {
        _window.Opened += (_, _) =>
        {
            _hwnd = _window.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0;
            Send($"{ControlProtocol.Ready} {_hwnd}");
        };
        _ = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            _in = new NamedPipeClientStream(".", _pipeName + ControlProtocol.ToAppSuffix, PipeDirection.In);
            _out = new NamedPipeClientStream(".", _pipeName + ControlProtocol.FromAppSuffix, PipeDirection.Out);
            await _in.ConnectAsync(15000);
            await _out.ConnectAsync(15000);
            new Thread(WriteLoop) { IsBackground = true, Name = "Renaissance.Embedded.Writer" }.Start();
            Log.Information("Connected to wrapper");
            if (_hwnd != 0) Send($"{ControlProtocol.Ready} {_hwnd}");

            using var reader = new StreamReader(_in, new UTF8Encoding(false));
            while (await reader.ReadLineAsync() is { } line)
            {
                var captured = line;
                await Dispatcher.UIThread.InvokeAsync(() => HandleAsync(captured));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Wrapper link ended");
        }
        await Dispatcher.UIThread.InvokeAsync(Quit);
    }

    private async Task HandleAsync(string line)
    {
        var parts = line.Split(' ');
        Log.Information("Wrapper message: {Command} ({Length} chars)", parts[0], line.Length);
        try
        {
            switch (parts[0])
            {
                case ControlProtocol.Config:
                    _sampleRate = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    _blockSize = int.Parse(parts[2]);
                    if (_main.Current is PluginViewModel p) await AttachAudioAsync(p.Session);
                    break;

                case ControlProtocol.SetState:
                    await _main.OpenStateAsync(PluginState.FromBytes(Convert.FromBase64String(parts[1])));
                    break;

                case ControlProtocol.GetState:
                {
                    var bytes = _main.Current is PluginViewModel loaded
                        ? (await loaded.Session.SnapshotAsync()).ToBytes()
                        : [];
                    Send($"{ControlProtocol.State} {parts[1]} {Convert.ToBase64String(bytes)}");
                    break;
                }

                case ControlProtocol.Close:
                    Quit();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to handle wrapper message {Line}", parts[0]);
        }
    }

    private async Task AttachAudioAsync(PluginSession session)
    {
        try
        {
            await session.ConfigureAudioAsync(_sampleRate, _blockSize);
            var l = session.Audio!.Layout;
            Send($"{ControlProtocol.Audio} {session.Audio.Name} {l.Inputs} {l.Outputs} {l.MaxFrames}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not start audio");
        }
    }

    // The wrapper resizes the embedded window to fit whichever view is showing.
    private void Watch(PluginViewModel? plugin)
    {
        if (_watched is not null) _watched.PropertyChanged -= OnPluginPropertyChanged;
        _watched = plugin;
        if (plugin is null) return;
        plugin.PropertyChanged += OnPluginPropertyChanged;
        ResizeForPlugin(plugin);
    }

    private void OnPluginPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PluginViewModel.EditorWidth) or nameof(PluginViewModel.EditorHeight) && sender is PluginViewModel p)
            ResizeForPlugin(p);
    }

    // RESIZE is requested in physical pixels (what the DAW window uses); layout values are in DIPs.
    private void ResizeForPlugin(PluginViewModel p)
    {
        var scale = _window.RenderScaling;
        SendResize((int)Math.Round(Math.Max(560, p.EditorWidth + 40) * scale), (int)Math.Round(Math.Max(360, p.EditorHeight + 150) * scale));
    }

    private void SendResize(int w, int h) => Send($"{ControlProtocol.Resize} {w} {h}");

    private void Send(string line)
    {
        try { _outbox.Add(line); }
        catch (InvalidOperationException) { }
    }

    private void WriteLoop()
    {
        try
        {
            using var writer = new StreamWriter(_out!, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            foreach (var line in _outbox.GetConsumingEnumerable()) writer.WriteLine(line);
        }
        catch (IOException)
        {
        }
    }

    private void Quit()
    {
        _main.Shutdown();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        else Environment.Exit(0);
    }
}
