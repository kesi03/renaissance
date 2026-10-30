using Renaissance.Core.Audio;
using Renaissance.Core.Hosting;
using Renaissance.Core.Ipc;
using Renaissance.Core.Models;
using Renaissance.PresetEngine;
using Serilog;

namespace Renaissance.Core.Session;

/// <summary>
/// A loaded plugin: owns its isolated host process, keeps Renaissance-side state (Rule 7),
/// and transparently restarts + restores the plugin if the host dies (Rule 5).
/// </summary>
public sealed class PluginSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ICrashLog? _crashLog;
    private readonly ILogger _log = Log.ForContext<PluginSession>();
    private readonly Queue<DateTime> _restarts = new();
    private HostProcessClient? _client;
    private CancellationTokenSource? _snapshotCts;
    private volatile bool _unloading;
    private bool _editorWanted;
    private long _editorParent;
    private int _audioInputs, _audioOutputs;
    private double _sampleRate;
    private int _blockSize;
    private List<ParameterInfo> _parameters = [];
    private List<string> _programs = [];

    public PluginSession(PluginInfo info, ICrashLog? crashLog = null)
    {
        Info = info;
        _crashLog = crashLog;
        State = new PluginState { PluginPath = info.Path, Architecture = info.Architecture, UniqueId = info.UniqueId, PluginName = info.Name };
    }

    public PluginInfo Info { get; }
    public PluginMetadata? Metadata { get; private set; }
    public IReadOnlyList<ParameterInfo> Parameters => _parameters;
    public IReadOnlyList<string> Programs => _programs;
    public float[] DefaultValues { get; private set; } = [];
    public int CurrentProgram { get; private set; }
    public PluginState State { get; private set; }
    public AudioBridge? Audio { get; private set; }
    public bool IsLoaded { get; private set; }
    public bool IsFaulted { get; private set; }
    public bool IsRecovering { get; private set; }
    public bool AutoRestart { get; set; } = true;
    public int MaxRestartsPerMinute { get; set; } = 5;
    public bool HidePopups { get; set; }
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromSeconds(3);

    public event Action<string>? Crashed;
    public event Action? Recovered;
    public event Action? Faulted;
    public event Action<int, float>? ParameterAutomated;
    public event Action<int, int>? EditorResizeRequested;
    public event Action? ParametersRefreshed;

    // ---------------------------------------------------------------- lifecycle

    public async Task LoadAsync(PluginState? restore = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StartHostAsync().ConfigureAwait(false);
            await RefreshAllAsync().ConfigureAwait(false);
            DefaultValues = _parameters.Select(p => p.Value).ToArray();
            IsLoaded = true;
            if (restore is not null)
            {
                State = restore;
                await RestoreStateCoreAsync(restore).ConfigureAwait(false);
            }
            else CaptureCachedState();
            StartSnapshotLoop();
        }
        catch
        {
            await TeardownAsync().ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task StartHostAsync()
    {
        var client = new HostProcessClient(Info.Architecture);
        client.EventReceived += OnHostEvent;
        client.Exited += OnHostExited;
        await client.StartAsync().ConfigureAwait(false);
        _client = client;

        var response = await CallCoreAsync(new HostRequest
        {
            Command = HostCommands.Load,
            Path = Info.Path,
            Flag = HidePopups,
        }, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        Metadata = response.Metadata ?? throw new PluginHostException("Host returned no plugin metadata.");
        State.UniqueId = Metadata.UniqueId;
        if (string.IsNullOrEmpty(State.PluginName)) State.PluginName = Metadata.Name;
    }

    private async Task RefreshAllAsync()
    {
        var p = await CallCoreAsync(new HostRequest { Command = HostCommands.GetParameters }, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        _parameters = p.Parameters ?? [];
        var pr = await CallCoreAsync(new HostRequest { Command = HostCommands.GetPrograms }, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        _programs = pr.Programs ?? [];
        var cur = await CallCoreAsync(new HostRequest { Command = HostCommands.GetProgram }).ConfigureAwait(false);
        CurrentProgram = cur.Index;
    }

    private async Task<HostResponse> CallCoreAsync(HostRequest request, TimeSpan? timeout = null)
    {
        var client = _client ?? throw new HostCrashedException("Plugin host is not running.");
        try
        {
            return await client.CallAsync(request, timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Error("Host hung on {Command}; terminating it", request.Command);
            client.Kill();
            throw;
        }
    }

    /// <summary>Sends a command to the host, serialised with lifecycle operations.</summary>
    private async Task<HostResponse> CallAsync(HostRequest request, TimeSpan? timeout = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await CallCoreAsync(request, timeout).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------- events / crash recovery

    private void OnHostEvent(HostResponse e)
    {
        switch (e.Event)
        {
            case HostEvents.ParameterChanged:
                if (e.Index >= 0 && e.Index < _parameters.Count) _parameters[e.Index].Value = e.Value;
                ParameterAutomated?.Invoke(e.Index, e.Value);
                break;
            case HostEvents.EditorResizeRequested:
                EditorResizeRequested?.Invoke(e.Width, e.Height);
                break;
        }
    }

    private void OnHostExited(string reason)
    {
        if (_unloading) return;
        _ = Task.Run(() => RecoverAsync(reason));
    }

    private async Task RecoverAsync(string reason)
    {
        _log.Error("Plugin {Plugin} crashed: {Reason}", Info.Name, reason);
        try
        {
            _crashLog?.Record(new CrashRecord(Info.Path, Info.Name, DateTime.UtcNow, reason));
        }
        catch (Exception ex) { _log.Warning(ex, "Failed to record crash"); }

        if (Audio is not null) Audio.Faulted = true;
        IsRecovering = true;
        Crashed?.Invoke(reason);

        var now = DateTime.UtcNow;
        while (_restarts.Count > 0 && now - _restarts.Peek() > TimeSpan.FromMinutes(1)) _restarts.Dequeue();
        if (!AutoRestart || _restarts.Count >= MaxRestartsPerMinute)
        {
            IsRecovering = false;
            IsFaulted = true;
            Faulted?.Invoke();
            return;
        }
        _restarts.Enqueue(now);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_unloading) return;
            var old = _client;
            _client = null;
            if (old is not null) _ = old.DisposeAsync().AsTask();

            await StartHostAsync().ConfigureAwait(false);
            await RefreshAllAsync().ConfigureAwait(false);
            await RestoreStateCoreAsync(State).ConfigureAwait(false);

            if (Audio is not null)
            {
                await StartAudioCoreAsync().ConfigureAwait(false);
                Audio.Faulted = false;
            }
            if (_editorWanted)
            {
                await OpenEditorCoreAsync(_editorParent).ConfigureAwait(false);
            }
            IsRecovering = false;
            _log.Information("Plugin {Plugin} recovered after crash", Info.Name);
            Recovered?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Recovery of {Plugin} failed", Info.Name);
            IsRecovering = false;
            IsFaulted = true;
            Faulted?.Invoke();
        }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------- state (Rule 7 / 8)

    private void CaptureCachedState()
    {
        State.Parameters = _parameters.Select(p => p.Value).ToArray();
        State.Program = CurrentProgram;
        State.ProgramName = CurrentProgram >= 0 && CurrentProgram < _programs.Count ? _programs[CurrentProgram] : "";
    }

    private void StartSnapshotLoop()
    {
        _snapshotCts?.Cancel();
        var cts = _snapshotCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(SnapshotInterval, cts.Token).ConfigureAwait(false);
                    if (IsRecovering || _unloading || _client is not { IsAlive: true }) continue;
                    await SnapshotAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (ex is HostCrashedException or TimeoutException or PluginHostException) { }
            }
        });
    }

    /// <summary>Pulls the plugin's current state into Renaissance's own copy.</summary>
    public async Task<PluginState> SnapshotAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not { IsAlive: true }) return State;
            var values = await CallCoreAsync(new HostRequest { Command = HostCommands.GetParameterValues }).ConfigureAwait(false);
            var cur = await CallCoreAsync(new HostRequest { Command = HostCommands.GetProgram }).ConfigureAwait(false);
            var next = State.Clone();
            next.Parameters = values.Floats ?? next.Parameters;
            next.Program = cur.Index;
            CurrentProgram = cur.Index;
            next.ProgramName = cur.Text ?? "";
            if (Metadata is { ProgramChunks: true })
            {
                var chunk = await CallCoreAsync(new HostRequest { Command = HostCommands.GetChunk, Flag = false }).ConfigureAwait(false);
                next.Chunk = chunk.Data;
            }
            for (var i = 0; i < _parameters.Count && next.Parameters is { } v && i < v.Length; i++) _parameters[i].Value = v[i];
            State = next;
            return State;
        }
        catch (Exception ex) when (ex is HostCrashedException or TimeoutException)
        {
            return State;
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreStateAsync(PluginState state)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            State = state;
            await RestoreStateCoreAsync(state).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task RestoreStateCoreAsync(PluginState state)
    {
        if (Metadata is { ProgramChunks: true } && state.Chunk is { Length: > 0 })
        {
            await CallCoreAsync(new HostRequest { Command = HostCommands.SetChunk, Flag = false, Data = state.Chunk }).ConfigureAwait(false);
        }
        else
        {
            if (state.Program >= 0 && state.Program < Math.Max(1, Metadata?.NumPrograms ?? 1))
                await CallCoreAsync(new HostRequest { Command = HostCommands.SetProgram, Index = state.Program }).ConfigureAwait(false);
            for (var i = 0; i < state.Parameters.Length && i < _parameters.Count; i++)
                await CallCoreAsync(new HostRequest { Command = HostCommands.SetParameter, Index = i, Value = state.Parameters[i] }).ConfigureAwait(false);
        }
        await ReloadParametersCoreAsync().ConfigureAwait(false);
        var cur = await CallCoreAsync(new HostRequest { Command = HostCommands.GetProgram }).ConfigureAwait(false);
        CurrentProgram = cur.Index;
    }

    public byte[] SerializeState() => State.ToBytes();

    // ---------------------------------------------------------------- parameters & programs

    public async Task SetParameterAsync(int index, float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        if (index >= 0 && index < _parameters.Count) _parameters[index].Value = value;
        if (index >= 0 && index < State.Parameters.Length) State.Parameters[index] = value;
        await CallAsync(new HostRequest { Command = HostCommands.SetParameter, Index = index, Value = value }).ConfigureAwait(false);
    }

    public async Task<string> GetParameterDisplayAsync(int index)
    {
        var r = await CallAsync(new HostRequest { Command = HostCommands.GetParameter, Index = index }).ConfigureAwait(false);
        if (index >= 0 && index < _parameters.Count)
        {
            _parameters[index].Value = r.Value;
            _parameters[index].Display = r.Text ?? "";
        }
        return r.Text ?? "";
    }

    public async Task ReloadParametersAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await ReloadParametersCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
        ParametersRefreshed?.Invoke();
    }

    private async Task ReloadParametersCoreAsync()
    {
        var p = await CallCoreAsync(new HostRequest { Command = HostCommands.GetParameters }, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        _parameters = p.Parameters ?? _parameters;
        CaptureCachedState();
    }

    public async Task SelectProgramAsync(int index)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CallCoreAsync(new HostRequest { Command = HostCommands.SetProgram, Index = index }).ConfigureAwait(false);
            CurrentProgram = index;
            await ReloadParametersCoreAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        ParametersRefreshed?.Invoke();
    }

    public async Task RenameProgramAsync(string name)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CallCoreAsync(new HostRequest { Command = HostCommands.SetProgramName, Text = name }).ConfigureAwait(false);
            var pr = await CallCoreAsync(new HostRequest { Command = HostCommands.GetPrograms }, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            _programs = pr.Programs ?? _programs;
            CaptureCachedState();
        }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------- editor

    public async Task<(int Width, int Height)> OpenEditorAsync(long parentHandle)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _editorWanted = true;
            _editorParent = parentHandle;
            return await OpenEditorCoreAsync(parentHandle).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<(int, int)> OpenEditorCoreAsync(long parentHandle)
    {
        var open = await CallCoreAsync(new HostRequest { Command = HostCommands.EditorOpen }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await CallCoreAsync(new HostRequest { Command = HostCommands.EditorAttach, Handle = parentHandle }).ConfigureAwait(false);
        return (open.Width, open.Height);
    }

    public async Task ResizeEditorAsync(int width, int height)
    {
        try { await CallAsync(new HostRequest { Command = HostCommands.EditorResize, Width = width, Height = height }).ConfigureAwait(false); }
        catch (HostCrashedException) { }
    }

    public async Task CloseEditorAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _editorWanted = false;
            if (_client is { IsAlive: true })
                await CallCoreAsync(new HostRequest { Command = HostCommands.EditorClose }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HostCrashedException or TimeoutException) { }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------- audio / midi

    public async Task ConfigureAudioAsync(double sampleRate, int blockSize)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var meta = Metadata ?? throw new InvalidOperationException("Plugin is not loaded.");
            _sampleRate = sampleRate;
            _blockSize = blockSize;
            _audioInputs = Math.Clamp(meta.NumInputs, 0, 8);
            _audioOutputs = Math.Clamp(meta.NumOutputs, 0, 8);
            var old = Audio;
            Audio = null;
            if (old is not null)
            {
                old.Faulted = true;
                try { await CallCoreAsync(new HostRequest { Command = HostCommands.AudioStop }).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HostCrashedException or TimeoutException) { }
                old.Dispose();
            }
            Audio = new AudioBridge("Renaissance.Audio." + Guid.NewGuid().ToString("N"), Math.Max(blockSize, 64), _audioInputs, _audioOutputs);
            await StartAudioCoreAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task StartAudioCoreAsync()
    {
        await CallCoreAsync(new HostRequest
        {
            Command = HostCommands.AudioStart,
            Text = Audio!.Name,
            SampleRate = _sampleRate,
            BlockSize = Audio.Layout.MaxFrames,
            Inputs = _audioInputs,
            Outputs = _audioOutputs,
        }).ConfigureAwait(false);
    }

    public async Task SendSysExAsync(byte[] data) =>
        await CallAsync(new HostRequest { Command = HostCommands.SendSysEx, Data = data }).ConfigureAwait(false);

    // ---------------------------------------------------------------- presets

    public async Task<FxpFile> CaptureFxpAsync(string? name = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await CaptureFxpCoreAsync(name).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<FxpFile> CaptureFxpCoreAsync(string? name)
    {
        var meta = Metadata ?? throw new InvalidOperationException("Plugin is not loaded.");
        var fxp = new FxpFile
        {
            FxId = meta.UniqueId,
            FxVersion = meta.VendorVersion,
            ProgramName = name ?? (CurrentProgram >= 0 && CurrentProgram < _programs.Count ? _programs[CurrentProgram] : "Program"),
        };
        if (meta.ProgramChunks)
        {
            var chunk = await CallCoreAsync(new HostRequest { Command = HostCommands.GetChunk, Flag = true }).ConfigureAwait(false);
            fxp.IsChunk = true;
            fxp.Chunk = chunk.Data ?? [];
        }
        else
        {
            var values = await CallCoreAsync(new HostRequest { Command = HostCommands.GetParameterValues }).ConfigureAwait(false);
            fxp.Parameters = values.Floats ?? [];
        }
        return fxp;
    }

    public async Task ApplyFxpAsync(FxpFile fxp)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireMatchingPlugin(fxp.FxId);
            await ApplyFxpCoreAsync(fxp).ConfigureAwait(false);
            await ReloadParametersCoreAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        ParametersRefreshed?.Invoke();
    }

    private async Task ApplyFxpCoreAsync(FxpFile fxp)
    {
        if (fxp.IsChunk)
            await CallCoreAsync(new HostRequest { Command = HostCommands.SetChunk, Flag = true, Data = fxp.Chunk }).ConfigureAwait(false);
        else
            for (var i = 0; i < fxp.Parameters.Length && i < _parameters.Count; i++)
                await CallCoreAsync(new HostRequest { Command = HostCommands.SetParameter, Index = i, Value = fxp.Parameters[i] }).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(fxp.ProgramName))
            await CallCoreAsync(new HostRequest { Command = HostCommands.SetProgramName, Text = fxp.ProgramName }).ConfigureAwait(false);
    }

    public async Task<FxbFile> CaptureFxbAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var meta = Metadata ?? throw new InvalidOperationException("Plugin is not loaded.");
            var fxb = new FxbFile { FxId = meta.UniqueId, FxVersion = meta.VendorVersion, CurrentProgram = CurrentProgram };
            if (meta.ProgramChunks)
            {
                var chunk = await CallCoreAsync(new HostRequest { Command = HostCommands.GetChunk, Flag = false }).ConfigureAwait(false);
                fxb.IsChunk = true;
                fxb.Chunk = chunk.Data ?? [];
                return fxb;
            }

            var original = CurrentProgram;
            for (var i = 0; i < _programs.Count; i++)
            {
                await CallCoreAsync(new HostRequest { Command = HostCommands.SetProgram, Index = i }).ConfigureAwait(false);
                fxb.Programs.Add(await CaptureFxpCoreAsync(_programs[i]).ConfigureAwait(false));
            }
            await CallCoreAsync(new HostRequest { Command = HostCommands.SetProgram, Index = original }).ConfigureAwait(false);
            return fxb;
        }
        finally { _gate.Release(); }
    }

    public async Task ApplyFxbAsync(FxbFile fxb)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireMatchingPlugin(fxb.FxId);
            if (fxb.IsChunk)
            {
                await CallCoreAsync(new HostRequest { Command = HostCommands.SetChunk, Flag = false, Data = fxb.Chunk }).ConfigureAwait(false);
            }
            else
            {
                for (var i = 0; i < fxb.Programs.Count && i < _programs.Count; i++)
                {
                    await CallCoreAsync(new HostRequest { Command = HostCommands.SetProgram, Index = i }).ConfigureAwait(false);
                    await ApplyFxpCoreAsync(fxb.Programs[i]).ConfigureAwait(false);
                }
                await CallCoreAsync(new HostRequest { Command = HostCommands.SetProgram, Index = Math.Max(0, fxb.CurrentProgram) }).ConfigureAwait(false);
            }
            var pr = await CallCoreAsync(new HostRequest { Command = HostCommands.GetPrograms }, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            _programs = pr.Programs ?? _programs;
            var cur = await CallCoreAsync(new HostRequest { Command = HostCommands.GetProgram }).ConfigureAwait(false);
            CurrentProgram = cur.Index;
            await ReloadParametersCoreAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        ParametersRefreshed?.Invoke();
    }

    public async Task<RPreset> CaptureRPresetAsync(string name)
    {
        var meta = Metadata ?? throw new InvalidOperationException("Plugin is not loaded.");
        var fxp = await CaptureFxpAsync(name).ConfigureAwait(false);
        var values = fxp.IsChunk
            ? (await SnapshotAsync().ConfigureAwait(false)).Parameters
            : fxp.Parameters;
        return new RPreset
        {
            Name = name,
            ProgramName = name,
            PluginName = string.IsNullOrWhiteSpace(meta.Name) ? Info.Name : meta.Name,
            Vendor = meta.Vendor,
            UniqueId = meta.UniqueId,
            PluginVersion = meta.VendorVersion,
            Parameters = values,
            Chunk = fxp.IsChunk ? fxp.Chunk : null,
        };
    }

    public async Task ApplyRPresetAsync(RPreset preset)
    {
        RequireMatchingPlugin(preset.UniqueId);
        var fxp = preset.Chunk is { Length: > 0 }
            ? new FxpFile { IsChunk = true, Chunk = preset.Chunk, FxId = preset.UniqueId, ProgramName = preset.ProgramName }
            : new FxpFile { Parameters = preset.Parameters, FxId = preset.UniqueId, ProgramName = preset.ProgramName };
        await ApplyFxpAsync(fxp).ConfigureAwait(false);
    }

    private void RequireMatchingPlugin(int fxId)
    {
        var id = Metadata?.UniqueId ?? 0;
        if (fxId != 0 && id != 0 && fxId != id)
            throw new InvalidOperationException("This preset belongs to a different plugin.");
    }

    // ---------------------------------------------------------------- unload

    /// <summary>Save State → Close Editor → Disconnect Audio → Release Plugin → Shutdown Host.</summary>
    public async Task UnloadAsync()
    {
        if (_unloading) return;
        try { await SnapshotAsync().ConfigureAwait(false); } catch { }
        _unloading = true;
        _snapshotCts?.Cancel();

        await _gate.WaitAsync().ConfigureAwait(false);
        try { await TeardownAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task TeardownAsync()
    {
        _unloading = true;
        var client = _client;
        _client = null;
        IsLoaded = false;
        if (client is { IsAlive: true })
        {
            foreach (var cmd in new[] { HostCommands.EditorClose, HostCommands.AudioStop, HostCommands.Unload })
            {
                try { await client.SendAsync(new HostRequest { Command = cmd }, TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or HostCrashedException) { }
            }
        }
        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        Audio?.Dispose();
        Audio = null;
        _editorWanted = false;
    }

    public async ValueTask DisposeAsync()
    {
        await UnloadAsync().ConfigureAwait(false);
        _snapshotCts?.Dispose();
    }
}
