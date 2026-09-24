using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Mods qui ne doivent pas cohabiter dans un meme jeu. Seules des regles etablies :
///  - deux addons RenoDX DLSS : ShortFuse et DLSS5 Tool s'excluent (RHI ne pose jamais les deux) ;
///  - deux voies neurales a la fois (addon RenoDX DLSS + pont DLSS 5) : RHI n'en garde qu'une (nrMethod) ;
///  - deux ReShade charges : le jeu plante ou double ses effets ;
///  - deux OptiScaler charges : son propre script d'installation les traite en restes a supprimer ;
///  - des restes RHI (« .original », rhi_install.txt) : a nettoyer avant d'empiler autre chose.
/// </summary>
public static class ConflictService
{
    public static List<ModConflict> Evaluate(GameInfo game)
    {
        var dir = DllInstaller.TargetDirectory(game);
        var list = new List<ModConflict>();
        if (!Directory.Exists(dir)) return list;

        bool Has(string name) => File.Exists(Path.Combine(dir, name));

        var dlssAddons = Dlss5Addon.All.Where(a => Has(a.FileName)).ToList();
        if (dlssAddons.Count > 1)
            list.Add(new ModConflict("conflict-dlss-addons", UiStatus.Error, Loc.T("conflict.dlss_addons"),
                string.Join(" + ", dlssAddons.Select(a => a.FileName)), DiagnosisFix.Reinstall));

        var bridge = Files(dir, "dlss5-bridge*.addon64").FirstOrDefault();
        if (bridge is not null && dlssAddons.Count > 0)
            list.Add(new ModConflict("conflict-nr-double", UiStatus.Error, Loc.T("conflict.nr_double"),
                $"{dlssAddons[0].FileName} + {Path.GetFileName(bridge)}", DiagnosisFix.None));

        var reshade = ReShadeLocator.Scan(dir);
        if (reshade.IsDuplicate)
            list.Add(new ModConflict("conflict-reshade", UiStatus.Error, Loc.T("conflict.reshade_double"),
                string.Join(" + ", reshade.Active.Select(l => l.Name)), DiagnosisFix.Clean));

        // Charges par le jeu : un proxy qui est OptiScaler, ou le .asi d'un ASI Loader.
        var opti = ReShadeLocator.ProxyNames.Append("nvngx.dll")
            .Select(n => Path.Combine(dir, n))
            .Where(p => File.Exists(p) && LeftoverCleaner.IsOptiScaler(p))
            .Select(Path.GetFileName)
            .ToList();
        if (Has("OptiScaler.asi")) opti.Add("OptiScaler.asi");
        if (opti.Count > 1)
            list.Add(new ModConflict("conflict-optiscaler", UiStatus.Error, Loc.T("conflict.optiscaler_double"),
                string.Join(" + ", opti), DiagnosisFix.Clean));

        if (LeftoverCleaner.HasRhiTraces(game))
            list.Add(new ModConflict("leftovers-rhi", UiStatus.Warning, Loc.T("conflict.rhi_leftovers"),
                Loc.T("conflict.rhi_leftovers_ev"), DiagnosisFix.Clean));

        return list;
    }

    private static IEnumerable<string> Files(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }
}
