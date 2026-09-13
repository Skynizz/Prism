namespace Prism.Services;

/// <summary>Architecture et dependances d'un executable, lues dans son en-tete PE.</summary>
public static class PeInfo
{
    private const ushort MachineI386 = 0x014C;

    /// <summary>Vrai pour un binaire 32 bits ; faux pour x64, ou si l'en-tete est illisible.</summary>
    public static bool Is32Bit(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return false; // "MZ"

            fs.Position = 0x3C;
            var peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > fs.Length) return false;

            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return false; // "PE\0\0"

            return br.ReadUInt16() == MachineI386;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Bibliotheques importees par l'executable, imports differes compris, en minuscules.
    /// C'est ce que RHI regarde pour deviner l'API : d3d9.dll, opengl32.dll, dxgi.dll...
    /// Vide si l'en-tete est illisible.
    /// </summary>
    public static HashSet<string> Imports(string? path)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return result;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return result;
            fs.Position = 0x3C;
            var pe = br.ReadInt32();
            if (pe <= 0 || pe + 24 > fs.Length) return result;

            fs.Position = pe;
            if (br.ReadUInt32() != 0x00004550) return result;

            fs.Position = pe + 6;
            var sectionCount = br.ReadUInt16();
            fs.Position = pe + 20;
            var optionalSize = br.ReadUInt16();

            var optional = pe + 24;
            fs.Position = optional;
            var magic = br.ReadUInt16();
            var plus = magic == 0x20B;
            if (!plus && magic != 0x10B) return result;

            fs.Position = optional + (plus ? 24 : 28);
            var imageBase = plus ? br.ReadUInt64() : br.ReadUInt32();

            var directories = optional + (plus ? 112 : 96);
            fs.Position = directories + 8;          // index 1 : imports
            var importRva = br.ReadUInt32();
            fs.Position = directories + 13 * 8;     // index 13 : imports differes
            var delayRva = br.ReadUInt32();

            var sections = new List<(uint Va, uint Size, uint Raw)>();
            fs.Position = optional + optionalSize;
            for (var i = 0; i < sectionCount; i++)
            {
                var header = fs.Position;
                fs.Position = header + 8;
                var virtualSize = br.ReadUInt32();
                var va = br.ReadUInt32();
                var rawSize = br.ReadUInt32();
                var raw = br.ReadUInt32();
                sections.Add((va, Math.Max(virtualSize, rawSize), raw));
                fs.Position = header + 40;
            }

            long ToOffset(ulong rva)
            {
                foreach (var s in sections)
                    if (rva >= s.Va && rva < (ulong)s.Va + s.Size)
                        return (long)(rva - s.Va + s.Raw);
                return -1;
            }

            string? ReadName(ulong rva)
            {
                var offset = ToOffset(rva);
                if (offset < 0 || offset >= fs.Length) return null;
                fs.Position = offset;
                var bytes = new List<byte>();
                for (int b; bytes.Count < 260 && (b = fs.ReadByte()) > 0;) bytes.Add((byte)b);
                return bytes.Count == 0 ? null : System.Text.Encoding.ASCII.GetString(bytes.ToArray());
            }

            if (importRva != 0 && ToOffset(importRva) is var start and >= 0)
            {
                for (var i = 0; i < 4096; i++)
                {
                    fs.Position = start + i * 20L + 12;
                    var nameRva = br.ReadUInt32();
                    if (nameRva == 0) break;
                    if (ReadName(nameRva) is { } name) result.Add(name.ToLowerInvariant());
                }
            }

            if (delayRva != 0 && ToOffset(delayRva) is var delay and >= 0)
            {
                for (var i = 0; i < 4096; i++)
                {
                    fs.Position = delay + i * 32L;
                    var attributes = br.ReadUInt32();
                    ulong nameRva = br.ReadUInt32();
                    if (nameRva == 0) break;
                    // Anciens linkers : adresse virtuelle au lieu d'une RVA.
                    if ((attributes & 1) == 0 && nameRva >= imageBase) nameRva -= imageBase;
                    if (ReadName(nameRva) is { } name) result.Add(name.ToLowerInvariant());
                }
            }
        }
        catch
        {
            // En-tete tronque ou atypique : on rend ce qui a pu etre lu.
        }

        return result;
    }
}
