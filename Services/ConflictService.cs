using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Mods qui ne doivent pas cohabiter dans un meme jeu. Seules des regles etablies :
///  - deux addons RenoDX DLSS : ShortFuse et DLSS5 Tool s'excluent ;
///  - deux voies neurales a la fois (addon RenoDX DLSS + pont DLSS 5) : une seule a la fois ;
///  - deux ReShade charges : le jeu plante ou double ses effets ;
///  - deux OptiScaler charges : son propre script d'installation les traite en restes a supprimer ;
///  - des restes d'un autre installeur (« .original », manifeste) : a nettoyer avant d'empiler autre chose.
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

        // Nom reserve a un chargeur de mods du jeu (Cyberpunk : winmm.dll pour RED4ext, version.dll pour CET).
        foreach (var name in ModRules.ReservedProxies(dir))
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            var who = LeftoverCleaner.IsOptiScaler(path) ? "OptiScaler"
                : ReShadeLocator.Scan(dir).Active.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? "ReShade" : null;
            if (who is not null)
                list.Add(new ModConflict("conflict-reserved-" + name, UiStatus.Warning, Loc.T("conflict.reserved", who, name),
                    Loc.T("conflict.reserved_ev", name), who == "OptiScaler" ? DiagnosisFix.MoveProxy : DiagnosisFix.None));
        }

        // OptiScaler sur d3d12.dll alors qu'un nom plus sur est libre : il s'insere dans le demarrage
        // de Direct3D (fabrique DXGI en echec constatee avec ReShade sur dxgi.dll, Cyberpunk 2077).
        var d3d12 = Path.Combine(dir, "d3d12.dll");
        if (File.Exists(d3d12) && LeftoverCleaner.IsOptiScaler(d3d12) &&
            FrameGenService.PickProxyName(dir) is { } better && better != "d3d12.dll")
            list.Add(new ModConflict("conflict-opti-d3d12", UiStatus.Warning, Loc.T("conflict.opti_d3d12"),
                Loc.T("conflict.opti_d3d12_ev", better), DiagnosisFix.MoveProxy));

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
