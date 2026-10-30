using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Renaissance.Core.Models;
using Xunit;

namespace Renaissance.Tests;

/// <summary>
/// Acts as a VST3 host: loads the real NativeAOT Renaissance.vst3, enumerates its classes through IPluginFactory3,
/// creates the instrument, drives IComponent / IAudioProcessor / IEditController through their COM vtables, sends a
/// note via IEventList and expects audio from the real 32-bit B4 II. Requires build-vst2.ps1 and B4 II.
/// </summary>
[Collection("RealPlugins")]
public sealed unsafe class Vst3SimulationTests
{
    private const string B4 = @"C:\Program Files (x86)\Native Instruments\B4 II\VSTPlugins\B4 II.dll";

    private static byte[] Tuid(uint l1, uint l2, uint l3, uint l4) =>
    [
        (byte)l1, (byte)(l1 >> 8), (byte)(l1 >> 16), (byte)(l1 >> 24),
        (byte)(l2 >> 16), (byte)(l2 >> 24), (byte)l2, (byte)(l2 >> 8),
        (byte)(l3 >> 24), (byte)(l3 >> 16), (byte)(l3 >> 8), (byte)l3,
        (byte)(l4 >> 24), (byte)(l4 >> 16), (byte)(l4 >> 8), (byte)l4,
    ];

    private static readonly byte[] IComponent = Tuid(0xE831FF31, 0xF2D54301, 0x928EBBEE, 0x25697802);
    private static readonly byte[] IAudioProcessor = Tuid(0x42043F99, 0xB7DA453C, 0xA569E79D, 0x9AAEC33D);
    private static readonly byte[] IEditController = Tuid(0xDCD7BBE3, 0x7742448D, 0xA874AACC, 0x979C759E);
    private static readonly byte[] IMidiMapping = Tuid(0xDF0FF9F7, 0x49B74669, 0xB63AB732, 0x7ADBF5E5);
    private static readonly byte[] IPlugView = Tuid(0x5BC32507, 0xD06049EA, 0xA6151B52, 0x2B755B29);
    private static readonly byte[] IPluginFactory3 = Tuid(0x4555A2AB, 0xC1234E57, 0x9B122910, 0x36878931);

