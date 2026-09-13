using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Reglages de MFG Unlock, tels que l'addon les lit dans ReShade.ini sous
/// <c>[RenoDX.MFGUnlock]</c>. Les noms et les valeurs proviennent de la
/// documentation de l'addon ; aucune n'est inventee.
/// </summary>
public sealed class MfgSettings
{
    /// <summary>0 respecte le choix du jeu ; 2 a 6 imposent ce multiplicateur exact.</summary>
    public int ForceMultiplier { get; set; }

    /// <summary>Plafond annonce au runtime via DLSSG.MultiFrameCountMax.</summary>
    public int MaxCount { get; set; } = 4;

    /// <summary>MFG dynamique natif — exige la pile 310.9.1 + Streamline 2.14.1 en D3D12.</summary>
    public bool DynamicMfg { get; set; }

    /// <summary>Cible du mode dynamique ; 0 suit la frequence de l'ecran.</summary>
    public int DynamicTargetFps { get; set; }

    /// <summary>0 laisse la politique du jeu, 1 privilegie les fichiers locaux, 2 force l'OTA.</summary>
    public int RuntimeSelectionMode { get; set; }

    /// <summary>0 Native, 1 UI Composition forcee, 2 Automatic Guard, 3 Final Color Fallback.</summary>
    public int HdrCompatibilityMode { get; set; }

    /// <summary>Ne l'activer que si 3x/4x gele : bascule sur le pacing logiciel historique.</summary>
    public bool ForceFlipMeteringOff { get; set; }

    /// <summary>Casse certains jeux : laisse a faux sauf besoin explicite.</summary>
    public bool RaiseFrameCeiling { get; set; }

    public string HdrModeLabel => HdrCompatibilityMode switch
    {
        1 => Loc.T("mfg.hdr.1"),
        2 => Loc.T("mfg.hdr.2"),
        3 => Loc.T("mfg.hdr.3"),
        _ => Loc.T("mfg.hdr.0")
    };

    public string RuntimeModeLabel => RuntimeSelectionMode switch
    {
        1 => Loc.T("mfg.runtime.1"),
        2 => Loc.T("mfg.runtime.2"),
        _ => Loc.T("mfg.runtime.0")
    };

    public string MultiplierLabel => ForceMultiplier == 0 ? Loc.T("mfg.mult.game") : Loc.T("mfg.mult.forced", ForceMultiplier);
}

/// <summary>
/// Lecture et ecriture de ReShade.ini.
///
/// Deux besoins distincts :
///  - configurer MFG Unlock sans passer par la surcouche en jeu ;
///  - inscrire un addon dans <c>[ADDON] LoadFromDllMain</c>, indispensable pour
///    RenoDX DLSS5 et pour les titres qui appellent slInit avant que ReShade n'ait
///    fait son balayage d'addons — Cyberpunk 2077 en fait partie.
/// </summary>
public static class ReShadeConfig
{
    private const string Src = "reshade";
    private const string FileName = "ReShade.ini";
    private const string MfgSection = "RenoDX.MFGUnlock";

    public static string PathFor(string dir) => Path.Combine(dir, FileName);
    public static bool Exists(string dir) => File.Exists(PathFor(dir));

    // ----------------------------------------------------------- MFG Unlock

    public static MfgSettings ReadMfg(string dir)
    {
        var s = new MfgSettings();
        var path = PathFor(dir);
        if (!File.Exists(path)) return s;

        try
        {
            var values = ReadSection(path, MfgSection);

            s.ForceMultiplier = Int(values, "ForceMultiplier", 0);
            s.MaxCount = Int(values, "MaxCount", 4);
            s.DynamicMfg = Int(values, "DynamicMFG", 0) != 0;
            s.DynamicTargetFps = Int(values, "DynamicTargetFPS", 0);
            s.RuntimeSelectionMode = Int(values, "RuntimeSelectionMode", 0);
            s.HdrCompatibilityMode = Int(values, "HDRCompatibilityMode", 0);
            s.ForceFlipMeteringOff = Int(values, "ForceFlipMeteringOff", 0) != 0;
            s.RaiseFrameCeiling = Int(values, "RaiseFrameCeiling", 0) != 0;
        }
        catch (Exception ex) { Log.Warn(Src, $"Lecture de {FileName} impossible : {ex.Message}"); }

        return s;
    }

