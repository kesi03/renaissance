using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Renaissance.Core.Audio;

namespace Renaissance.Host;

/// <summary>Host-side counterpart of <see cref="AudioBridge"/>: processes blocks straight out of shared memory.</summary>
internal sealed unsafe class AudioEngine : IDisposable
{
    private const int MidiEventBytes = 32;

    public static AudioEngine? Current { get; private set; }

    private readonly Vst2Plugin _plugin;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly EventWaitHandle _go;
    private readonly EventWaitHandle _done;
    private readonly AudioLayout _layout;
    private readonly byte* _base;
    private readonly float** _ins;
    private readonly float** _outs;
    private readonly float* _scratch;
    private readonly byte* _events;
    private readonly int _midiStorageOffset;
    private readonly int _sysexOffset;
    private readonly int _sysexDataOffset;
    private readonly int _ptr = IntPtr.Size;
    private readonly Thread _thread;
    private volatile bool _running;
    private byte[]? _pendingSysEx;
    private int _outMidiCount;

    public AudioEngine(Vst2Plugin plugin, string name, double sampleRate, int blockSize, int inputs, int outputs)
    {
        _plugin = plugin;
        _layout = new AudioLayout(blockSize, inputs, outputs);
        _mmf = MemoryMappedFile.OpenExisting(name);
        _view = _mmf.CreateViewAccessor(0, _layout.TotalSize);
        byte* b = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref b);
        _base = b;
        _go = EventWaitHandle.OpenExisting(AudioLayout.GoEventName(name));
        _done = EventWaitHandle.OpenExisting(AudioLayout.DoneEventName(name));

        var pluginIns = Math.Max(plugin.NumInputs, 1);
        var pluginOuts = Math.Max(plugin.NumOutputs, 1);
        _scratch = (float*)NativeMemory.AllocZeroed((nuint)(blockSize * sizeof(float)));
        _ins = (float**)NativeMemory.AllocZeroed((nuint)(pluginIns * _ptr));
        _outs = (float**)NativeMemory.AllocZeroed((nuint)(pluginOuts * _ptr));
        for (var ch = 0; ch < pluginIns; ch++)
            _ins[ch] = ch < inputs ? (float*)(_base + _layout.InputChannelOffset(ch)) : _scratch;
        for (var ch = 0; ch < pluginOuts; ch++)
            _outs[ch] = ch < outputs ? (float*)(_base + _layout.OutputChannelOffset(ch)) : _scratch;

        // VstEvents block: int numEvents; ptr reserved; ptr events[N]
        var arrayBytes = _ptr * 2 + (AudioLayout.MaxEvents + 1) * _ptr;
        _midiStorageOffset = (arrayBytes + 15) & ~15;
        _sysexOffset = _midiStorageOffset + AudioLayout.MaxEvents * MidiEventBytes;
        _sysexDataOffset = _sysexOffset + 64;
        _events = (byte*)NativeMemory.AllocZeroed((nuint)(_sysexDataOffset + 8192));

        plugin.Configure(sampleRate, blockSize);
        plugin.Resume();

        Current = this;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Renaissance.Audio", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void QueueSysEx(byte[] data) => Interlocked.Exchange(ref _pendingSysEx, data);

    private void Loop()
    {
        while (_running)
        {
            if (!_go.WaitOne(100)) continue;
            if (!_running) break;
            try { ProcessBlock(); }
            catch { }
            _done.Set();
        }
    }

    private void ProcessBlock()
    {
        var header = (int*)_base;
        var frames = Math.Clamp(header[0], 0, _layout.MaxFrames);
        var paramCount = header[1];
        var midiCount = header[2];

        var paramBase = (int*)(_base + _layout.ParamInOffset);
        for (var i = 0; i < paramCount; i++)
        {
            var index = paramBase[i * 2];
            if (index >= 0 && index < _plugin.NumParams) _plugin.SetParameter(index, ((float*)paramBase)[i * 2 + 1]);
        }

        var midiBase = (int*)(_base + _layout.MidiInOffset);
        var pointers = (IntPtr*)(_events + _ptr * 2);
        var count = 0;
        for (var i = 0; i < midiCount && i < AudioLayout.MaxEvents; i++)
        {
            var e = _events + _midiStorageOffset + i * MidiEventBytes;
            var packed = midiBase[i * 2 + 1];
            ((int*)e)[0] = 1;                                   // kVstMidiType
            ((int*)e)[1] = MidiEventBytes - 8;                  // byteSize
            ((int*)e)[2] = Math.Clamp(midiBase[i * 2], 0, Math.Max(0, frames - 1));
            ((int*)e)[3] = 1;                                   // realtime
            ((int*)e)[4] = 0;
            ((int*)e)[5] = 0;
            e[24] = (byte)(packed & 0xFF);
            e[25] = (byte)((packed >> 8) & 0xFF);
            e[26] = (byte)((packed >> 16) & 0xFF);
            e[27] = 0;
            *(int*)(e + 28) = 0;
            pointers[count++] = (IntPtr)e;
        }

        var sysex = Interlocked.Exchange(ref _pendingSysEx, null);
        if (sysex is { Length: > 0 and <= 8192 })
        {
            var s = _events + _sysexOffset;
            var dumpAt = _events + _sysexDataOffset;
            sysex.CopyTo(new Span<byte>(dumpAt, sysex.Length));
            var resvd1 = (_ptr == 8) ? 24 : 20;
            ((int*)s)[0] = 6;                                   // kVstSysExType
            ((int*)s)[1] = resvd1 + 3 * _ptr - 8;
            ((int*)s)[2] = 0;
            ((int*)s)[3] = 0;
            ((int*)s)[4] = sysex.Length;
            *(IntPtr*)(s + resvd1) = IntPtr.Zero;
            *(IntPtr*)(s + resvd1 + _ptr) = (IntPtr)dumpAt;
            *(IntPtr*)(s + resvd1 + _ptr * 2) = IntPtr.Zero;
            pointers[count++] = (IntPtr)s;
        }

        if (count > 0)
        {
            *(int*)_events = count;
            _plugin.ProcessEvents(_events);
        }

        Volatile.Write(ref _outMidiCount, 0);
        if (_plugin.CanReplacing && frames > 0) _plugin.ProcessReplacing(_ins, _outs, frames);
        header[3] = Volatile.Read(ref _outMidiCount);
    }

    /// <summary>Called from the plugin's audioMasterProcessEvents callback with a VstEvents*.</summary>
    public void CaptureOutgoing(byte* events)
    {
        if (events == null) return;
        var n = *(int*)events;
        var pointers = (IntPtr*)(events + _ptr * 2);
        var outBase = (int*)(_base + _layout.MidiOutOffset);
        for (var i = 0; i < n; i++)
        {
            var e = (byte*)pointers[i];
            if (e == null || ((int*)e)[0] != 1) continue;
            var slot = Volatile.Read(ref _outMidiCount);
            if (slot >= AudioLayout.MaxEvents) return;
            outBase[slot * 2] = ((int*)e)[2];
            outBase[slot * 2 + 1] = e[24] | (e[25] << 8) | (e[26] << 16);
            Volatile.Write(ref _outMidiCount, slot + 1);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(500);
        try { _plugin.Suspend(); } catch { }
        if (ReferenceEquals(Current, this)) Current = null;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
        _go.Dispose();
        _done.Dispose();
        NativeMemory.Free(_ins);
        NativeMemory.Free(_outs);
        NativeMemory.Free(_scratch);
        NativeMemory.Free(_events);
    }
}
