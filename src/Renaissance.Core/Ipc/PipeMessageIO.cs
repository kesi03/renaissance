using System.Buffers.Binary;
using System.Text.Json;

namespace Renaissance.Core.Ipc;

/// <summary>Length-prefixed (4 byte LE) JSON framing over any stream.</summary>
public static class PipeMessageIO
{
    public const int MaxMessageSize = 64 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns default when the stream is closed cleanly before a frame starts.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct = default) where T : class
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1, 3), ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxMessageSize) throw new InvalidDataException($"Invalid frame length {length}.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload);
    }
}
