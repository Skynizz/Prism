using System.Diagnostics;
using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Ce qu'un jeu contient en plus de sa version d'origine sans que Prism l'y ait pose.
///
/// Les dossiers regardes sont ceux ou un mod agit : a cote de l'executable, la ou le jeu
/// charge Streamline, et la ou il range ses DLL NGX. Deux niveaux de certitude :
///  - certain : un fichier propre a un mod (addon ReShade, OptiScaler, fichier .asi...),
///    ou une DLL proxy dont la description se reclame d'une surcouche ;
///  - probable : un runtime NVIDIA ou Streamline cree bien apres l'executable du jeu et
///    absent du registre de Prism. Une mise a jour du jeu produirait le meme indice, d'ou
///    la prudence : ces fichiers sont signales, jamais proposes au retrait.
/// </summary>
public static class ForeignModScanner
{
    private const string Known = "detected.reason.known";
    private const string Proxy = "detected.reason.proxy";
    private const string Later = "detected.reason.later";

    /// <summary>Noms sous lesquels une surcouche se fait charger par le jeu.</summary>
    private static readonly HashSet<string> ProxyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "dinput8.dll", "opengl32.dll",
        "version.dll", "winmm.dll", "winhttp.dll", "wininet.dll", "dbghelp.dll",
        "xinput1_3.dll", "xinput1_4.dll"
    };

    /// <summary>Delai au-dela duquel un runtime pose apres le jeu n'est plus d'origine.</summary>
    private static readonly TimeSpan AfterInstall = TimeSpan.FromHours(12);

    private sealed record Hit(string Label, bool Certain, bool Removable, string ReasonKey);

    /// <param name="prismPaths">Tout ce que Prism a ecrit ou sauvegarde pour ce jeu.</param>
    /// <param name="reShadeByPrism">ReShade installe par Prism : il n'a pas de trace fichier.</param>
    public static List<DetectedMod> Scan(GameInfo game, IEnumerable<string> prismPaths, bool reShadeByPrism)
    {
        var known = prismPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dirs = new List<string> { DllInstaller.TargetDirectory(game) };
        dirs.AddRange(game.StreamlineDirectories);
        dirs.AddRange(game.Dlls.Select(d => Path.GetDirectoryName(d.Path)).OfType<string>());

        var exeCreated = CreatedUtc(game.Executable);
        var groups = new Dictionary<string, (Hit Hit, List<string> Files)>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var file in files)
            {
                if (known.Contains(file)) continue;

                var hit = Classify(file, exeCreated);
                if (hit is null || (hit.Label == "ReShade" && reShadeByPrism)) continue;

                if (!groups.TryGetValue(hit.Label, out var group))
                    groups[hit.Label] = group = (hit, new List<string>());
                group.Files.Add(file);
            }
        }

        return groups.Values
            .Select(g => new DetectedMod
            {
                Label = g.Hit.Label,
                Files = g.Files,
                Certain = g.Hit.Certain,
                Removable = g.Hit.Removable,
                Reason = Loc.T(g.Hit.ReasonKey)
            })
            .OrderBy(m => m.Certain ? 0 : 1)
            .ThenBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Hit? Classify(string file, DateTime? exeCreated)
    {
        var name = Path.GetFileName(file).ToLowerInvariant();

        // Fichiers propres a un mod : leur seule presence suffit.
        if (name is "reshade64.dll" or "reshade32.dll" or "reshade.ini" or "reshadepreset.ini")
            return new Hit("ReShade", true, true, Known);
        if (name.EndsWith(".addon64") || name.EndsWith(".addon32") || name.EndsWith(".addon"))
            return new Hit(AddonLabel(name), true, true, Known);
        if (name.StartsWith("optiscaler") || name == "nvngx.ini")
            return new Hit("OptiScaler", true, true, Known);
        if (name.StartsWith("dlss-enabler"))
            return new Hit("DLSS Enabler", true, true, Known);
        // Mod dlssg-to-fsr3 de Nukem : DLSS-G redirige vers FSR 3, distinct d'OptiScaler.
        if (name.StartsWith("dlssg_to_fsr3"))
            return new Hit("dlssg-to-fsr3", true, true, Known);
        if (name.StartsWith("rtx40mfg") || name.StartsWith("rtxmfg"))
            return new Hit("RTX40MFG-Unlock", true, true, Known);
        if (name.StartsWith("dlssg_sm86") || name.StartsWith("dlssg_sm75"))
            return new Hit("dlssg sm_86 / sm_75", true, true, Known);
        if (name.StartsWith("specialk"))
            return new Hit("Special K", true, true, Known);
        if (name.StartsWith("dlsstweaks"))
            return new Hit("DLSSTweaks", true, true, Known);
        if (name.EndsWith(".asi"))
            return new Hit("ASI Loader", true, true, Known);
        if (name == "nvngx_dlssnr.dll")
            return new Hit("Neural Rendering", true, true, Known);

        // Proxy : seul le binaire qui se reclame d'une surcouche est un mod.
        if (ProxyNames.Contains(name))
        {
            var tag = Describe(file);
            if (tag.Contains("reshade")) return new Hit("ReShade", true, true, Proxy);
            if (tag.Contains("optiscaler")) return new Hit("OptiScaler", true, true, Proxy);
            if (tag.Contains("dlss enabler")) return new Hit("DLSS Enabler", true, true, Proxy);
            if (tag.Contains("rtxmfg") || tag.Contains("mfg unlock")) return new Hit("RTX40MFG-Unlock", true, true, Proxy);
            if (tag.Contains("special k")) return new Hit("Special K", true, true, Proxy);
            if (tag.Contains("dlsstweaks")) return new Hit("DLSSTweaks", true, true, Proxy);
            if (tag.Contains("asi loader")) return new Hit("ASI Loader", true, true, Proxy);
            return null;
        }

        // Runtimes NVIDIA et Streamline : aucun original a restaurer, seulement signales.
        var nvidia = name.StartsWith("nvngx_") && name.EndsWith(".dll");
        var streamline = name.StartsWith("sl.") && name.EndsWith(".dll");
        if ((nvidia || streamline) && exeCreated is { } exe && CreatedUtc(file) is { } created
            && created > exe + AfterInstall)
            return new Hit(nvidia ? "DLSS runtime" : "Streamline", false, false, Later);

        return null;
    }

    /// <summary>Famille d'un addon ReShade, alignee sur les noms des installations Prism.</summary>
    private static string AddonLabel(string name) =>
        name.StartsWith("renodx-dlss5") ? "RenoDX DLSS 5"
        : name.Contains("mfg") ? "RenoDX MFG Unlock"
        : name.StartsWith("dlss5-bridge") ? "DLSS 5 Bridge"
        : name.StartsWith("renodx") ? "RenoDX HDR"
        : "ReShade addon";

    private static string Describe(string file)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            return $"{info.FileDescription} {info.ProductName} {info.CompanyName}".ToLowerInvariant();
        }
        catch { return ""; }
    }

    private static DateTime? CreatedUtc(string? path)
    {
        try { return path is not null && File.Exists(path) ? File.GetCreationTimeUtc(path) : null; }
        catch { return null; }
    }
}
