using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Renaissance.Core.Audio;
using Renaissance.Core.Embedding;

namespace Renaissance.Vst2;

[StructLayout(LayoutKind.Sequential)]
internal struct AEffect
{
    public int Magic;
    public IntPtr Dispatcher;
    public IntPtr Process;
    public IntPtr SetParameter;
    public IntPtr GetParameter;
    public int NumPrograms;
    public int NumParams;
    public int NumInputs;
    public int NumOutputs;
    public int Flags;
    public IntPtr Reserved1;
    public IntPtr Reserved2;
    public int InitialDelay;
    public int RealQualities;
    public int OffQualities;
    public float IoRatio;
    public IntPtr Object;
    public IntPtr User;
    public int UniqueId;
    public int Version;
    public IntPtr ProcessReplacing;
    public IntPtr ProcessDoubleReplacing;
}

/// <summary>
/// One Renaissance VST2 instance. Contains no business logic: audio, MIDI and automation are forwarded to the
/// shared-memory bridge, state and editor handling to <see cref="WrapperLink"/> (shared with the VST3 wrapper).
/// </summary>
internal sealed unsafe class Vst2Instance
{
    private const int ParamCount = 128;
    private const int MaxOutMidi = 128;
    private const int VstMagic = 0x56737450;

    private readonly AEffect* _effect;
    private readonly IntPtr _hostCallback;
    private readonly bool _isEffect;
    private readonly WrapperLink _link;
    private readonly float[] _params = new float[ParamCount];
    private readonly short* _rect = (short*)NativeMemory.AllocZeroed(8);
    private readonly byte* _text = (byte*)NativeMemory.AllocZeroed(256);
    private readonly byte* _outEvents;
    private readonly int _ptr = IntPtr.Size;
    private IntPtr _chunk;
    private double _sampleRate = 44100;
    private int _blockSize = 512;
    private int _started;
    private int _pendingW, _pendingH;
    private GCHandle _handle;

    private Vst2Instance(AEffect* effect, IntPtr hostCallback, bool isEffect)
    {
        _effect = effect;
        _hostCallback = hostCallback;
        _isEffect = isEffect;
        _link = new WrapperLink((w, h) =>
        {
            _pendingW = w;
            _pendingH = h;
        });
        _outEvents = (byte*)NativeMemory.AllocZeroed((nuint)(_ptr * 2 + MaxOutMidi * _ptr + 15 + MaxOutMidi * 32));
        _rect[0] = 0;
        _rect[1] = 0;
        SetRect(_link.EditorWidth, _link.EditorHeight);
    }

    public static IntPtr Create(IntPtr hostCallback)
    {
        var path = ModuleInfo.Path();
        var isEffect = path is not null && Path.GetFileNameWithoutExtension(path).EndsWith("FX", StringComparison.OrdinalIgnoreCase);
        if (path is not null) WrapperLink.WrapperDirectory = Path.GetDirectoryName(path);

        var effect = (AEffect*)NativeMemory.AllocZeroed((nuint)sizeof(AEffect));
        var instance = new Vst2Instance(effect, hostCallback, isEffect);
        instance._handle = GCHandle.Alloc(instance);

        effect->Magic = VstMagic;
        effect->Dispatcher = (IntPtr)(delegate* unmanaged[Cdecl]<AEffect*, int, int, IntPtr, IntPtr, float, IntPtr>)&Dispatch;
        effect->Process = (IntPtr)(delegate* unmanaged[Cdecl]<AEffect*, float**, float**, int, void>)&ProcessLegacy;
        effect->ProcessReplacing = (IntPtr)(delegate* unmanaged[Cdecl]<AEffect*, float**, float**, int, void>)&ProcessReplacing;
        effect->SetParameter = (IntPtr)(delegate* unmanaged[Cdecl]<AEffect*, int, float, void>)&SetParameter;
        effect->GetParameter = (IntPtr)(delegate* unmanaged[Cdecl]<AEffect*, int, float>)&GetParameter;
        effect->NumPrograms = 1;
        effect->NumParams = ParamCount;
        effect->NumInputs = 2;
        effect->NumOutputs = 2;
        effect->Flags = 1 /* editor */ | 16 /* replacing */ | 32 /* chunks */ | (isEffect ? 0 : 256 /* synth */);
        effect->UniqueId = isEffect ? 0x526E7346 : 0x526E7349; // 'RnsF' / 'RnsI'
        effect->Version = Renaissance.Core.AppVersion.Vst2Number;
        effect->Object = GCHandle.ToIntPtr(instance._handle);
        Log.Write($"Instance created ({(isEffect ? "effect" : "instrument")}) from {path}");
        return (IntPtr)effect;
    }

    private static Vst2Instance? From(AEffect* effect) =>
        effect == null || effect->Object == IntPtr.Zero ? null : GCHandle.FromIntPtr(effect->Object).Target as Vst2Instance;