    /// <summary>
    /// Ecrit les reglages de MFG Unlock. L'addon relit son fichier au demarrage :
    /// un changement n'est effectif qu'apres un redemarrage complet du jeu.
    /// </summary>
    public static InstallResult WriteMfg(string dir, MfgSettings s)
    {
        var path = PathFor(dir);
        if (!File.Exists(path) && !ReShadeLocator.Scan(dir).Present)
            return new InstallResult(false, Loc.T("reshade.err.ini_missing", FileName));

        var keys = new (string Key, string Value)[]
        {
            ("Enabled", "1"),
            ("ForceMultiplier", s.ForceMultiplier.ToString()),
            ("MaxCount", Math.Clamp(s.MaxCount, 2, 6).ToString()),
            ("DynamicMFG", s.DynamicMfg ? "1" : "0"),
            ("DynamicTargetFPS", s.DynamicTargetFps.ToString()),
            ("RuntimeSelectionMode", s.RuntimeSelectionMode.ToString()),
            ("HDRCompatibilityMode", s.HdrCompatibilityMode.ToString()),
            ("ForceFlipMeteringOff", s.ForceFlipMeteringOff ? "1" : "0"),
            ("RaiseFrameCeiling", s.RaiseFrameCeiling ? "1" : "0")
        };

        try
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            foreach (var (key, value) in keys) SetKey(lines, MfgSection, key, value);
            DllInstaller.ClearReadOnly(path);
            File.WriteAllLines(path, lines);

            var msg = Loc.T("mfg.ok", s.MultiplierLabel, s.HdrModeLabel, s.RuntimeModeLabel);
            Log.Info(Src, $"{msg} ({dir})");
            return new InstallResult(true, msg + " " + Loc.T("common.restart_game"), keys.Length);
        }
        catch (Exception ex)
        {
            Log.Error(Src, $"Ecriture de {FileName} impossible : {ex.Message}");
            return new InstallResult(false, Loc.T("err.config_failed", ex.Message));
        }
    }

    // -------------------------------------------------- Chargement precoce

    /// <summary>
    /// Ajoute un addon a <c>[ADDON] LoadFromDllMain</c>. RenoDX DLSS5 l'exige, et
    /// certains titres appellent slInit avant le balayage normal de ReShade.
    /// </summary>
    public static InstallResult EnableEarlyLoading(string dir, string addonFileName)
    {
        var path = PathFor(dir);
        // ReShade complete lui-meme un fichier incomplet : l'absence de ReShade.ini ne doit
        // pas empecher d'inscrire l'addon, a condition que ReShade soit bien la.
        if (!File.Exists(path) && !ReShadeLocator.Scan(dir).Present)
            return new InstallResult(false, Loc.T("reshade.err.ini_missing", FileName));

        try
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            var current = ReadKey(lines, "ADDON", "LoadFromDllMain") ?? "";

            var entries = current
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (entries.Any(e => e.Equals(addonFileName, StringComparison.OrdinalIgnoreCase)))
                return new InstallResult(true, Loc.T("early.already", addonFileName), 0);

            entries.Add(addonFileName);
            SetKey(lines, "ADDON", "LoadFromDllMain", string.Join(",", entries));
            DllInstaller.ClearReadOnly(path);
            File.WriteAllLines(path, lines);

            Log.Info(Src, $"Chargement precoce active pour {addonFileName} dans {dir}");
            return new InstallResult(true,
                Loc.T("early.ok", addonFileName), 1);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, Loc.T("err.modify_failed", ex.Message));
        }
    }

    public static bool IsEarlyLoaded(string dir, string addonFileName)
    {
        var path = PathFor(dir);
        if (!File.Exists(path)) return false;

        try
        {
            var value = ReadKey(File.ReadAllLines(path).ToList(), "ADDON", "LoadFromDllMain") ?? "";
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(e => e.Equals(addonFileName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// Retire les traces laissees par Prism : la section de l'addon MFG et les
    /// inscriptions de chargement precoce. Le reste du fichier appartient a
    /// l'utilisateur et n'est pas touche.
    /// </summary>
    public static int CleanUp(string dir, IEnumerable<string> addonNames, bool removeMfgSection = true)
    {
        var path = PathFor(dir);
        if (!File.Exists(path)) return 0;

        try
        {
            var lines = File.ReadAllLines(path).ToList();
            var removed = 0;

            // Section complete de l'addon MFG — seulement si c'est lui qu'on retire.
            var start = !removeMfgSection ? -1
                : lines.FindIndex(l => l.Trim().Equals($"[{MfgSection}]", StringComparison.OrdinalIgnoreCase));
            if (start >= 0)
            {
                var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
                if (end < 0) end = lines.Count;
                lines.RemoveRange(start, end - start);
                removed++;
            }

            // Inscriptions de chargement precoce.
            var early = ReadKey(lines, "ADDON", "LoadFromDllMain");
            if (early is not null)
            {
                var kept = early
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(e => !addonNames.Any(a => e.Equals(a, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (kept.Count != early.Split(',', StringSplitOptions.RemoveEmptyEntries).Length)
                {
                    // Une liste vide n'a pas a subsister : on retire la cle entiere.
                    if (kept.Count == 0) RemoveKey(lines, "ADDON", "LoadFromDllMain");
                    else SetKey(lines, "ADDON", "LoadFromDllMain", string.Join(",", kept));
                    removed++;
                }
            }

            if (removed > 0)
            {
                File.WriteAllLines(path, lines);
                Log.Info(Src, $"{FileName} nettoye dans {dir} ({removed} entree(s))");
            }
            return removed;
        }
        catch (Exception ex)
        {
            Log.Warn(Src, $"Nettoyage de {FileName} impossible : {ex.Message}");
            return 0;
        }
    }

    // ------------------------------------------------------------ Primitives

    private static Dictionary<string, string> ReadSection(string path, string section)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            if (!inSection || line.Length == 0 || line.StartsWith(';')) continue;

            var eq = line.IndexOf('=');
            if (eq > 0) values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        return values;
    }

    private static string? ReadKey(List<string> lines, string section, string key)
    {
        var header = $"[{section}]";
        var start = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;

        var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;

        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim();
        }
        return null;
    }

    private static void SetKey(List<string> lines, string section, string key, string value)
    {
        var header = $"[{section}]";
        var start = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));

        if (start < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add(header);
            lines.Add($"{key}={value}");
            return;
        }

        var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;

        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return;
            }
        }

        var insert = end;
        while (insert > start + 1 && string.IsNullOrWhiteSpace(lines[insert - 1])) insert--;
        lines.Insert(insert, $"{key}={value}");
    }

    private static void RemoveKey(List<string> lines, string section, string key)
    {
        var header = $"[{section}]";
        var start = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return;

        var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;

        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(i);
                return;
            }
        }
    }

    private static int Int(Dictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out var v) && int.TryParse(v, out var parsed) ? parsed : fallback;
}
