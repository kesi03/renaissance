using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Renaissance.Core.Audio;
using Renaissance.Core.Embedding;

namespace Renaissance.Vst3;

/// <summary>
/// One Renaissance VST3 instance (a "single component" plug-in: processor and controller are the same object).
/// No business logic lives here: audio, MIDI and automation are forwarded to the shared-memory bridge,
/// state and the editor to <see cref="WrapperLink"/>, exactly as in the VST2 wrapper (Rule 9).
/// </summary>
internal sealed unsafe class Vst3Component
{
    public const int GenericParams = 128;
    public const int MidiParamBase = 1000;
    private const int MidiParams = 130;          // CC 0..127, aftertouch (128), pitch bend (129)
    private const int AudioMedia = 0, EventMedia = 1, Input = 0, Output = 1;

    private static readonly void** CompVtbl = BuildComponentVtable();
    private static readonly void** ProcVtbl = BuildProcessorVtable();
    private static readonly void** CtxVtbl = BuildContextVtable();
    private static readonly void** CtrlVtbl = BuildControllerVtable();
    private static readonly void** MidiVtbl = BuildMidiMappingVtable();

    private readonly bool _instrument;
    private readonly WrapperLink _link;
    private readonly float[] _params = new float[GenericParams];
    private readonly IntPtr[] _blocks = new IntPtr[5];
    private GCHandle _self;
    private int _refs = 1;
    private int _started;
    private double _sampleRate = 44100;
    private int _blockSize = 512;
    private IntPtr _handler;
    private Vst3View? _view;

    public Vst3Component(bool instrument)
    {
        _instrument = instrument;
        var path = ModuleInfo.Path();
        if (path is not null) WrapperLink.WrapperDirectory = Path.GetDirectoryName(path);
        _link = new WrapperLink((w, h) =>
        {
            PendingWidth = w;
            PendingHeight = h;
        });

        _self = GCHandle.Alloc(this);
        _blocks[0] = Com.NewBlock(CompVtbl, _self);
        _blocks[1] = Com.NewBlock(ProcVtbl, _self);
        _blocks[2] = Com.NewBlock(CtxVtbl, _self);
        _blocks[3] = Com.NewBlock(CtrlVtbl, _self);
        _blocks[4] = Com.NewBlock(MidiVtbl, _self);
        Log.Write($"Component created ({(instrument ? "instrument" : "effect")})");
    }

