using Renaissance.Core.Models;

namespace Renaissance.Core.Hosting;

public sealed record PeInfo(bool IsPe, PluginArchitecture Architecture, IReadOnlyList<string> Exports)
{
    public bool ExportsVstEntry => Exports.Contains("VSTPluginMain") || Exports.Contains("main");
}

/// <summary>Reads PE headers without loading the module, so bitness and VST entry points can be checked safely.</summary>
public static class PeInspector
{
    public static PeInfo Inspect(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new BinaryReader(fs);
            if (fs.Length < 0x40 || r.ReadUInt16() != 0x5A4D) return Not();

            fs.Position = 0x3C;
            var peOffset = r.ReadInt32();
            if (peOffset <= 0 || peOffset + 24 > fs.Length) return Not();
            fs.Position = peOffset;
            if (r.ReadUInt32() != 0x00004550) return Not();

            var machine = r.ReadUInt16();
            var sectionCount = r.ReadUInt16();
            fs.Position = peOffset + 20;
            var optionalSize = r.ReadUInt16();
            var optionalStart = peOffset + 24;
            if (optionalSize < 96) return Not();

            fs.Position = optionalStart;
            var magic = r.ReadUInt16();
            var arch = machine switch
            {
                0x014C => PluginArchitecture.X86,
                0x8664 => PluginArchitecture.X64,
                _ => PluginArchitecture.Unknown,
            };
            var dirOffset = magic == 0x20B ? 112 : 96;
            if (optionalSize < dirOffset + 8) return new PeInfo(true, arch, []);

            fs.Position = optionalStart + dirOffset;
            var exportRva = r.ReadUInt32();

            var sections = new List<(uint Va, uint Size, uint Raw)>();
            fs.Position = optionalStart + optionalSize;
            for (var i = 0; i < sectionCount; i++)
            {
                fs.Position += 8;
                var virtualSize = r.ReadUInt32();
                var va = r.ReadUInt32();
                var rawSize = r.ReadUInt32();
                var raw = r.ReadUInt32();
                fs.Position += 16;
                sections.Add((va, Math.Max(virtualSize, rawSize), raw));
            }

            long ToOffset(uint rva)
            {
                foreach (var s in sections)
                    if (rva >= s.Va && rva < s.Va + s.Size) return s.Raw + (rva - s.Va);
                return -1;
            }

            if (exportRva == 0) return new PeInfo(true, arch, []);
            var exportOffset = ToOffset(exportRva);
            if (exportOffset < 0 || exportOffset + 40 > fs.Length) return new PeInfo(true, arch, []);

            fs.Position = exportOffset + 24;
            var nameCount = r.ReadUInt32();
            fs.Position = exportOffset + 32;
            var namesRva = r.ReadUInt32();
            var namesOffset = ToOffset(namesRva);
            var names = new List<string>();
            if (namesOffset >= 0 && nameCount < 100000)
            {
                for (var i = 0; i < nameCount; i++)
                {
                    fs.Position = namesOffset + i * 4;
                    var nameOffset = ToOffset(r.ReadUInt32());
                    if (nameOffset < 0) continue;
                    fs.Position = nameOffset;
                    var sb = new System.Text.StringBuilder();
                    int b;
                    while ((b = fs.ReadByte()) > 0 && sb.Length < 256) sb.Append((char)b);
                    names.Add(sb.ToString());
                }
            }
            return new PeInfo(true, arch, names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return Not();
        }

        static PeInfo Not() => new(false, PluginArchitecture.Unknown, []);
    }
}
