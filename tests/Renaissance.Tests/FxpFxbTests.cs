using Renaissance.PresetEngine;
using Xunit;

namespace Renaissance.Tests;

public class FxpFxbTests
{
    [Fact]
    public void Fxp_parameter_program_roundtrips()
    {
        var fxp = new FxpFile { FxId = 0x53796E31, FxVersion = 1130, ProgramName = "Warm Pad", Parameters = [0f, 0.25f, 0.5f, 1f] };
        var back = FxpFile.FromBytes(fxp.ToBytes());

        Assert.False(back.IsChunk);
        Assert.Equal(0x53796E31, back.FxId);
        Assert.Equal(1130, back.FxVersion);
        Assert.Equal("Warm Pad", back.ProgramName);
        Assert.Equal(fxp.Parameters, back.Parameters);
    }

    [Fact]
    public void Fxp_chunk_program_roundtrips()
    {
        var chunk = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var back = FxpFile.FromBytes(new FxpFile { IsChunk = true, FxId = 7, ProgramName = "Chunky", Chunk = chunk }.ToBytes());

        Assert.True(back.IsChunk);
        Assert.Equal(chunk, back.Chunk);
        Assert.Equal("Chunky", back.ProgramName);
    }

    [Fact]
    public void Fxp_uses_big_endian_with_correct_header()
    {
        var bytes = new FxpFile { FxId = 1, FxVersion = 2, ProgramName = "A", Parameters = [1f] }.ToBytes();

        Assert.Equal("CcnK", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("FxCk", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        var size = (bytes[4] << 24) | (bytes[5] << 16) | (bytes[6] << 8) | bytes[7];
        Assert.Equal(bytes.Length - 8, size);
        Assert.Equal(new byte[] { 0x3F, 0x80, 0, 0 }, bytes[^4..]);
    }

    [Fact]
    public void Fxp_name_is_truncated_to_27_characters()
    {
        var back = FxpFile.FromBytes(new FxpFile { ProgramName = new string('x', 60), Parameters = [] }.ToBytes());
        Assert.Equal(27, back.ProgramName.Length);
    }

    [Fact]
    public void Fxb_regular_bank_roundtrips()
    {
        var fxb = new FxbFile
        {
            FxId = 42,
            FxVersion = 3,
            CurrentProgram = 1,
            Programs =
            [
                new FxpFile { FxId = 42, ProgramName = "One", Parameters = [0.1f, 0.2f] },
                new FxpFile { FxId = 42, ProgramName = "Two", Parameters = [0.3f, 0.4f] },
            ],
        };
        var back = FxbFile.FromBytes(fxb.ToBytes());

        Assert.False(back.IsChunk);
        Assert.Equal(1, back.CurrentProgram);
        Assert.Equal(2, back.Programs.Count);
        Assert.Equal("Two", back.Programs[1].ProgramName);
        Assert.Equal(new[] { 0.3f, 0.4f }, back.Programs[1].Parameters);
    }

    [Fact]
    public void Fxb_chunk_bank_roundtrips()
    {
        var back = FxbFile.FromBytes(new FxbFile { IsChunk = true, FxId = 9, CurrentProgram = 4, Chunk = [1, 2, 3, 4, 5] }.ToBytes());

        Assert.True(back.IsChunk);
        Assert.Equal(4, back.CurrentProgram);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, back.Chunk);
    }

    [Fact]
    public void Invalid_data_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => FxpFile.FromBytes(System.Text.Encoding.ASCII.GetBytes("NOPE0000000000000000")));
        Assert.Throws<InvalidDataException>(() => FxpFile.FromBytes([0x43, 0x63]));
        Assert.Throws<InvalidDataException>(() => FxbFile.FromBytes(new FxpFile().ToBytes()));
    }
}
