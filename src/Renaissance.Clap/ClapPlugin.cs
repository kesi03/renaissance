using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Renaissance.Core.Audio;
using Renaissance.Core.Embedding;

namespace Renaissance.Clap;

/// <summary>
/// One Renaissance CLAP instance. Thin wrapper only: audio, MIDI and automation are forwarded to the
/// shared-memory bridge, state and the editor to <see cref="WrapperLink"/> (Rules 1, 2, 9).
/// </summary>
internal sealed unsafe class ClapPluginInstance : IDisposable
{
    private const int ParamCount = 128;
    private const int MaxOutMidi = 256;

    private static readonly object ExtensionGate = new();
    private static bool _extensionsBuilt;

    private static clap_plugin_audio_ports* _audioPorts;
    private static clap_plugin_note_ports* _notePorts;
    private static clap_plugin_params* _paramsExt;
    private static clap_plugin_state* _stateExt;
    private static clap_plugin_gui* _guiExt;
    private static clap_plugin_latency* _latencyExt;

    private readonly bool _instrument;
    private readonly WrapperLink _link;
    private readonly string? _modulePath;
    private readonly float[] _params = new float[ParamCount];

    private clap_plugin* _plugin;
    private clap_host* _host;
    private GCHandle _self;

    private clap_host_gui* _hostGui;
    private clap_host_state* _hostState;
    private int _hostExtensionsQueried;

    private IntPtr _parent;
    private int _guiCreated;
    private int _started;
    private int _processing;
    private double _sampleRate = 44100;
    private uint _maxFrames = 512;

    private ClapPluginInstance(clap_host* host, bool instrument)
    {
        _host = host;
        _instrument = instrument;

        var path = ModuleInfo.Path();
        _modulePath = path;
        if (path is not null) WrapperLink.WrapperDirectory = Path.GetDirectoryName(path);

        _link = new WrapperLink(OnResizeRequested);
        _self = GCHandle.Alloc(this);

        BuildExtensions();
    }

    public static clap_plugin* Create(clap_host* host, bool instrument, clap_plugin_descriptor* descriptor)
    {
        var instance = new ClapPluginInstance(host, instrument);
        var plugin = (clap_plugin*)NativeMemory.AllocZeroed((nuint)sizeof(clap_plugin));

        plugin->desc = descriptor;
        plugin->plugin_data = (void*)GCHandle.ToIntPtr(instance._self);

        plugin->init = &Init;
        plugin->destroy = &Destroy;
        plugin->activate = &Activate;
        plugin->deactivate = &Deactivate;
        plugin->start_processing = &StartProcessing;
        plugin->stop_processing = &StopProcessing;
        plugin->reset = &Reset;
        plugin->process = &Process;
        plugin->get_extension = &GetExtension;
        plugin->on_main_thread = &OnMainThread;

        instance._plugin = plugin;
        Log.Write($"Instance created ({(instrument ? "instrument" : "effect")}) from {instance._modulePath}");
        return plugin;
    }

    private static ClapPluginInstance? Owner(clap_plugin* plugin)
    {
        if (plugin is null || plugin->plugin_data is null) return null;
        var handle = GCHandle.FromIntPtr((IntPtr)plugin->plugin_data);
        return handle.Target as ClapPluginInstance;
    }

