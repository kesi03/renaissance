namespace Renaissance.PresetEngine;

/// <summary>A single VST2 program (.fxp). Supports both parameter ("FxCk") and opaque chunk ("FPCh") variants.</summary>
public sealed class FxpFile
{
    internal const int NameLength = 28;

    public bool IsChunk { get; set; }
    public int FxId { get; set; }
    public int FxVersion { get; set; }
    public string ProgramName { get; set; } = "";
    public float[] Parameters { get; set; } = [];
    public byte[] Chunk { get; set; } = [];

    public byte[] ToBytes()
    {
        var body = new BigEndianWriter();
        body.Magic(IsChunk ? "FPCh" : "FxCk");
        body.Int(1);
        body.Int(FxId);
        body.Int(FxVersion);
        if (IsChunk)
        {
            body.Int(1);
            body.FixedString(ProgramName, NameLength);
            body.Int(Chunk.Length);
            body.Bytes(Chunk);
        }
        else
        {
            body.Int(Parameters.Length);
            body.FixedString(ProgramName, NameLength);
            foreach (var p in Parameters) body.Float(p);
        }

        var bodyBytes = body.ToArray();
        var w = new BigEndianWriter();
        w.Magic("CcnK");
        w.Int(bodyBytes.Length);
        w.Bytes(bodyBytes);
        return w.ToArray();
    }

    public static FxpFile FromBytes(byte[] data)
    {
        var r = new BigEndianReader(data);
        return Read(r);
    }

    internal static FxpFile Read(BigEndianReader r)
    {
        if (r.Magic() != "CcnK") throw new InvalidDataException("Not a VST preset file (missing CcnK).");
        r.Int();
        var kind = r.Magic();
        r.Int();
        var file = new FxpFile { FxId = r.Int(), FxVersion = r.Int() };
        switch (kind)
        {
            case "FxCk":
            {
                var count = r.Int();
                if (count < 0 || count > r.Remaining / 4 + 1000000) throw new InvalidDataException("Bad parameter count.");
                file.ProgramName = r.FixedString(NameLength);
                file.Parameters = new float[count];
                for (var i = 0; i < count; i++) file.Parameters[i] = r.Float();
                break;
            }
            case "FPCh":
            {
                r.Int();
                file.ProgramName = r.FixedString(NameLength);
                file.IsChunk = true;
                file.Chunk = r.Bytes(r.Int());
                break;
            }
            default:
                throw new InvalidDataException($"Unsupported program type '{kind}'.");
        }
        return file;
    }

    public static FxpFile Load(string path) => FromBytes(File.ReadAllBytes(path));
    public void Save(string path) => File.WriteAllBytes(path, ToBytes());
}

/// <summary>A VST2 bank (.fxb). Regular banks ("FxBk") hold programs; chunk banks ("FBCh") hold one opaque blob.</summary>
public sealed class FxbFile
{
    public bool IsChunk { get; set; }
    public int FxId { get; set; }
    public int FxVersion { get; set; }
    public int CurrentProgram { get; set; }
    public List<FxpFile> Programs { get; set; } = [];
    public byte[] Chunk { get; set; } = [];

    public byte[] ToBytes()
    {
        var body = new BigEndianWriter();
        body.Magic(IsChunk ? "FBCh" : "FxBk");
        body.Int(2);
        body.Int(FxId);
        body.Int(FxVersion);
        body.Int(IsChunk ? 1 : Programs.Count);
        body.Int(CurrentProgram);
        body.Zeros(124);
        if (IsChunk)
        {
            body.Int(Chunk.Length);
            body.Bytes(Chunk);
        }
        else
        {
            foreach (var p in Programs) body.Bytes(p.ToBytes());
        }

        var bodyBytes = body.ToArray();
        var w = new BigEndianWriter();
        w.Magic("CcnK");
        w.Int(bodyBytes.Length);
        w.Bytes(bodyBytes);
        return w.ToArray();
    }

    public static FxbFile FromBytes(byte[] data)
    {
        var r = new BigEndianReader(data);
        if (r.Magic() != "CcnK") throw new InvalidDataException("Not a VST bank file (missing CcnK).");
        r.Int();
        var kind = r.Magic();
        var version = r.Int();
        var file = new FxbFile { FxId = r.Int(), FxVersion = r.Int() };
        var count = r.Int();
        file.CurrentProgram = version >= 2 ? r.Int() : 0;
        if (version < 2) r.Skip(4);
        r.Skip(124);
        switch (kind)
        {
            case "FxBk":
                if (count < 0 || count > 100000) throw new InvalidDataException("Bad program count.");
                for (var i = 0; i < count; i++) file.Programs.Add(FxpFile.Read(r));
                break;
            case "FBCh":
                file.IsChunk = true;
                file.Chunk = r.Bytes(r.Int());
                break;
            default:
                throw new InvalidDataException($"Unsupported bank type '{kind}'.");
        }
        return file;
    }

    public static FxbFile Load(string path) => FromBytes(File.ReadAllBytes(path));
    public void Save(string path) => File.WriteAllBytes(path, ToBytes());
}
