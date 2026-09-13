namespace Prism.Services;

/// <summary>Architecture d'un executable, lue dans son en-tete PE.</summary>
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
}