    private static string? FindModule()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "Renaissance-VST2", "Renaissance.vst3", "Contents", "x86_64-win", "Renaissance.vst3");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void** Vt(IntPtr obj) => *(void***)obj;

    private static IntPtr Query(IntPtr obj, byte[] iid)
    {
        IntPtr result = IntPtr.Zero;
        fixed (byte* i = iid)
            ((delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)Vt(obj)[0])(obj, i, &result);
        return result;
    }

    // ------------------------------------------------------------ host-side COM objects (event list, stream)

    private static byte[]? s_streamData;
    private static int s_streamPos;
    private static readonly List<byte> s_written = [];
    private static int s_pendingNote;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int HQI(IntPtr s, byte* i, IntPtr* o) { *o = s; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static uint HAddRef(IntPtr s) => 2;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static uint HRelease(IntPtr s) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int EventCount(IntPtr s) => s_pendingNote;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetEvent(IntPtr s, int index, byte* e)
    {
        new Span<byte>(e, 128).Clear();
        *(int*)(e + 4) = 0;                 // sampleOffset
        *(ushort*)(e + 18) = 0;             // kNoteOnEvent
        *(short*)(e + 24) = 0;              // channel
        *(short*)(e + 26) = 60;             // pitch
        *(float*)(e + 32) = 0.8f;           // velocity
        *(int*)(e + 40) = -1;               // noteId
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int AddEvent(IntPtr s, byte* e) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int StreamRead(IntPtr s, byte* buffer, int num, int* read)
    {
        var n = Math.Max(0, Math.Min(num, (s_streamData?.Length ?? 0) - s_streamPos));
        for (var i = 0; i < n; i++) buffer[i] = s_streamData![s_streamPos + i];
        s_streamPos += n;
        if (read != null) *read = n;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int StreamWrite(IntPtr s, byte* buffer, int num, int* wrote)
    {
        for (var i = 0; i < num; i++) s_written.Add(buffer[i]);
        if (wrote != null) *wrote = num;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int StreamSeek(IntPtr s, long pos, int mode, long* result) => 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int StreamTell(IntPtr s, long* pos) => 0;

    private static IntPtr NewObject(params IntPtr[] methods)
    {
        var vt = (void**)NativeMemory.Alloc((nuint)(methods.Length * sizeof(void*)));
        for (var i = 0; i < methods.Length; i++) vt[i] = (void*)methods[i];
        var obj = (IntPtr*)NativeMemory.AllocZeroed((nuint)sizeof(IntPtr));
        obj[0] = (IntPtr)vt;
        return (IntPtr)obj;
    }

    private static IntPtr NewEventList() => NewObject(
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)&HQI,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&HAddRef,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&HRelease,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&EventCount,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int>)&GetEvent,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)&AddEvent);

    private static IntPtr NewStream() => NewObject(
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)&HQI,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&HAddRef,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&HRelease,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int*, int>)&StreamRead,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int*, int>)&StreamWrite,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, long, int, long*, int>)&StreamSeek,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, long*, int>)&StreamTell);

    // ------------------------------------------------------------ tests

    private static IntPtr LoadFactory(string module)
    {
        var lib = NativeLibrary.Load(module);
        var init = NativeLibrary.GetExport(lib, "InitDll");
        Assert.Equal(1, ((delegate* unmanaged[Cdecl]<byte>)init)());
        var get = NativeLibrary.GetExport(lib, "GetPluginFactory");
        return ((delegate* unmanaged[Cdecl]<IntPtr>)get)();
    }

    [Fact]
    public void Factory_lists_instrument_and_effect_classes()
    {
        var module = FindModule();
        if (module is null) return;

        var factory = LoadFactory(module);
        Assert.NotEqual(IntPtr.Zero, factory);
        Assert.NotEqual(IntPtr.Zero, Query(factory, IPluginFactory3));
        Assert.Equal(2, ((delegate* unmanaged[Cdecl]<IntPtr, int>)Vt(factory)[4])(factory));

        var info = stackalloc byte[452];
        Assert.Equal(0, ((delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Vt(factory)[3])(factory, info));
        Assert.Equal("Renaissance", Marshal.PtrToStringAnsi((IntPtr)info));

        var classInfo = stackalloc byte[440];
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(0, ((delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int>)Vt(factory)[7])(factory, i, classInfo));
            Assert.Equal("Audio Module Class", Marshal.PtrToStringAnsi((IntPtr)(classInfo + 20)));
            var name = Marshal.PtrToStringAnsi((IntPtr)(classInfo + 52));
            var sub = Marshal.PtrToStringAnsi((IntPtr)(classInfo + 120));
            Assert.Equal(i == 0 ? ("Renaissance", "Instrument|Synth") : ("Renaissance FX", "Fx"), (name, sub));
        }
    }

    [Fact]
    public void Component_exposes_buses_parameters_and_midi_mapping()
    {
        var module = FindModule();
        if (module is null) return;

        var factory = LoadFactory(module);
        var classInfo = stackalloc byte[440];
        ((delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int>)Vt(factory)[7])(factory, 0, classInfo);

        IntPtr component = IntPtr.Zero;
        fixed (byte* iid = IComponent)
            Assert.Equal(0, ((delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, IntPtr*, int>)Vt(factory)[6])(factory, classInfo, iid, &component));
        Assert.NotEqual(IntPtr.Zero, component);

        Assert.Equal(0, ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)Vt(component)[3])(component, IntPtr.Zero)); // initialize
        var getBusCount = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int>)Vt(component)[7];
        Assert.Equal(0, getBusCount(component, 0, 0)); // instrument: no audio input
        Assert.Equal(1, getBusCount(component, 0, 1)); // one audio output
        Assert.Equal(1, getBusCount(component, 1, 0)); // one event input

        var bus = stackalloc byte[276];
        Assert.Equal(0, ((delegate* unmanaged[Cdecl]<IntPtr, int, int, int, byte*, int>)Vt(component)[8])(component, 0, 1, 0, bus));
        Assert.Equal(2, *(int*)(bus + 8));
        Assert.Equal("Stereo Out", new string((char*)(bus + 12)));

        var processor = Query(component, IAudioProcessor);
        var controller = Query(component, IEditController);
        var midi = Query(component, IMidiMapping);
        Assert.NotEqual(IntPtr.Zero, processor);
        Assert.NotEqual(IntPtr.Zero, controller);
        Assert.NotEqual(IntPtr.Zero, midi);

        var stereo = 3UL;
        var outs = &stereo;
        Assert.Equal(0, ((delegate* unmanaged[Cdecl]<IntPtr, ulong*, int, ulong*, int, int>)Vt(processor)[3])(processor, null, 0, outs, 1));
        var mono = 1UL << 19;
        Assert.NotEqual(0, ((delegate* unmanaged[Cdecl]<IntPtr, ulong*, int, ulong*, int, int>)Vt(processor)[3])(processor, null, 0, &mono, 1));

        var count = ((delegate* unmanaged[Cdecl]<IntPtr, int>)Vt(controller)[8])(controller);
        Assert.Equal(128 + 130, count);
        var pinfo = stackalloc byte[792];
        Assert.Equal(0, ((delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int>)Vt(controller)[9])(controller, 0, pinfo));
        Assert.Equal(0u, *(uint*)pinfo);
        Assert.Equal("P1", new string((char*)(pinfo + 4)));

        uint id = 0;
        Assert.Equal(0, ((delegate* unmanaged[Cdecl]<IntPtr, int, short, short, uint*, int>)Vt(midi)[3])(midi, 0, 0, 129, &id));
        Assert.Equal(1129u, id);

        IntPtr view = ((delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)Vt(controller)[17])(controller, (byte*)Marshal.StringToHGlobalAnsi("editor"));
        Assert.NotEqual(IntPtr.Zero, view);
        Assert.Equal(0, ((delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Vt(view)[3])(view, (byte*)Marshal.StringToHGlobalAnsi("HWND")));
        var rect = stackalloc int[4];
        Assert.Equal(0, ((delegate* unmanaged[Cdecl]<IntPtr, int*, int>)Vt(view)[9])(view, rect));
        Assert.True(rect[2] > 0 && rect[3] > 0);
    }

    [Fact]
    public void Full_chain_vst3_state_load_midi_in_audio_out_and_state_save()
    {
        var module = FindModule();
        if (module is null || !File.Exists(B4)) return;
        var existing = DawSimulationTests.ExistingProcessIds();

        var factory = LoadFactory(module);
        var classInfo = stackalloc byte[440];
        ((delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int>)Vt(factory)[7])(factory, 0, classInfo);
        IntPtr component = IntPtr.Zero;
        fixed (byte* iid = IComponent)
            ((delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, IntPtr*, int>)Vt(factory)[6])(factory, classInfo, iid, &component);
        var processor = Query(component, IAudioProcessor);
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)Vt(component)[3])(component, IntPtr.Zero);

        var setup = stackalloc byte[24];
        *(int*)(setup + 8) = 512;
        *(double*)(setup + 16) = 44100;
        Assert.Equal(0, ((delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Vt(processor)[7])(processor, setup));

        // Project load: state naming the plugin to host.
        s_streamData = new PluginState { PluginPath = B4, Architecture = PluginArchitecture.X86, PluginName = "B4 II" }.ToBytes();
        s_streamPos = 0;
        var stream = NewStream();
        Assert.Equal(0, ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)Vt(component)[12])(component, stream));
        ((delegate* unmanaged[Cdecl]<IntPtr, byte, int>)Vt(component)[11])(component, 1);     // setActive
        ((delegate* unmanaged[Cdecl]<IntPtr, byte, int>)Vt(processor)[8])(processor, 1);      // setProcessing

        const int frames = 512;
        var channels = (float*)NativeMemory.AllocZeroed(2 * frames * sizeof(float));
        var chPtrs = stackalloc float*[2] { channels, channels + frames };
        var outBus = stackalloc byte[24];
        new Span<byte>(outBus, 24).Clear();
        *(int*)outBus = 2;
        *(float***)(outBus + 16) = chPtrs;

        var events = NewEventList();
        var data = stackalloc byte[80];
        new Span<byte>(data, 80).Clear();
        *(int*)(data + 8) = frames;
        *(int*)(data + 16) = 1;
        *(byte**)(data + 32) = outBus;
        *(IntPtr*)(data + 56) = events;

        var process = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Vt(processor)[9];
        var peak = 0f;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(120) && peak < 0.0005f)
        {
            s_pendingNote = 1;
            Assert.Equal(0, process(processor, data));
            for (var i = 0; i < frames; i++) peak = Math.Max(peak, Math.Abs(chPtrs[0][i]));
            Thread.Sleep(15);
        }

        // Project save.
        s_written.Clear();
        Assert.Equal(0, ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)Vt(component)[13])(component, stream));
        var saved = s_written.Count > 0 ? PluginState.FromBytes(s_written.ToArray()) : null;

        ((delegate* unmanaged[Cdecl]<IntPtr, uint>)Vt(processor)[2])(processor); // release every interface we hold
        ((delegate* unmanaged[Cdecl]<IntPtr, uint>)Vt(component)[2])(component);
        Thread.Sleep(3000);
        var leftover = DawSimulationTests.StartedSince(existing);

        Assert.True(peak > 0.0005f, $"expected audio from B4 II over VST3, peak={peak} after {sw.Elapsed.TotalSeconds:0}s");
        Assert.NotNull(saved);
        Assert.Equal(B4, saved!.PluginPath, ignoreCase: true);
        Assert.Equal(0, leftover);
    }
}
