using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Renaissance.Core.Models;

namespace Renaissance.Host;

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

/// <summary>Minimal, dependency-free VST2 module loader (works for both x86 and x64 hosts).</summary>
internal sealed unsafe class Vst2Plugin : IDisposable
{
    private const int VstMagic = 0x56737450;

    // dispatcher opcodes
    private const int EffOpen = 0, EffClose = 1, EffSetProgram = 2, EffGetProgram = 3, EffSetProgramName = 4,
        EffGetProgramName = 5, EffGetParamLabel = 6, EffGetParamDisplay = 7, EffGetParamName = 8,
        EffSetSampleRate = 10, EffSetBlockSize = 11, EffMainsChanged = 12, EffEditGetRect = 13,
        EffEditOpen = 14, EffEditClose = 15, EffEditIdle = 19, EffGetChunk = 23, EffSetChunk = 24,
        EffProcessEvents = 25, EffCanBeAutomated = 26, EffGetProgramNameIndexed = 29,
        EffGetPlugCategory = 35, EffGetEffectName = 45, EffGetVendorString = 47, EffGetProductString = 48,
        EffGetVendorVersion = 49, EffCanDo = 51, EffGetVstVersion = 58, EffStartProcess = 71, EffStopProcess = 72;

    private const int FlagHasEditor = 1, FlagCanReplacing = 16, FlagProgramChunks = 32, FlagIsSynth = 256;

    public static Vst2Plugin? Current { get; private set; }
    public static event Action<int, float>? ParameterAutomated;
    public static event Action<int, int>? WindowSizeRequested;

    private static double s_sampleRate = 44100;
    private static int s_blockSize = 512;
    private static readonly double* s_timeInfo = AllocTimeInfo();

    private readonly IntPtr _module;
    private readonly AEffect* _effect;
    private readonly byte* _stringBuffer = (byte*)NativeMemory.AllocZeroed(512);

    private Vst2Plugin(IntPtr module, AEffect* effect)
    {
        _module = module;
        _effect = effect;
    }

    public int NumInputs => _effect->NumInputs;
    public int NumOutputs => _effect->NumOutputs;
    public int NumParams => _effect->NumParams;
    public int NumPrograms => _effect->NumPrograms;
    public bool CanReplacing => (_effect->Flags & FlagCanReplacing) != 0 && _effect->ProcessReplacing != IntPtr.Zero;
    public bool HasEditor => (_effect->Flags & FlagHasEditor) != 0;
    public bool ProgramChunks => (_effect->Flags & FlagProgramChunks) != 0;
    public string Path { get; private set; } = "";

