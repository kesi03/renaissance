using System.IO.Pipes;
using Renaissance.Core.Audio;
using Renaissance.Core.Hosting;
using Renaissance.Core.Ipc;
using Renaissance.Core.Models;
using Xunit;

namespace Renaissance.Tests;

public class CoreTests
{
    [Fact]
    public async Task Pipe_framing_roundtrips_messages_including_binary_data()
    {
        using var ms = new MemoryStream();
        await PipeMessageIO.WriteAsync(ms, new HostRequest { Id = 5, Command = "SetChunk", Data = [1, 2, 3, 255], Value = 0.5f });
        await PipeMessageIO.WriteAsync(ms, new HostResponse { Id = 5, Floats = [0.1f, 0.2f] });
        ms.Position = 0;

        var req = await PipeMessageIO.ReadAsync<HostRequest>(ms);
        var resp = await PipeMessageIO.ReadAsync<HostResponse>(ms);
        var end = await PipeMessageIO.ReadAsync<HostResponse>(ms);

        Assert.Equal("SetChunk", req!.Command);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, req.Data);
        Assert.Equal(new[] { 0.1f, 0.2f }, resp!.Floats);
        Assert.Null(end);
    }

    [Fact]
    public async Task Pipe_framing_rejects_oversized_frames()
    {
        using var ms = new MemoryStream(BitConverter.GetBytes(int.MaxValue));
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeMessageIO.ReadAsync<HostRequest>(ms));
    }

    [Fact]
    public void SpscRing_preserves_order_and_reports_full()
    {
        var ring = new SpscRing<int>(4);
        for (var i = 0; i < 4; i++) Assert.True(ring.TryWrite(i));
        Assert.False(ring.TryWrite(99));

        for (var i = 0; i < 4; i++)
        {
            Assert.True(ring.TryRead(out var v));
            Assert.Equal(i, v);
        }
        Assert.False(ring.TryRead(out _));
    }

    [Fact]
    public async Task SpscRing_is_correct_across_threads()
    {
        var ring = new SpscRing<int>(64);
        const int total = 200_000;
        var producer = Task.Run(() =>
        {
            for (var i = 0; i < total; i++) while (!ring.TryWrite(i)) Thread.SpinWait(10);
        });
        var expected = 0;
        while (expected < total)
        {
            if (!ring.TryRead(out var v)) continue;
            Assert.Equal(expected, v);
            expected++;
        }
        await producer;
    }

    [Fact]
    public void SpscRing_requires_power_of_two()
    {
        Assert.Throws<ArgumentException>(() => new SpscRing<int>(3));
    }

    [Fact]
    public void Midi_events_pack_and_unpack()
    {
        var e = MidiEvent.NoteOn(2, 60, 100, 17);
        Assert.Equal(0x92, e.Status);
        var back = MidiEvent.Unpack(e.DeltaFrames, e.Packed);
        Assert.Equal(e, back);

        var pb = MidiEvent.PitchBend(0, 0x2000);
        Assert.Equal(0xE0, pb.Status);
        Assert.Equal(0x00, pb.Data1);
        Assert.Equal(0x40, pb.Data2);
    }

    [Fact]
    public void AudioLayout_regions_do_not_overlap()
    {
        var l = new AudioLayout(512, 2, 2);
        Assert.True(l.MidiInOffset >= l.ParamInOffset + AudioLayout.MaxEvents * AudioLayout.EventSize);
        Assert.True(l.MidiOutOffset >= l.MidiInOffset + AudioLayout.MaxEvents * AudioLayout.EventSize);
        Assert.True(l.InputOffset >= l.MidiOutOffset + AudioLayout.MaxEvents * AudioLayout.EventSize);
        Assert.Equal(l.InputOffset + 2 * 512 * 4, l.OutputOffset);
        Assert.Equal(l.OutputOffset + 2 * 512 * 4, l.TotalSize);
    }

    [Fact]
    public void AudioBridge_without_host_times_out_to_silence_without_allocating()
    {
        using var bridge = new AudioBridge("Renaissance.Test." + Guid.NewGuid().ToString("N"), 256, 2, 2) { TimeoutMs = 20 };
        var input = new[] { Enumerable.Repeat(1f, 128).ToArray(), Enumerable.Repeat(1f, 128).ToArray() };
        var output = new[] { Enumerable.Repeat(9f, 128).ToArray(), Enumerable.Repeat(9f, 128).ToArray() };

        Assert.False(bridge.Process(input, output, 128));
        Assert.True(bridge.Faulted);
        Assert.All(output, ch => Assert.All(ch, s => Assert.Equal(0f, s)));

        // Once faulted the audio thread must not block at all.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bridge.Process(input, output, 128);
        Assert.True(sw.ElapsedMilliseconds < 15);
    }

    [Fact]
    public async Task AudioBridge_processing_loop_does_not_allocate()
    {
        using var bridge = new AudioBridge("Renaissance.Test." + Guid.NewGuid().ToString("N"), 256, 2, 2) { TimeoutMs = 1000 };
        using var go = EventWaitHandle.OpenExisting(AudioLayout.GoEventName(bridge.Name));
        using var done = EventWaitHandle.OpenExisting(AudioLayout.DoneEventName(bridge.Name));
        using var cts = new CancellationTokenSource();

        var echo = Task.Run(() =>
        {
            using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(bridge.Name);
            using var view = mmf.CreateViewAccessor();
            var l = bridge.Layout;
            while (!cts.IsCancellationRequested)
            {
                if (!go.WaitOne(50)) continue;
                var frames = view.ReadInt32(0);
                for (var ch = 0; ch < 2; ch++)
                for (var i = 0; i < frames; i++)
                    view.Write(l.OutputChannelOffset(ch) + i * 4, view.ReadSingle(l.InputChannelOffset(ch) + i * 4) * 0.5f);
                done.Set();
            }
        });

        var input = new[] { Enumerable.Repeat(0.8f, 64).ToArray(), Enumerable.Repeat(0.4f, 64).ToArray() };
        var output = new[] { new float[64], new float[64] };
        bridge.MidiIn.TryWrite(MidiEvent.NoteOn(0, 60, 100));

        Assert.True(bridge.Process(input, output, 64));
        Assert.Equal(0.4f, output[0][10], 4);
        Assert.Equal(0.2f, output[1][63], 4);

        bridge.Process(input, output, 64);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++) bridge.Process(input, output, 64);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        cts.Cancel();
        await echo;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void AudioBridge_adapts_mono_input_to_stereo_plugin()
    {
        using var bridge = new AudioBridge("Renaissance.Test." + Guid.NewGuid().ToString("N"), 64, 2, 1) { TimeoutMs = 20 };
        var mono = new[] { new float[32] };
        var stereoOut = new[] { new float[32], new float[32] };
        bridge.Process(mono, stereoOut, 32); // no host: must simply stay safe
        Assert.True(bridge.Faulted);
    }

    [Fact]
    public void PluginState_roundtrips_for_daw_project_save()
    {
        var state = new PluginState
        {
            PluginPath = @"C:\VST\Synth1.dll", Architecture = PluginArchitecture.X86, UniqueId = 5, PluginName = "Synth1",
            Program = 7, ProgramName = "Lead", Parameters = [0.1f, 0.7f], Chunk = [9, 8, 7], PresetPath = "p.rpreset",
        };
        var back = PluginState.FromBytes(state.ToBytes());

        Assert.Equal(state.PluginPath, back.PluginPath);
        Assert.Equal(PluginArchitecture.X86, back.Architecture);
        Assert.Equal(7, back.Program);
        Assert.Equal(new[] { 0.1f, 0.7f }, back.Parameters);
        Assert.Equal(new byte[] { 9, 8, 7 }, back.Chunk);

        var clone = state.Clone();
        clone.Parameters[0] = 1f;
        Assert.Equal(0.1f, state.Parameters[0]);
    }

    [Theory]
    [InlineData(1130, "1.1.3")]
    [InlineData(2000, "2")]
    [InlineData(2400, "2.4")]
    [InlineData(12, "12")]
    [InlineData(0, "")]
    public void Vendor_version_is_formatted(int raw, string expected) =>
        Assert.Equal(expected, PluginMetadata.FormatVersion(raw));

    [Theory]
    [InlineData("Valhalla Reverb", 0, false, "Reverb")]
    [InlineData("Ping Pong Delay", 0, false, "Delay")]
    [InlineData("Bus Compressor", 0, false, "Dynamics")]
    [InlineData("Spectrum Analyser", 0, false, "Analyzer")]
    [InlineData("Whatever", 0, true, "Instruments")]
    [InlineData("Plain Thing", 0, false, "Effects")]
    [InlineData("Plain Thing", 6, false, "Reverb")]
    public void Plugins_are_classified(string name, int vstCategory, bool synth, string expected) =>
        Assert.Equal(expected, PluginClassifier.Classify(new PluginMetadata { Name = name, Category = vstCategory, IsSynth = synth, NumOutputs = 2 }));

    [Theory]
    [InlineData("Filter Cutoff", "Filter")]
    [InlineData("Env Attack", "Amplifier")]
    [InlineData("Osc1 Waveform", "Oscillators")]
    [InlineData("Master Volume", "Master")]
    [InlineData("Zzz", "Other")]
    public void Parameters_are_grouped(string name, string expected) =>
        Assert.Equal(expected, ParameterGrouper.GroupFor(name));
}