    /// <summary>The app is only launched once the host actually uses the plugin, not while it is merely being scanned.</summary>
    private void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _link.Start();
        _link.Configure(_sampleRate, _blockSize);
    }

    private void SetRect(int w, int h)
    {
        _rect[2] = (short)h;
        _rect[3] = (short)w;
    }

    // ---------------------------------------------------------------- callbacks from the host

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr Dispatch(AEffect* effect, int opcode, int index, IntPtr value, IntPtr ptr, float opt)
    {
        try
        {
            var self = From(effect);
            return self is null ? IntPtr.Zero : self.Handle(opcode, index, value, ptr, opt);
        }
        catch (Exception ex)
        {
            Log.Write($"Dispatch({opcode}) failed: {ex}");
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ProcessReplacing(AEffect* effect, float** inputs, float** outputs, int frames)
    {
        try { From(effect)?.Process(inputs, outputs, frames); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ProcessLegacy(AEffect* effect, float** inputs, float** outputs, int frames)
    {
        try { From(effect)?.Process(inputs, outputs, frames); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetParameter(AEffect* effect, int index, float value)
    {
        try { From(effect)?.SetParam(index, value); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static float GetParameter(AEffect* effect, int index)
    {
        try { return From(effect)?.GetParam(index) ?? 0f; }
        catch { return 0f; }
    }

    private IntPtr Handle(int opcode, int index, IntPtr value, IntPtr ptr, float opt)
    {
        switch (opcode)
        {
            case 1: Close(); return IntPtr.Zero;                                  // effClose
            case 3: return IntPtr.Zero;                                           // effGetProgram
            case 5: WriteString(ptr, "Renaissance"); return IntPtr.Zero;         // effGetProgramName
            case 6: WriteString(ptr, ""); return IntPtr.Zero;                    // effGetParamLabel
            case 7: WriteString(ptr, GetParam(index).ToString("0.00")); return IntPtr.Zero; // effGetParamDisplay
            case 8: WriteString(ptr, "P" + (index + 1)); return IntPtr.Zero;     // effGetParamName
            case 10:                                                              // effSetSampleRate
                _sampleRate = opt;
                EnsureStarted();
                _link.Configure(_sampleRate, _blockSize);
                return IntPtr.Zero;
            case 11:                                                              // effSetBlockSize
                _blockSize = (int)value;
                if (_started != 0) _link.Configure(_sampleRate, _blockSize);
                return IntPtr.Zero;
            case 12:                                                              // effMainsChanged
                if (value != IntPtr.Zero) EnsureStarted();
                return IntPtr.Zero;
            case 13:                                                              // effEditGetRect
                SetRect(_link.EditorWidth, _link.EditorHeight);
                *(IntPtr*)ptr = (IntPtr)_rect;
                return (IntPtr)1;
            case 14:                                                              // effEditOpen
                EnsureStarted();
                _link.AttachEditor(ptr);
                return (IntPtr)1;
            case 15: _link.DetachEditor(); return IntPtr.Zero;                    // effEditClose
            case 19: ApplyPendingResize(); return IntPtr.Zero;                   // effEditIdle
            case 23: return GetChunk(ptr);                                        // effGetChunk
            case 24: SetChunk(ptr, (int)value); return (IntPtr)1;               // effSetChunk
            case 25: ProcessEvents((byte*)ptr); return (IntPtr)1;               // effProcessEvents
            case 26: return (IntPtr)1;                                            // effCanBeAutomated
            case 29: WriteString(ptr, "Renaissance"); return (IntPtr)1;          // effGetProgramNameIndexed
            case 35: return (IntPtr)(_isEffect ? 1 : 2);                         // effGetPlugCategory
            case 45: WriteString(ptr, _isEffect ? "Renaissance FX" : "Renaissance"); return (IntPtr)1;
            case 47: WriteString(ptr, "Renaissance"); return (IntPtr)1;          // vendor
            case 48: WriteString(ptr, _isEffect ? "Renaissance FX" : "Renaissance"); return (IntPtr)1;
            case 49: return (IntPtr)Renaissance.Core.AppVersion.Vst2Number;      // vendor version
            case 51: return CanDo(Marshal.PtrToStringAnsi(ptr) ?? "") ? (IntPtr)1 : (IntPtr)(-1);
            case 58: return (IntPtr)2400;                                         // effGetVstVersion
            case 71: EnsureStarted(); return IntPtr.Zero;                         // effStartProcess
            default: return IntPtr.Zero;
        }
    }

    private static bool CanDo(string what) => what is
        "receiveVstEvents" or "receiveVstMidiEvent" or "sendVstEvents" or "sendVstMidiEvent" or "sizeWindow" or "bypass";

    private void WriteString(IntPtr dest, string text)
    {
        if (dest == IntPtr.Zero) return;
        var bytes = Encoding.ASCII.GetBytes(text.Length > 60 ? text[..60] : text);
        var d = (byte*)dest;
        for (var i = 0; i < bytes.Length; i++) d[i] = bytes[i];
        d[bytes.Length] = 0;
    }

    // ---------------------------------------------------------------- parameters

    private void SetParam(int index, float value)
    {
        if ((uint)index >= ParamCount) return;
        _params[index] = value;
        _link.Bridge?.ParamIn.TryWrite(new ParamEvent(index, value));
    }

    private float GetParam(int index) => (uint)index < ParamCount ? _params[index] : 0f;

    // ---------------------------------------------------------------- audio / midi (audio thread)

    private void Process(float** inputs, float** outputs, int frames)
    {
        var bridge = _link.Bridge;
        if (bridge is null)
        {
            for (var ch = 0; ch < 2; ch++)
            {
                var o = new Span<float>(outputs[ch], frames);
                if (_isEffect && inputs != null) new ReadOnlySpan<float>(inputs[ch], frames).CopyTo(o);
                else o.Clear();
            }
            return;
        }

        bridge.ProcessNative(inputs, 2, outputs, 2, frames);
        SendOutgoingMidi(bridge);
    }

    private void ProcessEvents(byte* events)
    {
        var bridge = _link.Bridge;
        if (bridge is null || events == null) return;
        var count = *(int*)events;
        var list = (IntPtr*)(events + _ptr * 2);
        for (var i = 0; i < count; i++)
        {
            var e = (byte*)list[i];
            if (e == null || *(int*)e != 1) continue; // kVstMidiType only
            bridge.MidiIn.TryWrite(new MidiEvent(((int*)e)[2], e[24], e[25], e[26]));
        }
    }

    private void SendOutgoingMidi(AudioBridge bridge)
    {
        var n = 0;
        var pointers = (IntPtr*)(_outEvents + _ptr * 2);
        var storage = _outEvents + ((_ptr * 2 + MaxOutMidi * _ptr + 15) & ~15);
        while (n < MaxOutMidi && bridge.MidiOut.TryRead(out var m))
        {
            var e = storage + n * 32;
            ((int*)e)[0] = 1;
            ((int*)e)[1] = 24;
            ((int*)e)[2] = m.DeltaFrames;
            ((int*)e)[3] = 1;
            ((int*)e)[4] = 0;
            ((int*)e)[5] = 0;
            e[24] = m.Status;
            e[25] = m.Data1;
            e[26] = m.Data2;
            e[27] = 0;
            pointers[n++] = (IntPtr)e;
        }
        if (n == 0 || _hostCallback == IntPtr.Zero) return;
        *(int*)_outEvents = n;
        ((delegate* unmanaged[Cdecl]<AEffect*, int, int, IntPtr, IntPtr, float, IntPtr>)_hostCallback)(_effect, 8, 0, IntPtr.Zero, (IntPtr)_outEvents, 0f);
    }

    // ---------------------------------------------------------------- state / editor

    private IntPtr GetChunk(IntPtr ptr)
    {
        var data = _started != 0 ? _link.GetState() : [];
        if (_chunk != IntPtr.Zero) NativeMemory.Free((void*)_chunk);
        _chunk = IntPtr.Zero;
        if (data.Length == 0) return IntPtr.Zero;
        _chunk = (IntPtr)NativeMemory.Alloc((nuint)data.Length);
        Marshal.Copy(data, 0, _chunk, data.Length);
        *(IntPtr*)ptr = _chunk;
        return (IntPtr)data.Length;
    }

    private void SetChunk(IntPtr ptr, int size)
    {
        if (size <= 0 || ptr == IntPtr.Zero) return;
        var data = new byte[size];
        Marshal.Copy(ptr, data, 0, size);
        EnsureStarted();
        _link.SetState(data);
    }

    /// <summary>effEditIdle runs on the DAW's GUI thread, the only thread that may call sizeWindow.</summary>
    private void ApplyPendingResize()
    {
        var w = _pendingW;
        var h = _pendingH;
        if (w <= 0 || h <= 0) return;
        _pendingW = _pendingH = 0;
        SetRect(w, h);
        if (_hostCallback != IntPtr.Zero)
            ((delegate* unmanaged[Cdecl]<AEffect*, int, int, IntPtr, IntPtr, float, IntPtr>)_hostCallback)(_effect, 15, w, (IntPtr)h, IntPtr.Zero, 0f);
    }

    private void Close()
    {
        Log.Write("Instance closing");
        _effect->Object = IntPtr.Zero;
        _link.Dispose();
        if (_chunk != IntPtr.Zero) NativeMemory.Free((void*)_chunk);
        _chunk = IntPtr.Zero;
        // The effect block is intentionally leaked for a moment longer than the host's last use; free the rest.
        NativeMemory.Free(_rect);
        NativeMemory.Free(_text);
        NativeMemory.Free(_outEvents);
        if (_handle.IsAllocated) _handle.Free();
    }
}