    public static Vst2Plugin Load(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.SetCurrentDirectory(dir);

        var module = Native.LoadLibraryExW(path, IntPtr.Zero, Native.LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module == IntPtr.Zero)
            throw new InvalidOperationException("Could not load library: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);

        var entry = Native.GetProcAddress(module, "VSTPluginMain");
        if (entry == IntPtr.Zero) entry = Native.GetProcAddress(module, "main");
        if (entry == IntPtr.Zero) throw new InvalidOperationException("Module has no VSTPluginMain/main entry point.");

        var plugin = new Vst2Plugin(module, null) { Path = path };
        Current = plugin;
        var callback = (IntPtr)(delegate* unmanaged[Cdecl]<AEffect*, int, int, IntPtr, IntPtr, float, IntPtr>)&HostCallback;
        var effect = ((delegate* unmanaged[Cdecl]<IntPtr, AEffect*>)entry)(callback);
        if (effect == null || effect->Magic != VstMagic)
            throw new InvalidOperationException("Plugin did not return a valid VST2 effect.");

        var real = new Vst2Plugin(module, effect) { Path = path };
        Current = real;
        real.Dispatch(EffOpen);
        return real;
    }

    public IntPtr Dispatch(int opcode, int index = 0, IntPtr value = default, IntPtr ptr = default, float opt = 0f) =>
        ((delegate* unmanaged[Cdecl]<AEffect*, int, int, IntPtr, IntPtr, float, IntPtr>)_effect->Dispatcher)(
            _effect, opcode, index, value, ptr, opt);

    private string GetString(int opcode, int index = 0)
    {
        new Span<byte>(_stringBuffer, 512).Clear();
        Dispatch(opcode, index, IntPtr.Zero, (IntPtr)_stringBuffer);
        return Marshal.PtrToStringAnsi((IntPtr)_stringBuffer) ?? "";
    }

    private bool CanDo(string what)
    {
        var bytes = Encoding.ASCII.GetBytes(what + "\0");
        fixed (byte* p = bytes) return Dispatch(EffCanDo, 0, IntPtr.Zero, (IntPtr)p) == 1;
    }

    public PluginMetadata GetMetadata()
    {
        var vendorVersion = (int)Dispatch(EffGetVendorVersion);
        if (vendorVersion == 0) vendorVersion = _effect->Version;
        var name = GetString(EffGetEffectName);
        var product = GetString(EffGetProductString);
        if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(product) ? System.IO.Path.GetFileNameWithoutExtension(Path) : product;
        return new PluginMetadata
        {
            Name = name,
            Product = product,
            Vendor = GetString(EffGetVendorString),
            VendorVersion = vendorVersion,
            VstVersion = (int)Dispatch(EffGetVstVersion),
            UniqueId = _effect->UniqueId,
            NumParameters = _effect->NumParams,
            NumPrograms = _effect->NumPrograms,
            NumInputs = _effect->NumInputs,
            NumOutputs = _effect->NumOutputs,
            Flags = _effect->Flags,
            Category = (int)Dispatch(EffGetPlugCategory),
            HasEditor = HasEditor,
            IsSynth = (_effect->Flags & FlagIsSynth) != 0,
            ProgramChunks = ProgramChunks,
            CanReplacing = CanReplacing,
            ReceivesMidi = CanDo("receiveVstMidiEvent"),
            SendsMidi = CanDo("sendVstMidiEvent"),
        };
    }

    // ---------------------------------------------------------------- parameters

    public float GetParameter(int index) =>
        ((delegate* unmanaged[Cdecl]<AEffect*, int, float>)_effect->GetParameter)(_effect, index);

    public void SetParameter(int index, float value) =>
        ((delegate* unmanaged[Cdecl]<AEffect*, int, float, void>)_effect->SetParameter)(_effect, index, value);

    public string GetParameterDisplay(int index)
    {
        var d = GetString(EffGetParamDisplay, index);
        var l = GetString(EffGetParamLabel, index);
        return string.IsNullOrWhiteSpace(l) ? d : $"{d} {l}";
    }

    public List<ParameterInfo> GetParameters()
    {
        var list = new List<ParameterInfo>(_effect->NumParams);
        for (var i = 0; i < _effect->NumParams; i++)
        {
            list.Add(new ParameterInfo
            {
                Index = i,
                Name = GetString(EffGetParamName, i),
                Label = GetString(EffGetParamLabel, i),
                Display = GetString(EffGetParamDisplay, i),
                Value = GetParameter(i),
                Automatable = Dispatch(EffCanBeAutomated, i) != 0,
            });
        }
        return list;
    }

    public float[] GetParameterValues()
    {
        var values = new float[_effect->NumParams];
        for (var i = 0; i < values.Length; i++) values[i] = GetParameter(i);
        return values;
    }

    // ---------------------------------------------------------------- programs

    public int GetProgram() => (int)Dispatch(EffGetProgram);
    public void SetProgram(int index) => Dispatch(EffSetProgram, 0, (IntPtr)index);
    public string GetProgramName() => GetString(EffGetProgramName);

    public void SetProgramName(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name.Length > 23 ? name[..23] : name);
        var buf = stackalloc byte[32];
        new Span<byte>(buf, 32).Clear();
        bytes.CopyTo(new Span<byte>(buf, 32));
        Dispatch(EffSetProgramName, 0, IntPtr.Zero, (IntPtr)buf);
    }

    public List<string> GetProgramNames()
    {
        var names = new List<string>(_effect->NumPrograms);
        var original = GetProgram();
        for (var i = 0; i < _effect->NumPrograms; i++)
        {
            new Span<byte>(_stringBuffer, 512).Clear();
            if (Dispatch(EffGetProgramNameIndexed, i, IntPtr.Zero, (IntPtr)_stringBuffer) != 0)
            {
                names.Add(Marshal.PtrToStringAnsi((IntPtr)_stringBuffer) ?? "");
                continue;
            }
            SetProgram(i);
            names.Add(GetProgramName());
        }
        if (GetProgram() != original) SetProgram(original);
        return names;
    }

    // ---------------------------------------------------------------- chunks

    public byte[] GetChunk(bool program)
    {
        IntPtr data = IntPtr.Zero;
        var size = (int)Dispatch(EffGetChunk, program ? 1 : 0, IntPtr.Zero, (IntPtr)(&data));
        if (size <= 0 || data == IntPtr.Zero) return [];
        var result = new byte[size];
        Marshal.Copy(data, result, 0, size);
        return result;
    }

