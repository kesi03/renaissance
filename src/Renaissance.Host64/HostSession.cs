using Renaissance.Core.Ipc;
using Renaissance.Core.Models;
using Serilog;

namespace Renaissance.Host;

/// <summary>Executes commands from Renaissance.Core against the loaded plugin on the plugin thread.</summary>
internal sealed class HostSession
{
    private readonly PluginThread _thread;
    private readonly Action<HostResponse> _emit;
    private readonly PopupSuppressor _popups = new();
    private Vst2Plugin? _plugin;
    private EditorController? _editor;
    private AudioEngine? _audio;

    public HostSession(PluginThread thread, Action<HostResponse> emit)
    {
        _thread = thread;
        _emit = emit;
        _popups.SetEditorProvider(() => _editor?.Handle ?? IntPtr.Zero);
        _thread.OnIdle = () => _editor?.Idle();

        Vst2Plugin.ParameterAutomated += (i, v) =>
            _emit(new HostResponse { Id = 0, Event = HostEvents.ParameterChanged, Index = i, Value = v });
        Vst2Plugin.WindowSizeRequested += (w, h) =>
        {
            _thread.InvokeAsync(() => { _editor?.Resize(w, h); return 0; });
            _emit(new HostResponse { Id = 0, Event = HostEvents.EditorResizeRequested, Width = w, Height = h });
        };
    }

    /// <summary>Enqueues synchronously (preserving request order) and completes when the plugin thread has run it.</summary>
    public Task<HostResponse> HandleAsync(HostRequest req) =>
        _thread.InvokeAsync(() =>
        {
            try
            {
                var response = Execute(req);
                response.Id = req.Id;
                return response;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Command {Command} failed", req.Command);
                return new HostResponse { Id = req.Id, Ok = false, Error = ex.Message };
            }
        });

    private HostResponse Execute(HostRequest req)
    {
        switch (req.Command)
        {
            case HostCommands.Ping:
                return new HostResponse();

            case HostCommands.Load:
            {
                if (_plugin is not null) throw new InvalidOperationException("A plugin is already loaded.");
                if (req.Flag) _popups.ActivateIndefinitely();
                try
                {
                    _plugin = Vst2Plugin.Load(req.Path ?? throw new ArgumentException("Path required."));
                    _editor = new EditorController(_plugin);
                    return new HostResponse { Metadata = _plugin.GetMetadata() };
                }
                finally
                {
                    if (req.Flag) _popups.ActivateFor(TimeSpan.FromSeconds(1.5)); else _popups.Deactivate();
                }
            }

            case HostCommands.SetHidePopups:
                if (req.Flag) _popups.ActivateIndefinitely(); else _popups.Deactivate();
                return new HostResponse();

            case HostCommands.Unload:
                _editor?.Close();
                _audio?.Dispose();
                _audio = null;
                _plugin?.Dispose();
                _plugin = null;
                _editor = null;
                return new HostResponse();
        }

        var plugin = _plugin ?? throw new InvalidOperationException("No plugin loaded.");
        switch (req.Command)
        {
            case HostCommands.GetParameters:
                return new HostResponse { Parameters = plugin.GetParameters() };
            case HostCommands.GetParameterValues:
                return new HostResponse { Floats = plugin.GetParameterValues() };
            case HostCommands.GetParameter:
                return new HostResponse { Value = plugin.GetParameter(req.Index), Text = plugin.GetParameterDisplay(req.Index) };
            case HostCommands.SetParameter:
                plugin.SetParameter(req.Index, req.Value);
                return new HostResponse();
            case HostCommands.GetPrograms:
                return new HostResponse { Programs = plugin.GetProgramNames() };
            case HostCommands.GetProgram:
                return new HostResponse { Index = plugin.GetProgram(), Text = plugin.GetProgramName() };
            case HostCommands.SetProgram:
                plugin.SetProgram(req.Index);
                return new HostResponse();
            case HostCommands.SetProgramName:
                plugin.SetProgramName(req.Text ?? "");
                return new HostResponse();
            case HostCommands.GetChunk:
                return new HostResponse { Data = plugin.GetChunk(req.Flag) };
            case HostCommands.SetChunk:
                plugin.SetChunk(req.Flag, req.Data ?? []);
                return new HostResponse();

            case HostCommands.EditorOpen:
            {
                var (w, h) = _editor!.Open();
                return new HostResponse { Width = w, Height = h, Handle = _editor.Handle.ToInt64() };
            }
            case HostCommands.EditorAttach:
                _editor!.Attach((IntPtr)req.Handle);
                return new HostResponse();
            case HostCommands.EditorResize:
                _editor!.Resize(req.Width, req.Height);
                return new HostResponse();
            case HostCommands.EditorClose:
                _editor!.Close();
                return new HostResponse();
            case HostCommands.EditorTest:
                _popups.ActivateFor(TimeSpan.FromSeconds(3));
                _editor!.Test();
                return new HostResponse();

            case HostCommands.AudioStart:
                _audio?.Dispose();
                _audio = new AudioEngine(plugin, req.Text ?? "", req.SampleRate, req.BlockSize, req.Inputs, req.Outputs);
                return new HostResponse();
            case HostCommands.AudioStop:
                _audio?.Dispose();
                _audio = null;
                return new HostResponse();
            case HostCommands.SendSysEx:
                _audio?.QueueSysEx(req.Data ?? []);
                return new HostResponse();
        }

        throw new NotSupportedException($"Unknown command '{req.Command}'.");
    }
}
