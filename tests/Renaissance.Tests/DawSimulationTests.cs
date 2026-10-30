using System.Diagnostics;
using System.Runtime.InteropServices;
using Renaissance.Core.Models;
using Xunit;

namespace Renaissance.Tests;

/// <summary>
/// Acts as a VST2 DAW: loads the real NativeAOT Renaissance.dll, drives it through the AEffect interface and checks
/// that audio comes back from the real 32-bit B4 II plugin via app → Host32. Requires build-vst2.ps1 to have run
/// and B4 II to be installed; otherwise each test is a no-op.
/// </summary>
[Collection("RealPlugins")]
public sealed unsafe class DawSimulationTests
{
    private const string B4 = @"C:\Program Files (x86)\Native Instruments\B4 II\VSTPlugins\B4 II.dll";

    [StructLayout(LayoutKind.Sequential)]
    private struct AEffect
    {
        public int Magic; public IntPtr Dispatcher, Process, SetParameter, GetParameter;
        public int NumPrograms, NumParams, NumInputs, NumOutputs, Flags;
        public IntPtr Reserved1, Reserved2;
        public int InitialDelay, RealQualities, OffQualities; public float IoRatio;
        public IntPtr Object, User; public int UniqueId, Version;
        public IntPtr ProcessReplacing, ProcessDoubleReplacing;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static IntPtr HostCallback(AEffect* e, int opcode, int index, IntPtr value, IntPtr ptr, float opt) =>
        opcode == 1 ? (IntPtr)2400 : IntPtr.Zero;

    /// <summary>Only count processes this test started, so a Renaissance already open in a real DAW is ignored.</summary>
    internal static HashSet<int> ExistingProcessIds() =>
        Process.GetProcessesByName("Renaissance").Concat(Process.GetProcessesByName("Renaissance.Host32")).Concat(Process.GetProcessesByName("Renaissance.Host64")).Select(p => p.Id).ToHashSet();

    internal static int StartedSince(HashSet<int> existing) =>
        ExistingProcessIds().Count(id => !existing.Contains(id));

    private static string? FindDll(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "Renaissance-VST2", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed class Daw : IDisposable
    {
        public AEffect* Effect;
        public IntPtr Module;

        public IntPtr Dispatch(int op, int index = 0, IntPtr value = default, IntPtr ptr = default, float opt = 0) =>
            ((delegate* unmanaged[Cdecl]<AEffect*, int, int, IntPtr, IntPtr, float, IntPtr>)Effect->Dispatcher)(Effect, op, index, value, ptr, opt);

        public static Daw Load(string path)
        {
            var module = NativeLibrary.Load(path);
            var main = NativeLibrary.GetExport(module, "VSTPluginMain");
            var cb = (IntPtr)(delegate* unmanaged[Cdecl]<AEffect*, int, int, IntPtr, IntPtr, float, IntPtr>)&HostCallback;
            var effect = ((delegate* unmanaged[Cdecl]<IntPtr, AEffect*>)main)(cb);
            return new Daw { Effect = effect, Module = module };
        }

        public void Dispose() { }
    }

    [Fact]
    public void Instrument_dll_presents_as_a_valid_vst2_synth()
    {
        var path = FindDll("Renaissance.dll");
        if (path is null) return;

        var daw = Daw.Load(path);
        Assert.Equal(0x56737450, daw.Effect->Magic);
        Assert.Equal(2, daw.Effect->NumOutputs);
        Assert.True((daw.Effect->Flags & 256) != 0, "instrument must be flagged as a synth");
        Assert.True((daw.Effect->Flags & 1) != 0, "must have an editor");
        Assert.True((daw.Effect->Flags & 16) != 0, "must support processReplacing");
        Assert.Equal((IntPtr)2400, daw.Dispatch(58));

        var name = stackalloc byte[64];
        daw.Dispatch(45, 0, 0, (IntPtr)name);
        Assert.Equal("Renaissance", Marshal.PtrToStringAnsi((IntPtr)name));

        daw.Dispatch(1); // effClose without ever starting the app (scan-style use)
    }

    [Fact]
    public void Fx_dll_presents_as_an_effect()
    {
        var path = FindDll("Renaissance FX.dll");
        if (path is null) return;

        var daw = Daw.Load(path);
        Assert.True((daw.Effect->Flags & 256) == 0, "FX version must not be a synth");
        Assert.Equal((IntPtr)1, daw.Dispatch(35));
        daw.Dispatch(1);
    }

    [Fact]
    public void Scanning_style_open_and_close_does_not_launch_the_app()
    {
        var path = FindDll("Renaissance.dll");
        if (path is null) return;
        var before = Process.GetProcessesByName("Renaissance").Length;

        var daw = Daw.Load(path);
        daw.Dispatch(0);
        daw.Dispatch(1);

        Assert.Equal(before, Process.GetProcessesByName("Renaissance").Length);
    }

    [Fact]
    public void Full_chain_state_load_midi_in_audio_out_and_state_save()
    {
        var path = FindDll("Renaissance.dll");
        if (path is null || !File.Exists(B4)) return;
        var existing = ExistingProcessIds();

        var daw = Daw.Load(path);
        daw.Dispatch(0);
        daw.Dispatch(10, 0, 0, 0, 44100f);
        daw.Dispatch(11, 0, 512);
        daw.Dispatch(12, 0, 1);

        // DAW project load: tell Renaissance which plugin to host.
        var state = new PluginState { PluginPath = B4, Architecture = PluginArchitecture.X86, PluginName = "B4 II" }.ToBytes();
        fixed (byte* s = state) daw.Dispatch(24, 0, state.Length, (IntPtr)s);

        const int frames = 512;
        var buffers = (float*)NativeMemory.AllocZeroed(4 * frames * sizeof(float));
        var ins = stackalloc float*[2] { buffers, buffers + frames };
        var outs = stackalloc float*[2] { buffers + 2 * frames, buffers + 3 * frames };

        var eventsMem = (byte*)NativeMemory.AllocZeroed(128);
        var ev = eventsMem + 32;
        ((int*)ev)[0] = 1; ((int*)ev)[1] = 24; ev[24] = 0x90; ev[25] = 60; ev[26] = 100;
        *(int*)eventsMem = 1;
        *(IntPtr*)(eventsMem + 16) = (IntPtr)ev;

        var process = (delegate* unmanaged[Cdecl]<AEffect*, float**, float**, int, void>)daw.Effect->ProcessReplacing;
        var peak = 0f;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(120) && peak < 0.0005f)
        {
            daw.Dispatch(25, 0, 0, (IntPtr)eventsMem);
            process(daw.Effect, ins, outs, frames);
            for (var i = 0; i < frames; i++) peak = Math.Max(peak, Math.Abs(outs[0][i]));
            Thread.Sleep(15);
        }
        var loadedAfter = sw.Elapsed;

        // DAW project save: Renaissance must hand back a state naming the hosted plugin.
        IntPtr chunk = IntPtr.Zero;
        var size = (int)daw.Dispatch(23, 0, 0, (IntPtr)(&chunk));
        var saved = size > 0 ? PluginState.FromBytes(new ReadOnlySpan<byte>((void*)chunk, size)) : null;

        daw.Dispatch(15);
        daw.Dispatch(1);
        Thread.Sleep(3000);
        var leftover = StartedSince(existing);

        Assert.True(peak > 0.0005f, $"expected audio from B4 II, peak={peak} after {loadedAfter.TotalSeconds:0}s");
        Assert.NotNull(saved);
        Assert.Equal(B4, saved!.PluginPath, ignoreCase: true);
        Assert.Equal(0, leftover);
    }
}
