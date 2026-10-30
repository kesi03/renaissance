using System.Buffers.Binary;
using System.Text;

namespace Renaissance.PresetEngine;

internal sealed class BigEndianWriter
{
    private readonly MemoryStream _ms = new();

    public void Magic(string four) => _ms.Write(Encoding.ASCII.GetBytes(four), 0, 4);

    public void Int(int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        _ms.Write(b);
    }

    public void Float(float value) => Int(BitConverter.SingleToInt32Bits(value));

    public void FixedString(string value, int length)
    {
        var bytes = new byte[length];
        var src = Encoding.ASCII.GetBytes(value ?? "");
        Array.Copy(src, bytes, Math.Min(src.Length, length - 1));
        _ms.Write(bytes, 0, length);
    }

    public void Bytes(ReadOnlySpan<byte> data) => _ms.Write(data);

    public void Zeros(int count) => _ms.Write(new byte[count], 0, count);

    public byte[] ToArray() => _ms.ToArray();
}

internal sealed class BigEndianReader
{
    private readonly byte[] _data;
    private int _pos;

    public BigEndianReader(byte[] data) => _data = data;

    public int Position => _pos;
    public int Remaining => _data.Length - _pos;

    private void Need(int count)
    {
        if (count < 0 || _pos + count > _data.Length)
            throw new InvalidDataException("Unexpected end of preset data.");
    }

    public string Magic()
    {
        Need(4);
        var s = Encoding.ASCII.GetString(_data, _pos, 4);
        _pos += 4;
        return s;
    }

    public int Int()
    {
        Need(4);
        var v = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(_pos, 4));
        _pos += 4;
        return v;
    }

    public float Float() => BitConverter.Int32BitsToSingle(Int());

    public string FixedString(int length)
    {
        Need(length);
        var span = _data.AsSpan(_pos, length);
        _pos += length;
        var end = span.IndexOf((byte)0);
        if (end < 0) end = length;
        return Encoding.ASCII.GetString(span[..end]);
    }

    public byte[] Bytes(int count)
    {
        Need(count);
        var r = new byte[count];
        Array.Copy(_data, _pos, r, 0, count);
        _pos += count;
        return r;
    }

    public void Skip(int count)
    {
        Need(count);
        _pos += count;
    }
}