    public void SetChunk(bool program, byte[] chunk)
    {
        fixed (byte* p = chunk) Dispatch(EffSetChunk, program ? 1 : 0, (IntPtr)chunk.Length, (IntPtr)p);
    }

    // ---------------------------------------------------------------- editor

    public (int Width, int Height) GetEditorRect()
    {
        short* rect = null;
        Dispatch(EffEditGetRect, 0, IntPtr.Zero, (IntPtr)(&rect));
        if (rect == null) return (0, 0);
        return (rect[3] - rect[1], rect[2] - rect[0]);
    }

    public void OpenEditor(IntPtr hwnd) => Dispatch(EffEditOpen, 0, IntPtr.Zero, hwnd);
    public void CloseEditor() => Dispatch(EffEditClose);
    public void EditorIdle() => Dispatch(EffEditIdle);

    // ---------------------------------------------------------------- audio

    public void Configure(double sampleRate, int blockSize)
    {
        s_sampleRate = sampleRate;
        s_blockSize = blockSize;
        s_timeInfo[1] = sampleRate;
        Dispatch(EffSetSampleRate, 0, IntPtr.Zero, IntPtr.Zero, (float)sampleRate);
        Dispatch(EffSetBlockSize, 0, (IntPtr)blockSize);
    }

    public void Resume()
    {
        Dispatch(EffMainsChanged, 0, (IntPtr)1);
        Dispatch(EffStartProcess);
    }

    public void Suspend()
    {
        Dispatch(EffStopProcess);
        Dispatch(EffMainsChanged, 0, IntPtr.Zero);
    }

    public void ProcessReplacing(float** inputs, float** outputs, int frames)
    {
        ((delegate* unmanaged[Cdecl]<AEffect*, float**, float**, int, void>)_effect->ProcessReplacing)(_effect, inputs, outputs, frames);
        s_timeInfo[0] += frames;
    }

    public void ProcessEvents(void* events) => Dispatch(EffProcessEvents, 0, IntPtr.Zero, (IntPtr)events);

    public void Dispose()
    {
        try { Dispatch(EffClose); } catch { }
        NativeMemory.Free(_stringBuffer);
        if (ReferenceEquals(Current, this)) Current = null;
    }

    // ---------------------------------------------------------------- host callback

    private static double* AllocTimeInfo()
    {
        var p = (double*)NativeMemory.AllocZeroed(96);
        p[1] = 44100;          // sampleRate
        p[4] = 120;            // tempo
        ((int*)p)[16] = 4;     // timeSigNumerator
        ((int*)p)[17] = 4;     // timeSigDenominator
        ((int*)p)[21] = (1 << 10) | (1 << 13); // tempo + time signature valid
        return p;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr HostCallback(AEffect* effect, int opcode, int index, IntPtr value, IntPtr ptr, float opt)
    {
        try
        {
            switch (opcode)
            {
                case 0: ParameterAutomated?.Invoke(index, opt); return IntPtr.Zero;
                case 1: return (IntPtr)2400;
                case 2: return IntPtr.Zero;
                case 3: return IntPtr.Zero;
                case 6: return (IntPtr)1;
                case 7: return (IntPtr)s_timeInfo;
                case 8: AudioEngine.Current?.CaptureOutgoing((byte*)ptr); return (IntPtr)1;
                case 13: return (IntPtr)1;
                case 15: WindowSizeRequested?.Invoke(index, (int)value); return (IntPtr)1;
                case 16: return (IntPtr)(int)s_sampleRate;
                case 17: return (IntPtr)s_blockSize;
                case 23: return IntPtr.Zero;
                case 32:
                case 33: WriteString(ptr, "Renaissance"); return (IntPtr)1;
                case 34: return (IntPtr)1000;
                case 37: return CanDoHost(Marshal.PtrToStringAnsi(ptr) ?? "") ? (IntPtr)1 : (IntPtr)(-1);
                case 38: return (IntPtr)1;
                case 42: return (IntPtr)1;
                case 43:
                case 44: return (IntPtr)1;
                default: return IntPtr.Zero;
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static bool CanDoHost(string what) => what is
        "sendVstEvents" or "sendVstMidiEvent" or "receiveVstEvents" or "receiveVstMidiEvent"
        or "sendVstTimeInfo" or "sizeWindow" or "supplyIdle" or "startStopProcess";

    private static void WriteString(IntPtr dest, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text + "\0");
        Marshal.Copy(bytes, 0, dest, bytes.Length);
    }
}
