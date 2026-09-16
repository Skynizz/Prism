using System.Runtime.InteropServices;
using Microsoft.Win32;
using Prism.Models;

namespace Prism.Services;

/// <summary>Etat de l'ecran principal, lu via l'API d'affichage Windows.</summary>
public sealed record DisplayInfo(int Width, int Height, int RefreshHz, int BitsPerPixel)
{
    public string Resolution => $"{Width} × {Height}";
    public string Summary => $"{Width} × {Height} @ {RefreshHz} Hz";
}

/// <summary>
/// Informations materielles et systeme affichees dans la vue d'ensemble.
/// Tout vient du registre et de user32 : aucune dependance externe.
/// </summary>
public static class SystemInfo
{
    private const string DisplayClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    // ------------------------------------------------------------------ Ecran

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    private const int CurrentSettings = -1;

    public static DisplayInfo GetDisplay()
    {
        try
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, CurrentSettings, ref dm))
                return new DisplayInfo((int)dm.dmPelsWidth, (int)dm.dmPelsHeight,
                    (int)dm.dmDisplayFrequency, (int)dm.dmBitsPerPel);
        }
        catch (Exception ex) { Log.Warn("system", $"Cannot read the display: {ex.Message}"); }

        return new DisplayInfo(0, 0, 0, 0);
    }

    // -------------------------------------------------------------------- GPU

    /// <summary>Memoire dediee de la carte, en octets, ou 0 si illisible.</summary>
    public static long GetVramBytes(string gpuName)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (cls is null) return 0;

            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") as string != gpuName) continue;

                // Windows expose la taille en QWORD sur les cartes modernes.
                if (k.GetValue("HardwareInformation.qwMemorySize") is long qw && qw > 0) return qw;

                if (k.GetValue("HardwareInformation.MemorySize") is byte[] raw)
                {
                    if (raw.Length >= 8) return BitConverter.ToInt64(raw, 0);
                    if (raw.Length >= 4) return BitConverter.ToUInt32(raw, 0);
                }
                if (k.GetValue("HardwareInformation.MemorySize") is int mi && mi > 0) return mi;
            }
        }
        catch (Exception ex) { Log.Warn("system", $"Cannot read VRAM: {ex.Message}"); }

        return 0;
    }

    /// <summary>
    /// Traduit la version WDDM en version commerciale NVIDIA : le pilote 32.0.16.1692
    /// est connu des joueurs sous le nom 616.92. La convention consiste a concatener
    /// les deux derniers champs et a n'en garder que les cinq derniers chiffres.
    /// </summary>
    public static string? NvidiaMarketingVersion(string wddmVersion)
    {
        try
        {
            var parts = wddmVersion.Split('.');
            if (parts.Length < 4) return null;

            var joined = parts[2] + parts[3].PadLeft(4, '0');
            if (joined.Length < 5) return null;

            var five = joined[^5..];
            return $"{five[..3]}.{five[3..]}";
        }
        catch { return null; }
    }

    /// <summary>
    /// Niveau DirectX annonce. WDDM 3.x implique un pilote Direct3D 12 Ultimate ;
    /// on ne pretend pas interroger le device, on qualifie ce que le pilote supporte.
    /// </summary>
    public static string DirectXLevel(string wddmVersion)
    {
        var major = wddmVersion.Split('.').FirstOrDefault();
        return major switch
        {
            "32" or "31" => "DirectX 12 Ultimate",
            "30" => "DirectX 12 Ultimate",
            "27" or "26" => "DirectX 12",
            _ => "DirectX 12"
        };
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        double v = bytes;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i >= 3 ? $"{v:0.#} {u[i]}" : $"{v:0} {u[i]}";
    }
}

/// <summary>
/// Determine si l'un des jeux detectes tourne actuellement. Sert au bandeau
/// "jeu actif" de la vue d'ensemble.
/// </summary>
public static class ProcessWatcher
{
    /// <summary>Renvoie le jeu dont l'executable correspond a un processus vivant.</summary>
    public static GameInfo? FindRunning(IEnumerable<GameInfo> games)
    {
        Dictionary<string, bool> running;
        try
        {
            running = System.Diagnostics.Process.GetProcesses()
                .Select(p => { try { return p.ProcessName; } catch { return null; } })
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(n => n!, _ => true, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("system", $"Cannot enumerate processes: {ex.Message}");
            return null;
        }

        foreach (var g in games)
        {
            if (string.IsNullOrWhiteSpace(g.Executable)) continue;
            var name = Path.GetFileNameWithoutExtension(g.Executable);
            if (!string.IsNullOrEmpty(name) && running.ContainsKey(name)) return g;
        }
        return null;
    }
}