    public WrapperLink Link => _link;
    public volatile int PendingWidth;
    public volatile int PendingHeight;

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _link.Start();
        _link.Configure(_sampleRate, _blockSize);
    }

    // ---------------------------------------------------------------- COM identity / lifetime

    public int Query(byte* iid, IntPtr* obj)
    {
        IntPtr block;
        if (Tuids.Eq(iid, Tuids.FUnknown) || Tuids.Eq(iid, Tuids.IPluginBase) || Tuids.Eq(iid, Tuids.IComponent)) block = _blocks[0];
        else if (Tuids.Eq(iid, Tuids.IAudioProcessor)) block = _blocks[1];
        else if (Tuids.Eq(iid, Tuids.IProcessContextRequirements)) block = _blocks[2];
        else if (Tuids.Eq(iid, Tuids.IEditController)) block = _blocks[3];
        else if (Tuids.Eq(iid, Tuids.IMidiMapping)) block = _blocks[4];
        else
        {
            *obj = IntPtr.Zero;
            return R.NoInterface;
        }
        AddRef();
        *obj = block;
        return R.Ok;
    }

    public uint AddRef() => (uint)Interlocked.Increment(ref _refs);

    public uint Release()
    {
        var n = Interlocked.Decrement(ref _refs);
        if (n == 0) Destroy();
        return (uint)Math.Max(0, n);
    }

    private void Destroy()
    {
        Log.Write("Component destroyed");
        if (_handler != IntPtr.Zero) Com.Release(_handler);
        _handler = IntPtr.Zero;
        _link.Dispose();
        foreach (var b in _blocks) NativeMemory.Free((void*)b);
        if (_self.IsAllocated) _self.Free();
    }

    public IntPtr CreateView()
    {
        _view ??= new Vst3View(this);
        _view.AddRef();
        return _view.Block;
    }

    // ---------------------------------------------------------------- vtable construction

    private static void** BuildComponentVtable() => Com.Vtable(
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)&QI,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&AddRefThunk,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&ReleaseThunk,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&Initialize,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&Terminate,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)&GetControllerClassId,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int>)&SetIoMode,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, int>)&GetBusCount,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, int, byte*, int>)&GetBusInfo,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&GetRoutingInfo,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, int, byte, int>)&ActivateBus,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte, int>)&SetActive,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&SetComponentState,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&GetComponentState);

    private static void** BuildProcessorVtable() => Com.Vtable(
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)&QI,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&AddRefThunk,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&ReleaseThunk,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong*, int, ulong*, int, int>)&SetBusArrangements,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, ulong*, int>)&GetBusArrangement,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int>)&CanProcessSampleSize,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&GetLatencySamples,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)&SetupProcessing,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte, int>)&SetProcessing,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)&Process,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&GetTailSamples);

    private static void** BuildContextVtable() => Com.Vtable(
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)&QI,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&AddRefThunk,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&ReleaseThunk,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&GetProcessContextRequirements);

    private static void** BuildControllerVtable() => Com.Vtable(
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)&QI,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&AddRefThunk,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&ReleaseThunk,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&Initialize,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&Terminate,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&ControllerSetComponentState,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&ControllerSetState,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&ControllerGetState,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&GetParameterCount,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int>)&GetParameterInfo,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, double, char*, int>)&GetParamStringByValue,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, char*, double*, int>)&GetParamValueByString,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, double, double>)&NormalizedToPlain,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, double, double>)&PlainToNormalized,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, double>)&GetParamNormalized,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, double, int>)&SetParamNormalized,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&SetComponentHandler,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)&CreateViewThunk);

    private static void** BuildMidiMappingVtable() => Com.Vtable(
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)&QI,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&AddRefThunk,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&ReleaseThunk,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, short, short, uint*, int>)&GetMidiControllerAssignment);

    // ---------------------------------------------------------------- FUnknown thunks (shared by all blocks)

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QI(IntPtr self, byte* iid, IntPtr* obj) => Com.Owner<Vst3Component>(self).Query(iid, obj);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint AddRefThunk(IntPtr self) => Com.Owner<Vst3Component>(self).AddRef();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint ReleaseThunk(IntPtr self) => Com.Owner<Vst3Component>(self).Release();

    // ---------------------------------------------------------------- IComponent

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Initialize(IntPtr self, IntPtr context) => R.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Terminate(IntPtr self) => R.Ok;

    // Single-component plug-in: the controller is this same object.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetControllerClassId(IntPtr self, byte* classId) => R.NotImplemented;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetIoMode(IntPtr self, int mode) => R.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetBusCount(IntPtr self, int type, int dir)
    {
        var c = Com.Owner<Vst3Component>(self);
        if (type == AudioMedia) return dir == Input ? (c._instrument ? 0 : 1) : 1;
        if (type == EventMedia) return 1;
        return 0;
    }

    // BusInfo: int32 mediaType @0, int32 direction @4, int32 channelCount @8, char16 name[128] @12, int32 busType @268, uint32 flags @272
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetBusInfo(IntPtr self, int type, int dir, int index, byte* bus)
    {
        var c = Com.Owner<Vst3Component>(self);
        if (index != 0 || (type != AudioMedia && type != EventMedia)) return R.InvalidArgument;
        if (type == AudioMedia && dir == Input && c._instrument) return R.InvalidArgument;

        *(int*)(bus + 0) = type;
        *(int*)(bus + 4) = dir;
        *(int*)(bus + 8) = type == AudioMedia ? 2 : 16;
        Com.PutUtf16((char*)(bus + 12), 128, type == AudioMedia ? (dir == Input ? "Stereo In" : "Stereo Out") : (dir == Input ? "MIDI In" : "MIDI Out"));
        *(int*)(bus + 268) = 0;  // kMain
        *(uint*)(bus + 272) = 1; // kDefaultActive
        return R.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetRoutingInfo(IntPtr self, IntPtr inInfo, IntPtr outInfo) => R.NotImplemented;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ActivateBus(IntPtr self, int type, int dir, int index, byte state) => R.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetActive(IntPtr self, byte state)
    {
        if (state != 0) Com.Owner<Vst3Component>(self).EnsureStarted();
        return R.Ok;
    }

    /// <summary>DAW project load.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetComponentState(IntPtr self, IntPtr stream)
    {
        try
        {
            var c = Com.Owner<Vst3Component>(self);
            var data = Streams.ReadAll(stream);
            if (data.Length > 0)
            {
                c.EnsureStarted();
                c._link.SetState(data);
            }
            return R.Ok;
        }
        catch (Exception ex)
        {
            Log.Write("setState failed: " + ex);
            return R.InternalError;
        }
    }

    /// <summary>DAW project save.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetComponentState(IntPtr self, IntPtr stream)
    {
        try
        {
            var c = Com.Owner<Vst3Component>(self);
            var data = c._started != 0 ? c._link.GetState() : [];
            Streams.WriteAll(stream, data);
            return R.Ok;
        }
        catch (Exception ex)
        {
            Log.Write("getState failed: " + ex);
            return R.InternalError;
        }
    }

    // ---------------------------------------------------------------- IAudioProcessor

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetBusArrangements(IntPtr self, ulong* inputs, int numIns, ulong* outputs, int numOuts)
    {
        const ulong stereo = 3;
        var c = Com.Owner<Vst3Component>(self);
        var wantIns = c._instrument ? 0 : 1;
        if (numIns != wantIns || numOuts != 1) return R.False;
        if (numIns == 1 && inputs[0] != stereo) return R.False;
        return outputs[0] == stereo ? R.Ok : R.False;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetBusArrangement(IntPtr self, int dir, int index, ulong* arrangement)
    {
        *arrangement = 3; // stereo
        return index == 0 ? R.Ok : R.InvalidArgument;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CanProcessSampleSize(IntPtr self, int size) => size == 0 ? R.Ok : R.False;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint GetLatencySamples(IntPtr self) => 0;

    // ProcessSetup: int32 processMode @0, int32 symbolicSampleSize @4, int32 maxSamplesPerBlock @8, double sampleRate @16
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetupProcessing(IntPtr self, byte* setup)
    {
        var c = Com.Owner<Vst3Component>(self);
        c._blockSize = Math.Max(64, *(int*)(setup + 8));
        c._sampleRate = *(double*)(setup + 16);
        c.EnsureStarted();
        c._link.Configure(c._sampleRate, c._blockSize);
        return R.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetProcessing(IntPtr self, byte state) => R.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Process(IntPtr self, byte* data)
    {
        try { return Com.Owner<Vst3Component>(self).ProcessBlock(data); }
        catch { return R.InternalError; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint GetTailSamples(IntPtr self) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint GetProcessContextRequirements(IntPtr self) => 0;

    // ProcessData: int32 processMode @0, symbolicSampleSize @4, numSamples @8, numInputs @12, numOutputs @16,
    // inputs* @24, outputs* @32, inputParameterChanges @40, outputParameterChanges @48, inputEvents @56, outputEvents @64
    // AudioBusBuffers (24 bytes): int32 numChannels @0, uint64 silenceFlags @8, float** buffers @16
    private int ProcessBlock(byte* data)
    {
        var frames = *(int*)(data + 8);
        var numIn = *(int*)(data + 12);
        var numOut = *(int*)(data + 16);
        var inBus = *(byte**)(data + 24);
        var outBus = *(byte**)(data + 32);
        var bridge = _link.Bridge;

        var paramChanges = *(IntPtr*)(data + 40);
        if (paramChanges != IntPtr.Zero && bridge is not null) ReadParameterChanges(paramChanges, bridge);
        var events = *(IntPtr*)(data + 56);
        if (events != IntPtr.Zero && bridge is not null) ReadEvents(events, bridge);

        if (numOut < 1 || outBus == null || frames <= 0) return R.Ok;
        var outCh = *(int*)outBus;
        var outs = *(float***)(outBus + 16);
        var inCh = numIn > 0 && inBus != null ? *(int*)inBus : 0;
        var ins = inCh > 0 ? *(float***)(inBus + 16) : null;
        if (outCh <= 0 || outs == null) return R.Ok;

        if (bridge is null)
        {
            for (var ch = 0; ch < outCh; ch++)
            {
                var o = new Span<float>(outs[ch], frames);
                if (!_instrument && ch < inCh) new ReadOnlySpan<float>(ins[ch], frames).CopyTo(o);
                else o.Clear();
            }
            return R.Ok;
        }

        bridge.ProcessNative(ins, inCh, outs, outCh, frames);
        var outEvents = *(IntPtr*)(data + 64);
        if (outEvents != IntPtr.Zero) WriteOutgoingMidi(outEvents, bridge);
        return R.Ok;
    }

    private void ReadParameterChanges(IntPtr changes, AudioBridge bridge)
    {
        var count = ((delegate* unmanaged[Cdecl]<IntPtr, int>)Com.VtableOf(changes)[3])(changes);
        for (var i = 0; i < count; i++)
        {
            var queue = ((delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr>)Com.VtableOf(changes)[4])(changes, i);
            if (queue == IntPtr.Zero) continue;
            var id = ((delegate* unmanaged[Cdecl]<IntPtr, uint>)Com.VtableOf(queue)[3])(queue);
            var points = ((delegate* unmanaged[Cdecl]<IntPtr, int>)Com.VtableOf(queue)[4])(queue);
            for (var p = 0; p < points; p++)
            {
                int offset;
                double value;
                ((delegate* unmanaged[Cdecl]<IntPtr, int, int*, double*, int>)Com.VtableOf(queue)[5])(queue, p, &offset, &value);
                ApplyParameter(id, (float)value, offset, bridge);
            }
        }
    }

    private void ApplyParameter(uint id, float value, int offset, AudioBridge bridge)
    {
        if (id < GenericParams)
        {
            _params[id] = value;
            bridge.ParamIn.TryWrite(new ParamEvent((int)id, value));
            return;
        }
        var ctrl = (int)id - MidiParamBase;
        if (ctrl is < 0 or >= MidiParams) return;
        if (ctrl == 129) bridge.MidiIn.TryWrite(MidiEvent.PitchBend(0, Math.Clamp((int)(value * 16383f), 0, 16383), offset));
        else if (ctrl == 128) bridge.MidiIn.TryWrite(MidiEvent.Aftertouch(0, Math.Clamp((int)(value * 127f), 0, 127), offset));
        else bridge.MidiIn.TryWrite(MidiEvent.ControlChange(0, ctrl, Math.Clamp((int)MathF.Round(value * 127f), 0, 127), offset));
    }

    // Event: int32 busIndex @0, int32 sampleOffset @4, double ppqPosition @8, uint16 flags @16, uint16 type @18, union @24
    // NoteOn @24: int16 channel, int16 pitch, float tuning @28, float velocity @32, int32 length @36, int32 noteId @40
    // NoteOff @24: int16 channel, int16 pitch, float velocity @28, int32 noteId @32, float tuning @36
    // PolyPressure @24: int16 channel, int16 pitch, float pressure @28, int32 noteId @32
    private void ReadEvents(IntPtr list, AudioBridge bridge)
    {
        var count = ((delegate* unmanaged[Cdecl]<IntPtr, int>)Com.VtableOf(list)[3])(list);
        var buffer = stackalloc byte[128];
        for (var i = 0; i < count; i++)
        {
            new Span<byte>(buffer, 128).Clear();
            if (((delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int>)Com.VtableOf(list)[4])(list, i, buffer) != R.Ok) continue;
            var offset = *(int*)(buffer + 4);
            var type = *(ushort*)(buffer + 18);
            var channel = *(short*)(buffer + 24) & 0xF;
            var pitch = *(short*)(buffer + 26) & 0x7F;
            switch (type)
            {
                case 0:
                {
                    var velocity = Math.Clamp((int)MathF.Round(*(float*)(buffer + 32) * 127f), 1, 127);
                    bridge.MidiIn.TryWrite(MidiEvent.NoteOn(channel, pitch, velocity, offset));
                    break;
                }
                case 1:
                    bridge.MidiIn.TryWrite(MidiEvent.NoteOff(channel, pitch, Math.Clamp((int)(*(float*)(buffer + 28) * 127f), 0, 127), offset));
                    break;
                case 3:
                    bridge.MidiIn.TryWrite(new MidiEvent(offset, (byte)(0xA0 | channel), (byte)pitch,
                        (byte)Math.Clamp((int)(*(float*)(buffer + 28) * 127f), 0, 127)));
                    break;
            }
        }
    }

    private void WriteOutgoingMidi(IntPtr list, AudioBridge bridge)
    {
        var buffer = stackalloc byte[128];
        var written = 0;
        while (written < 128 && bridge.MidiOut.TryRead(out var m))
        {
            written++;
            new Span<byte>(buffer, 128).Clear();
            *(int*)(buffer + 4) = m.DeltaFrames;
            var kind = m.Status & 0xF0;
            var channel = (short)(m.Status & 0x0F);
            if (kind == 0x90 && m.Data2 > 0)
            {
                *(ushort*)(buffer + 18) = 0;
                *(short*)(buffer + 24) = channel;
                *(short*)(buffer + 26) = m.Data1;
                *(float*)(buffer + 32) = m.Data2 / 127f;
                *(int*)(buffer + 40) = -1;
            }
            else if (kind is 0x80 or 0x90)
            {
                *(ushort*)(buffer + 18) = 1;
                *(short*)(buffer + 24) = channel;
                *(short*)(buffer + 26) = m.Data1;
                *(float*)(buffer + 28) = m.Data2 / 127f;
                *(int*)(buffer + 32) = -1;
            }
            else if (kind == 0xB0)
            {
                *(ushort*)(buffer + 18) = 65535; // LegacyMIDICCOutEvent
                buffer[24] = m.Data1;
                buffer[25] = (byte)channel;
                buffer[26] = m.Data2;
            }
            else continue;
            ((delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Com.VtableOf(list)[5])(list, buffer);
        }
    }

    // ---------------------------------------------------------------- IEditController

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ControllerSetComponentState(IntPtr self, IntPtr stream) => R.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ControllerSetState(IntPtr self, IntPtr stream) => R.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ControllerGetState(IntPtr self, IntPtr stream) => R.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetParameterCount(IntPtr self) => GenericParams + MidiParams;

    // ParameterInfo: uint32 id @0, char16 title[128] @4, shortTitle[128] @260, units[128] @516,
    // int32 stepCount @772, double defaultNormalizedValue @776, int32 unitId @784, int32 flags @788
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetParameterInfo(IntPtr self, int index, byte* info)
    {
        if ((uint)index >= GenericParams + MidiParams) return R.InvalidArgument;
        new Span<byte>(info, 792).Clear();

        string title;
        uint id;
        int flags;
        double def = 0;
        if (index < GenericParams)
        {
            id = (uint)index;
            title = "P" + (index + 1);
            flags = 1; // kCanAutomate
        }
        else
        {
            var ctrl = index - GenericParams;
            id = (uint)(MidiParamBase + ctrl);
            title = ctrl == 129 ? "Pitch Bend" : ctrl == 128 ? "Aftertouch" : "MIDI CC " + ctrl;
            flags = 2 | 16; // read-only + hidden: exists only so hosts can map MIDI controllers
            if (ctrl == 129) def = 0.5;
        }
        *(uint*)info = id;
        Com.PutUtf16((char*)(info + 4), 128, title);
        Com.PutUtf16((char*)(info + 260), 128, title);
        *(int*)(info + 772) = 0;
        *(double*)(info + 776) = def;
        *(int*)(info + 784) = -1; // kRootUnitId
        *(int*)(info + 788) = flags;
        return R.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetParamStringByValue(IntPtr self, uint id, double value, char* text)
    {
        Com.PutUtf16(text, 128, value.ToString("0.00"));
        return R.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetParamValueByString(IntPtr self, uint id, char* text, double* value)
    {
        if (double.TryParse(new string(text), out var v))
        {
            *value = Math.Clamp(v, 0, 1);
            return R.Ok;
        }
        return R.False;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static double NormalizedToPlain(IntPtr self, uint id, double value) => value;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static double PlainToNormalized(IntPtr self, uint id, double value) => value;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static double GetParamNormalized(IntPtr self, uint id)
    {
        var c = Com.Owner<Vst3Component>(self);
        if (id < GenericParams) return c._params[id];
        return id == MidiParamBase + 129 ? 0.5 : 0.0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetParamNormalized(IntPtr self, uint id, double value)
    {
        var c = Com.Owner<Vst3Component>(self);
        if (id < GenericParams) c._params[id] = (float)value;
        return R.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetComponentHandler(IntPtr self, IntPtr handler)
    {
        var c = Com.Owner<Vst3Component>(self);
        if (handler != IntPtr.Zero) Com.AddRef(handler);
        if (c._handler != IntPtr.Zero) Com.Release(c._handler);
        c._handler = handler;
        return R.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr CreateViewThunk(IntPtr self, byte* name)
    {
        var text = Marshal.PtrToStringAnsi((IntPtr)name);
        return text == "editor" ? Com.Owner<Vst3Component>(self).CreateView() : IntPtr.Zero;
    }

    // ---------------------------------------------------------------- IMidiMapping

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetMidiControllerAssignment(IntPtr self, int busIndex, short channel, short controller, uint* id)
    {
        if (busIndex != 0 || controller is < 0 or >= MidiParams) return R.False;
        *id = (uint)(MidiParamBase + controller);
        return R.Ok;
    }
}

/// <summary>Reads and writes the host's IBStream (read=3, write=4 in the vtable).</summary>
internal static unsafe class Streams
{
    public static byte[] ReadAll(IntPtr stream)
    {
        if (stream == IntPtr.Zero) return [];
        using var ms = new MemoryStream();
        var buffer = new byte[65536];
        var read = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int*, int>)Com.VtableOf(stream)[3];
        fixed (byte* b = buffer)
        {
            while (true)
            {
                int got = 0;
                var result = read(stream, b, buffer.Length, &got);
                if (result != R.Ok || got <= 0) break;
                ms.Write(buffer, 0, got);
            }
        }
        return ms.ToArray();
    }

    public static void WriteAll(IntPtr stream, byte[] data)
    {
        if (stream == IntPtr.Zero || data.Length == 0) return;
        var write = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int*, int>)Com.VtableOf(stream)[4];
        fixed (byte* b = data)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                int wrote = 0;
                if (write(stream, b + offset, data.Length - offset, &wrote) != R.Ok || wrote <= 0) break;
                offset += wrote;
            }
        }
    }
}
