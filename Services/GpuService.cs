using System.Text.RegularExpressions;
using Microsoft.Win32;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Identifie le GPU via le registre des classes d'affichage. On evite volontairement
/// System.Management (WMI) : une dependance NuGet de plus pour une information
/// deja presente dans le registre.
/// </summary>
public static class GpuService
{
    private const string DisplayClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static GpuInfo Detect()
    {
        var candidates = new List<GpuInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (cls is not null)
            {
                foreach (var sub in cls.GetSubKeyNames())
                {
                    // Les sous-cles utiles sont numerotees "0000", "0001", ...
                    if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                    using var k = cls.OpenSubKey(sub);
                    var desc = k?.GetValue("DriverDesc") as string;
                    if (string.IsNullOrWhiteSpace(desc)) continue;

                    var wddm = k?.GetValue("DriverVersion") as string ?? "";
                    candidates.Add(new GpuInfo
                    {
                        Name = desc!,
                        DriverVersion = wddm,
                        DriverBranch = SystemInfo.NvidiaMarketingVersion(wddm),
                        DirectXLevel = SystemInfo.DirectXLevel(wddm),
                        VramBytes = SystemInfo.GetVramBytes(desc!),
                        Generation = ClassifyGeneration(desc!)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Detection GPU impossible : {ex.Message}");
        }

        // Priorite au GPU NVIDIA : sur un portable Optimus, l'iGPU Intel apparait aussi.
        return candidates.FirstOrDefault(g => g.Generation is not (GpuGeneration.NonNvidia or GpuGeneration.Unknown))
               ?? candidates.FirstOrDefault()
               ?? new GpuInfo();
    }

    /// <summary>
    /// Deduit la generation depuis le nom commercial. Couvre GeForce, Quadro RTX
    /// et les cartes mobiles ("Laptop GPU").
    /// </summary>
    public static GpuGeneration ClassifyGeneration(string name)
    {
        if (name.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) < 0 &&
            name.IndexOf("geforce", StringComparison.OrdinalIgnoreCase) < 0 &&
            name.IndexOf("quadro", StringComparison.OrdinalIgnoreCase) < 0 &&
            name.IndexOf("rtx", StringComparison.OrdinalIgnoreCase) < 0)
            return GpuGeneration.NonNvidia;

        // "RTX 4070", "RTX A2000", "GTX 1660"
        var m = Regex.Match(name, @"\b(?:RTX|GTX)\s*([0-9]{3,4})\b", RegexOptions.IgnoreCase);
        if (!m.Success) return GpuGeneration.Unknown;

        var num = int.Parse(m.Groups[1].Value);
        // Les modeles a 3 chiffres (RTX 500 Ada) ne sont pas des cartes de bureau : on les ignore.
        if (num < 1000) return GpuGeneration.Unknown;

        return (num / 1000) switch
        {
            1 => GpuGeneration.NonNvidia, // GTX 16xx : pas de coeurs tensor exploitables par DLSS
            2 => GpuGeneration.Turing,
            3 => GpuGeneration.Ampere,
            4 => GpuGeneration.AdaLovelace,
            5 => GpuGeneration.Blackwell,
            _ => GpuGeneration.NewerNvidia
        };
    }
}