    /// <summary>The app is only launched once the host actually uses the plugin, not while it is merely scanned.</summary>
    private void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _link.Start();
        _link.Configure(_sampleRate, (int)_maxFrames);
    }

    public void Dispose()
    {
        _link.Dispose();

        if (_self.IsAllocated) _self.Free();

        if (_plugin is not null)
        {
            NativeMemory.Free(_plugin);
            _plugin = null;
        }
    }

    // ---------------------------------------------------------------- lifecycle

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Init(clap_plugin* plugin)
    {
        try
        {
            if (Owner(plugin) is null) return 0;
            Log.Write("plugin init");
            return 1;
        }
        catch (Exception ex)
        {
            Log.Write("init failed: " + ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Destroy(clap_plugin* plugin)
    {
        try
        {
            Owner(plugin)?.Dispose();
            Log.Write("plugin destroy");
        }
        catch (Exception ex)
        {
            Log.Write("destroy failed: " + ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Activate(clap_plugin* plugin, double sampleRate, uint minFrames, uint maxFrames)
    {
        try
        {
            var owner = Owner(plugin);
            if (owner is null) return 0;

            owner._sampleRate = sampleRate > 0 ? sampleRate : 44100;
            owner._maxFrames = Math.Max(1, maxFrames);
            owner.EnsureStarted();
            owner._link.Configure(owner._sampleRate, (int)owner._maxFrames);

            Log.Write($"activate sr={owner._sampleRate} min={minFrames} max={owner._maxFrames}");
            return 1;
        }
        catch (Exception ex)
        {
            Log.Write("activate failed: " + ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Deactivate(clap_plugin* plugin)
    {
        Log.Write("deactivate");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte StartProcessing(clap_plugin* plugin)
    {
        var owner = Owner(plugin);
        if (owner is null) return 0;
        owner.EnsureStarted();
        Interlocked.Exchange(ref owner._processing, 1);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void StopProcessing(clap_plugin* plugin)
    {
        var owner = Owner(plugin);
        if (owner is not null) Interlocked.Exchange(ref owner._processing, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Reset(clap_plugin* plugin)
    {
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMainThread(clap_plugin* plugin)
    {
    }

    // ---------------------------------------------------------------- audio / midi

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Process(clap_plugin* plugin, clap_process* process)
    {
        try
        {
            var owner = Owner(plugin);
            if (owner is null || process is null) return ClapConstants.ProcessError;
            return owner.ProcessBlock(process);
        }
        catch (Exception ex)
        {
            Log.Write("process failed: " + ex);
            return ClapConstants.ProcessError;
        }
    }

    private int ProcessBlock(clap_process* process)
    {
        var frames = (int)process->frames_count;
        if (frames <= 0) return ClapConstants.ProcessContinue;

        var output = process->audio_outputs_count > 0 ? &process->audio_outputs[0] : null;
        var outputChannels = output is not null ? (int)output->channel_count : 0;
        var outputs = output is not null ? output->data32 : null;

        var input = process->audio_inputs_count > 0 ? &process->audio_inputs[0] : null;
        var inputChannels = input is not null ? (int)input->channel_count : 0;
        var inputs = input is not null ? input->data32 : null;

        var bridge = _link.Bridge;
        if (bridge is not null) ReadEvents(process->in_events, bridge);

        if (outputChannels <= 0 || outputs is null)
        {
            // The host chose a 64-bit buffer, which this wrapper does not advertise.
            return ClapConstants.ProcessContinue;
        }

        if (bridge is null)
        {
            for (var ch = 0; ch < outputChannels; ch++)
            {
                var destination = new Span<float>(outputs[ch], frames);
                if (!_instrument && inputs is not null && ch < inputChannels)
                    new ReadOnlySpan<float>(inputs[ch], frames).CopyTo(destination);
                else
                    destination.Clear();
            }
            return ClapConstants.ProcessContinue;
        }

        bridge.ProcessNative(inputs, inputChannels, outputs, outputChannels, frames);
        WriteOutgoingMidi(process->out_events, bridge);
        return ClapConstants.ProcessContinue;
    }

    /// <summary>Reads CLAP core events and forwards them to the app. Returns true if a parameter changed.</summary>
    private bool ReadEvents(clap_input_events* events, AudioBridge bridge)
    {
        var parametersChanged = false;
        if (events is null || events->size is null || events->get is null) return false;

        var count = events->size(events);
        for (var i = 0u; i < count; i++)
        {
            var header = events->get(events, i);
            if (header is null || header->space_id != ClapConstants.CoreEventSpaceId) continue;

            switch (header->type)
            {
                case ClapConstants.EventNoteOn:
                case ClapConstants.EventNoteOff:
                case ClapConstants.EventNoteChoke:
                case ClapConstants.EventNoteEnd:
                    HandleNote(bridge, header->type, (clap_event_note*)header);
                    break;

                case ClapConstants.EventParamValue:
                {
                    var value = (clap_event_param_value*)header;
                    if (value->param_id < ParamCount)
                    {
                        var plain = (float)value->value;
                        _params[value->param_id] = plain;
                        bridge.ParamIn.TryWrite(new ParamEvent((int)value->param_id, plain));
                        parametersChanged = true;
                    }
                    break;
                }

                case ClapConstants.EventParamMod:
                {
                    var mod = (clap_event_param_mod*)header;
                    if (mod->param_id < ParamCount)
                    {
                        var plain = Math.Clamp(_params[mod->param_id] + (float)mod->amount, 0f, 1f);
                        bridge.ParamIn.TryWrite(new ParamEvent((int)mod->param_id, plain));
                        parametersChanged = true;
                    }
                    break;
                }

                case ClapConstants.EventMidi:
                {
                    var midi = (clap_event_midi*)header;
                    bridge.MidiIn.TryWrite(new MidiEvent(
                        (int)header->time,
                        midi->data[0],
                        midi->data[1],
                        midi->data[2]));
                    break;
                }
            }
        }
        return parametersChanged;
    }

    private static void HandleNote(AudioBridge bridge, ushort type, clap_event_note* note)
    {
        var channel = note->channel < 0 ? 0 : note->channel & 0xF;
        var key = note->key < 0 ? 0 : note->key & 0x7F;
        var time = (int)note->header.time;

        switch (type)
        {
            case ClapConstants.EventNoteOn:
            {
                // A CLAP note-on with velocity 0 is still a note-on, so never encode it as MIDI note-off.
                var velocity = Math.Clamp((int)MathF.Round((float)note->velocity * 127f), 1, 127);
                bridge.MidiIn.TryWrite(MidiEvent.NoteOn(channel, key, velocity, time));
                break;
            }
            case ClapConstants.EventNoteOff:
            case ClapConstants.EventNoteEnd:
            {
                var velocity = Math.Clamp((int)MathF.Round((float)note->velocity * 127f), 0, 127);
                bridge.MidiIn.TryWrite(MidiEvent.NoteOff(channel, key, velocity, time));
                break;
            }
            default: // note choke
                if (note->key >= 0) bridge.MidiIn.TryWrite(MidiEvent.NoteOff(channel, key, 0, time));
                else bridge.MidiIn.TryWrite(MidiEvent.ControlChange(channel, 123, 0, time));
                break;
        }
    }

    private static void WriteOutgoingMidi(clap_output_events* events, AudioBridge bridge)
    {
        if (events is null || events->try_push is null) return;

        var ev = stackalloc clap_event_midi[1];
        var written = 0;
        while (written < MaxOutMidi && bridge.MidiOut.TryRead(out var midi))
        {
            ev->header.size = (uint)sizeof(clap_event_midi);
            ev->header.time = (uint)Math.Max(0, midi.DeltaFrames);
            ev->header.space_id = ClapConstants.CoreEventSpaceId;
            ev->header.type = ClapConstants.EventMidi;
            ev->header.flags = 0;
            ev->port_index = 0;
            ev->data[0] = midi.Status;
            ev->data[1] = midi.Data1;
            ev->data[2] = midi.Data2;

            if (events->try_push(events, &ev->header) == 0) break;
            written++;
        }
    }

    // ---------------------------------------------------------------- extensions

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* GetExtension(clap_plugin* plugin, byte* id)
    {
        try
        {
            if (id is null) return null;
            if (Strings.Equals(id, ClapConstants.ExtAudioPorts)) return _audioPorts;
            if (Strings.Equals(id, ClapConstants.ExtNotePorts)) return _notePorts;
            if (Strings.Equals(id, ClapConstants.ExtParams)) return _paramsExt;
            if (Strings.Equals(id, ClapConstants.ExtState)) return _stateExt;
            if (Strings.Equals(id, ClapConstants.ExtGui)) return _guiExt;
            if (Strings.Equals(id, ClapConstants.ExtLatency)) return _latencyExt;
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("get_extension failed: " + ex);
            return null;
        }
    }

    private static void BuildExtensions()
    {
        if (_extensionsBuilt) return;
        lock (ExtensionGate)
        {
            if (_extensionsBuilt) return;

            _audioPorts = (clap_plugin_audio_ports*)NativeMemory.AllocZeroed((nuint)sizeof(clap_plugin_audio_ports));
            _audioPorts->count = &AudioPortCount;
            _audioPorts->get = &AudioPortGet;

            _notePorts = (clap_plugin_note_ports*)NativeMemory.AllocZeroed((nuint)sizeof(clap_plugin_note_ports));
            _notePorts->count = &NotePortCount;
            _notePorts->get = &NotePortGet;

            _paramsExt = (clap_plugin_params*)NativeMemory.AllocZeroed((nuint)sizeof(clap_plugin_params));
            _paramsExt->count = &ParamsCount;
            _paramsExt->get_info = &ParamsGetInfo;
            _paramsExt->get_value = &ParamsGetValue;
            _paramsExt->value_to_text = &ParamsValueToText;
            _paramsExt->text_to_value = &ParamsTextToValue;
            _paramsExt->flush = &ParamsFlush;

            _stateExt = (clap_plugin_state*)NativeMemory.AllocZeroed((nuint)sizeof(clap_plugin_state));
            _stateExt->save = &StateSave;
            _stateExt->load = &StateLoad;

            _guiExt = (clap_plugin_gui*)NativeMemory.AllocZeroed((nuint)sizeof(clap_plugin_gui));
            _guiExt->is_api_supported = &GuiIsApiSupported;
            _guiExt->get_preferred_api = &GuiGetPreferredApi;
            _guiExt->create = &GuiCreate;
            _guiExt->destroy = &GuiDestroy;
            _guiExt->set_scale = &GuiSetScale;
            _guiExt->get_size = &GuiGetSize;
            _guiExt->can_resize = &GuiCanResize;
            _guiExt->get_resize_hints = &GuiGetResizeHints;
            _guiExt->adjust_size = &GuiAdjustSize;
            _guiExt->set_size = &GuiSetSize;
            _guiExt->set_parent = &GuiSetParent;
            _guiExt->set_transient = &GuiSetTransient;
            _guiExt->suggest_title = &GuiSuggestTitle;
            _guiExt->show = &GuiShow;
            _guiExt->hide = &GuiHide;

            _latencyExt = (clap_plugin_latency*)NativeMemory.AllocZeroed((nuint)sizeof(clap_plugin_latency));
            _latencyExt->get = &LatencyGet;

            _extensionsBuilt = true;
        }
    }

    // ---------------------------------------------------------------- audio ports

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint AudioPortCount(clap_plugin* plugin, byte isInput)
    {
        var owner = Owner(plugin);
        if (owner is null) return 0;
        return isInput != 0 ? (owner._instrument ? 0u : 1u) : 1u;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte AudioPortGet(clap_plugin* plugin, uint index, byte isInput, clap_audio_port_info* info)
    {
        var owner = Owner(plugin);
        if (owner is null || info is null || index != 0) return 0;
        if (isInput != 0 && owner._instrument) return 0;

        info->id = 0;
        Strings.Write(info->name, 256, isInput != 0 ? "Stereo In" : "Stereo Out");
        info->flags = ClapConstants.AudioPortIsMain;
        info->channel_count = 2;
        info->port_type = ClapStrings.PortStereo;
        // The bridge copies input into shared memory before writing output, so in-place is safe
        // whenever there is a matching input port.
        info->in_place_pair = isInput != 0 || !owner._instrument ? 0u : ClapConstants.InvalidId;
        return 1;
    }

    // ---------------------------------------------------------------- note ports

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint NotePortCount(clap_plugin* plugin, byte isInput) => Owner(plugin) is null ? 0u : 1u;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte NotePortGet(clap_plugin* plugin, uint index, byte isInput, clap_note_port_info* info)
    {
        if (Owner(plugin) is null || info is null || index != 0) return 0;

        info->id = 0;
        info->supported_dialects = ClapConstants.NoteDialectClap | ClapConstants.NoteDialectMidi;
        info->preferred_dialect = ClapConstants.NoteDialectClap;
        Strings.Write(info->name, 256, isInput != 0 ? "MIDI In" : "MIDI Out");
        return 1;
    }

    // ---------------------------------------------------------------- parameters

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint ParamsCount(clap_plugin* plugin) => Owner(plugin) is null ? 0u : ParamCount;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ParamsGetInfo(clap_plugin* plugin, uint index, clap_param_info* info)
    {
        if (Owner(plugin) is null || info is null || index >= ParamCount) return 0;

        info->id = index;
        info->flags = ClapConstants.ParamIsAutomatable;
        info->cookie = null;
        Strings.Write(info->name, 256, "P" + (index + 1));
        Strings.Write(info->module, 1024, "");
        info->min_value = 0;
        info->max_value = 1;
        info->default_value = 0;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ParamsGetValue(clap_plugin* plugin, uint id, double* value)
    {
        var owner = Owner(plugin);
        if (owner is null || value is null || id >= ParamCount) return 0;
        *value = owner._params[id];
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ParamsValueToText(clap_plugin* plugin, uint id, double value, byte* buffer, uint capacity)
    {
        if (buffer is null || capacity == 0) return 0;
        Strings.Write(buffer, (int)capacity, value.ToString("0.00", CultureInfo.InvariantCulture));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ParamsTextToValue(clap_plugin* plugin, uint id, byte* text, double* value)
    {
        if (text is null || value is null) return 0;
        var s = Marshal.PtrToStringUTF8((IntPtr)text);
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return 0;
        *value = Math.Clamp(parsed, 0, 1);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ParamsFlush(clap_plugin* plugin, clap_input_events* input, clap_output_events* output)
    {
        var owner = Owner(plugin);
        var bridge = owner?._link.Bridge;
        if (owner is null || bridge is null) return;

        if (owner.ReadEvents(input, bridge)) owner.MarkStateDirty();
    }

    private void MarkStateDirty()
    {
        EnsureHostExtensions();
        if (_hostState is not null && _hostState->mark_dirty is not null && _host is not null)
            _hostState->mark_dirty(_host);
    }

    // ---------------------------------------------------------------- state

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte StateSave(clap_plugin* plugin, clap_ostream* stream)
    {
        try
        {
            var owner = Owner(plugin);
            if (owner is null || stream is null || stream->write is null) return 0;

            var data = owner._started != 0 ? owner._link.GetState() : [];
            return WriteAll(stream, data) ? (byte)1 : (byte)0;
        }
        catch (Exception ex)
        {
            Log.Write("state save failed: " + ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte StateLoad(clap_plugin* plugin, clap_istream* stream)
    {
        try
        {
            var owner = Owner(plugin);
            if (owner is null || stream is null || stream->read is null) return 0;

            var data = ReadAll(stream);
            owner.EnsureStarted();
            owner._link.SetState(data);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Write("state load failed: " + ex);
            return 0;
        }
    }

    private static bool WriteAll(clap_ostream* stream, byte[] data)
    {
        if (data.Length == 0) return true;
        fixed (byte* buffer = data)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var written = stream->write(stream, buffer + offset, (ulong)(data.Length - offset));
                if (written <= 0) return false;
                offset += (int)written;
            }
        }
        return true;
    }

    private static byte[] ReadAll(clap_istream* stream)
    {
        using var result = new MemoryStream();
        var buffer = new byte[65536];
        fixed (byte* p = buffer)
        {
            while (true)
            {
                var read = stream->read(stream, p, (ulong)buffer.Length);
                if (read <= 0) break;
                result.Write(buffer, 0, (int)read);
            }
        }
        return result.ToArray();
    }

    // ---------------------------------------------------------------- gui

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GuiIsApiSupported(clap_plugin* plugin, byte* api, byte isFloating) =>
        (byte)(isFloating == 0 && Strings.Equals(api, ClapConstants.WindowApiWin32) ? 1 : 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GuiGetPreferredApi(clap_plugin* plugin, byte** api, byte* isFloating)
    {
        if (api is null || isFloating is null) return 0;
        *api = ClapStrings.WindowApiWin32;
        *isFloating = 0;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GuiCreate(clap_plugin* plugin, byte* api, byte isFloating)
    {
        var owner = Owner(plugin);
        if (owner is null || isFloating != 0 || !Strings.Equals(api, ClapConstants.WindowApiWin32)) return 0;
        owner.EnsureStarted();
        Interlocked.Exchange(ref owner._guiCreated, 1);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void GuiDestroy(clap_plugin* plugin)
    {
        var owner = Owner(plugin);
        if (owner is null) return;
        owner._link.DetachEditor();
        owner._parent = IntPtr.Zero;
        Interlocked.Exchange(ref owner._guiCreated, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GuiSetScale(clap_plugin* plugin, double scale) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GuiGetSize(clap_plugin* plugin, uint* width, uint* height)
    {
        var owner = Owner(plugin);
        if (owner is null || width is null || height is null) return 0;
        *width = (uint)owner._link.EditorWidth;
        *height = (uint)owner._link.EditorHeight;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GuiCanResize(clap_plugin* plugin) => Owner(plugin) is null ? (byte)0 : (byte)1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GuiGetResizeHints(clap_plugin* plugin, clap_gui_resize_hints* hints)
    {
        if (hints is null) return 0;
        hints->can_resize_horizontally = 1;
        hints->can_resize_vertically = 1;
        hints->preserve_aspect_ratio = 0;
        hints->aspect_ratio_width = 0;
        hints->aspect_ratio_height = 0;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GuiAdjustSize(clap_plugin* plugin, uint* width, uint* height)
    {
        if (width is null || height is null) return 0;
        *width = Math.Max(200, *width);
        *height = Math.Max(150, *height);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GuiSetSize(clap_plugin* plugin, uint width, uint height)
    {
        var owner = Owner(plugin);
        if (owner is null) return 0;
        owner._link.ResizeEditorWindow((int)Math.Max(200, width), (int)Math.Max(150, height));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GuiSetParent(clap_plugin* plugin, clap_window* window)
    {
        var owner = Owner(plugin);
        if (owner is null || window is null || !Strings.Equals(window->api, ClapConstants.WindowApiWin32)) return 0;

        owner.EnsureStarted();
        owner._parent = (IntPtr)window->ptr;
        owner._link.AttachEditor(owner._parent);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GuiSetTransient(clap_plugin* plugin, clap_window* window) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void GuiSuggestTitle(clap_plugin* plugin, byte* title)
    {
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GuiShow(clap_plugin* plugin)
    {
        var owner = Owner(plugin);
        if (owner is null) return 0;
        if (owner._parent != IntPtr.Zero) owner._link.AttachEditor(owner._parent);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte GuiHide(clap_plugin* plugin)
    {
        var owner = Owner(plugin);
        if (owner is null) return 0;
        owner._link.DetachEditor();
        return 1;
    }

    private void OnResizeRequested(int width, int height)
    {
        try
        {
            EnsureHostExtensions();
            if (_hostGui is not null && _hostGui->request_resize is not null && _host is not null)
                _hostGui->request_resize(_host, (uint)width, (uint)height);
        }
        catch (Exception ex)
        {
            Log.Write("resize request failed: " + ex);
        }
    }

    private void EnsureHostExtensions()
    {
        if (Interlocked.Exchange(ref _hostExtensionsQueried, 1) != 0) return;
        if (_host is null || _host->get_extension is null) return;

        _hostGui = (clap_host_gui*)_host->get_extension(_host, ClapStrings.ExtGui);
        _hostState = (clap_host_state*)_host->get_extension(_host, ClapStrings.ExtState);
    }

    // ---------------------------------------------------------------- latency

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint LatencyGet(clap_plugin* plugin) => 0;
}
