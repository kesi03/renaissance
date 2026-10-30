using System.Runtime.InteropServices;

namespace Renaissance.Core.Audio;

public readonly record struct MidiEvent(int DeltaFrames, byte Status, byte Data1, byte Data2)
{
    public int Packed => Status | (Data1 << 8) | (Data2 << 16);

    public static MidiEvent Unpack(int delta, int packed) =>
        new(delta, (byte)(packed & 0xFF), (byte)((packed >> 8) & 0xFF), (byte)((packed >> 16) & 0xFF));

    public static MidiEvent NoteOn(int channel, int note, int velocity, int delta = 0) =>
        new(delta, (byte)(0x90 | (channel & 0xF)), (byte)(note & 0x7F), (byte)(velocity & 0x7F));

    public static MidiEvent NoteOff(int channel, int note, int velocity = 0, int delta = 0) =>
        new(delta, (byte)(0x80 | (channel & 0xF)), (byte)(note & 0x7F), (byte)(velocity & 0x7F));

    public static MidiEvent ControlChange(int channel, int controller, int value, int delta = 0) =>
        new(delta, (byte)(0xB0 | (channel & 0xF)), (byte)(controller & 0x7F), (byte)(value & 0x7F));

    public static MidiEvent PitchBend(int channel, int value14, int delta = 0) =>
        new(delta, (byte)(0xE0 | (channel & 0xF)), (byte)(value14 & 0x7F), (byte)((value14 >> 7) & 0x7F));

    public static MidiEvent Aftertouch(int channel, int pressure, int delta = 0) =>
        new(delta, (byte)(0xD0 | (channel & 0xF)), (byte)(pressure & 0x7F), 0);

    public static MidiEvent PolyAftertouch(int channel, int note, int pressure, int delta = 0) =>
        new(delta, (byte)(0xA0 | (channel & 0xF)), (byte)(note & 0x7F), (byte)(pressure & 0x7F));

    public static MidiEvent ProgramChange(int channel, int program, int delta = 0) =>
        new(delta, (byte)(0xC0 | (channel & 0xF)), (byte)(program & 0x7F), 0);
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ParamEvent(int Index, float Value);

/// <summary>Lock-free, allocation-free single-producer/single-consumer ring buffer.</summary>
public sealed class SpscRing<T> where T : struct
{
    private readonly T[] _items;
    private readonly int _mask;
    private long _head;
    private long _tail;

    public SpscRing(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo <= 0 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of two.", nameof(capacityPowerOfTwo));
        _items = new T[capacityPowerOfTwo];
        _mask = capacityPowerOfTwo - 1;
    }

    public bool TryWrite(in T item)
    {
        var tail = Volatile.Read(ref _tail);
        if (tail - Volatile.Read(ref _head) >= _items.Length) return false;
        _items[tail & _mask] = item;
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    public bool TryRead(out T item)
    {
        var head = Volatile.Read(ref _head);
        if (head >= Volatile.Read(ref _tail))
        {
            item = default;
            return false;
        }
        item = _items[head & _mask];
        Volatile.Write(ref _head, head + 1);
        return true;
    }

    public int Count => (int)(Volatile.Read(ref _tail) - Volatile.Read(ref _head));
}

/// <summary>Binary layout of the shared-memory block used for audio, MIDI and automation exchange.</summary>
public sealed class AudioLayout
{
    public const int MaxEvents = 256;
    public const int HeaderSize = 16;
    public const int EventSize = 8;

    public AudioLayout(int maxFrames, int inputs, int outputs)
    {
        MaxFrames = maxFrames;
        Inputs = inputs;
        Outputs = outputs;
        ParamInOffset = HeaderSize;
        MidiInOffset = ParamInOffset + MaxEvents * EventSize;
        MidiOutOffset = MidiInOffset + MaxEvents * EventSize;
        InputOffset = MidiOutOffset + MaxEvents * EventSize;
        OutputOffset = InputOffset + inputs * maxFrames * sizeof(float);
        TotalSize = OutputOffset + outputs * maxFrames * sizeof(float);
    }

    public int MaxFrames { get; }
    public int Inputs { get; }
    public int Outputs { get; }
    public int ParamInOffset { get; }
    public int MidiInOffset { get; }
    public int MidiOutOffset { get; }
    public int InputOffset { get; }
    public int OutputOffset { get; }
    public int TotalSize { get; }

    // Header: [0]=frames [1]=paramInCount [2]=midiInCount [3]=midiOutCount
    public int InputChannelOffset(int channel) => InputOffset + channel * MaxFrames * sizeof(float);
    public int OutputChannelOffset(int channel) => OutputOffset + channel * MaxFrames * sizeof(float);

    public static string GoEventName(string name) => name + ".go";
    public static string DoneEventName(string name) => name + ".done";
}
