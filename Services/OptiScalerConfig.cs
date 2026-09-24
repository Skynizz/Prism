using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Ecrit OptiScaler.ini pour n'activer que ce qui est demande.
///
/// Un paquet OptiScaler apporte tout a la fois : remplacement de l'upscaler,
/// generation d'images, passe neurale. Vouloir DLSS 5 seul, ou le MFG seul, suppose
/// donc d'eteindre le reste explicitement.
///
/// Les cles utilisees sont celles du fichier livre par le projet ; aucune n'est
/// inventee. Une valeur "auto" laisse OptiScaler decider, ce qui est justement ce
/// qu'on veut eviter pour un profil cible.
/// </summary>
public static class OptiScalerConfig
{
    private const string Src = "optiini";
    private const string FileName = "OptiScaler.ini";

    /// <summary>
    /// Applique un profil au fichier de configuration du titre. Les cles absentes
    /// sont ajoutees dans leur section, les autres lignes ne sont pas touchees :
    /// on ne veut pas effacer les reglages deja ajustes par l'utilisateur.
    /// </summary>
    /// <param name="nativeFg">
    /// Le jeu a son propre DLSS-G (Streamline) : il sert d'entree. Sinon la generation part de
    /// l'upscaler du jeu (« OptiFG », FGInput=upscaler), ce qui exige HUDFix contre les defauts d'interface.
    /// </param>
    public static InstallResult Apply(string dir, OptiProfile profile, int multiplier = 2, bool nativeFg = true)
    {
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path))
            return new InstallResult(false, Loc.T("opti.err.ini_missing", FileName));

        try
        {
            var lines = File.ReadAllLines(path).ToList();

            // Deux familles de fichiers, verifiees sur les paquets livres :
            //  - OptiScaler officiel 0.9.4 : pas de section [DLSSG] ; sorties fsrfg, xefg, nukems ;
            //    le MFG de XeFG est reserve aux cartes Arc (notes de version 0.9.4) : x2 ailleurs ;
            //  - forks DLSSNR : section [DLSSG] dont InterpolationCount compte les images
            //    intercalees (« 1 = 2X | 2 = 3X ... 5 = 6X »), pas le multiplicateur.
            var fork = HasSection(lines, "DLSSG");
            var wanted = new List<(string Section, string Key, string Value)>();

            switch (profile)
            {
                case OptiProfile.Dlss5Only:
                    // Pas de generation d'images : il ne reste que la passe neurale, reglee dans la
                    // surcouche en jeu (touche Inser). Sans OverlayMenu, OptiScaler coupe tout le FG.
                    wanted.Add(("FrameGen", "Enabled", "false"));
                    wanted.Add(("Menu", "OverlayMenu", "true"));
                    break;

                case OptiProfile.MfgOnly:
                    wanted.Add(("FrameGen", "Enabled", "true"));
                    wanted.Add(("Menu", "OverlayMenu", "true"));
                    if (fork)
                    {
                        // Notice du fork : InterpolationCount regle sa propre sortie DLSSG ;
                        // OverrideInterpolationCount (entier 0..6, Config.cpp) vise les appels
                        // Streamline natifs du jeu. Les deux comptent les images generees.
                        var generated = (Math.Clamp(multiplier, 2, 6) - 1).ToString();
                        wanted.Add(("DLSSG", nativeFg ? "OverrideInterpolationCount" : "InterpolationCount", generated));
                    }
                    else
                    {
                        // Par defaut FGInput/FGOutput valent « nofg » : sans eux, rien ne s'active.
                        wanted.Add(("FrameGen", "FGInput", nativeFg ? "dlssg" : "upscaler"));
                        wanted.Add(("FrameGen", "FGOutput", "fsrfg"));
                        if (!nativeFg) wanted.Add(("OptiFG", "HUDFix", "true"));
                    }
                    break;

                case OptiProfile.Full:
                    wanted.Add(("FrameGen", "Enabled", "auto"));
                    wanted.Add(("Menu", "OverlayMenu", "true"));
                    break;

                case OptiProfile.InjectedFg:
                    // Recette de docs/DLSS-FRAME-GENERATION.md du fork, cle pour cle.
                    wanted.Add(("FrameGen", "External", "false"));
                    wanted.Add(("FrameGen", "Enabled", "true"));
                    wanted.Add(("FrameGen", "FGInput", "upscaler"));
                    wanted.Add(("FrameGen", "FGOutput", "dlssg"));
                    wanted.Add(("FrameGen", "FGNvngxReplacement", "None"));
                    wanted.Add(("DLSSG", "InterpolationCount", (Math.Clamp(multiplier, 2, 6) - 1).ToString()));
                    wanted.Add(("DLSSG", "ForceDMFG", "false"));
                    wanted.Add(("Menu", "OverlayMenu", "true"));
                    break;
            }

            var applied = 0;

            foreach (var (section, key, value) in wanted)
                if (SetKey(lines, section, key, value)) applied++;

            File.WriteAllLines(path, lines);
            Log.Info(Src, $"{FileName}: profile {profile}, {applied} key(s) written in {dir}");

            var label = profile switch
            {
                OptiProfile.Dlss5Only => Loc.T("opti.profile.dlss5"),
                OptiProfile.MfgOnly => Loc.T("opti.profile.mfg", fork ? Math.Clamp(multiplier, 2, 6) : 2),
                OptiProfile.InjectedFg => Loc.T("opti.profile.mfg", Math.Clamp(multiplier, 2, 6)),
                _ => Loc.T("opti.profile.full")
            };
            return new InstallResult(true, Loc.T("opti.ok.config", label), applied);
        }
        catch (Exception ex)
        {
            Log.Error(Src, $"Cannot write {FileName}: {ex.Message}");
            return new InstallResult(false, Loc.T("err.config_failed", ex.Message));
        }
    }

    /// <summary>
    /// Pose une cle dans sa section. La section est creee si elle manque, la cle
    /// remplacee si elle existe deja — y compris quand elle est commentee.
    /// </summary>
    private static bool SetKey(List<string> lines, string section, string key, string value)
    {
        var header = $"[{section}]";
        var start = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));

        if (start < 0)
        {
            lines.Add("");
            lines.Add(header);
            lines.Add($"{key}={value}");
            return true;
        }

        // Fin de section : prochaine entete, ou fin de fichier.
        var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;

        for (var i = start + 1; i < end; i++)
        {
            var trimmed = lines[i].TrimStart().TrimStart(';').TrimStart();
            if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;

            var after = trimmed[key.Length..].TrimStart();
            if (!after.StartsWith('=')) continue;

            lines[i] = $"{key}={value}";
            return true;
        }

        // On remonte au-dessus des lignes vides qui separent les sections, sinon la
        // cle semble appartenir a la suivante.
        var insert = end;
        while (insert > start + 1 && string.IsNullOrWhiteSpace(lines[insert - 1])) insert--;

        lines.Insert(insert, $"{key}={value}");
        return true;
    }

    private static bool HasSection(List<string> lines, string section)
        => lines.Any(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));

    /// <summary>Lit la valeur courante d'une cle, pour afficher l'etat reel du profil.</summary>
    public static string? Read(string dir, string section, string key)
    {
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return null;

        try
        {
            var header = $"[{section}]";
            var inSection = false;

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inSection = line.Equals(header, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection || line.StartsWith(';')) continue;

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return line[(eq + 1)..].Trim();
            }
        }
        catch { /* fichier illisible : on ne sait pas, on ne pretend pas savoir */ }

        return null;
    }
}
