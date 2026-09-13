using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Juge la combinaison (DLSS-G, Streamline, addons charges) d'un titre.
///
/// Le tableau vient de la documentation de MFG Unlock. Deux enseignements y
/// comptent plus que les autres :
///
///  - le MFG dynamique exige la pile exacte 310.9.1 + Streamline 2.14.1, en D3D12,
///    avec un pilote 595.41 ou plus recent ;
///  - quand MFG Unlock et RenoDX DLSS5 sont charges ensemble, la combinaison
///    Streamline 2.14.0 + 310.9 a produit un effondrement des performances dans
///    STALKER 2 et Cyberpunk 2077, alors que 2.12.129 + 310.7.129 fonctionne.
///
/// Cette derniere ligne est un rapport d'utilisateur reproduit, pas une preuve que
/// 2.14 soit defectueux en soi : le verdict le dit tel quel.
/// </summary>
public static class CompatibilityMatrix
{
    /// <summary>Pile validee par l'addon pour le MFG dynamique.</summary>
    public const string DynamicDlssG = "310.9.1";
    public const string DynamicStreamline = "2.14.1";

    /// <summary>Combinaison rapportee comme fonctionnelle avec les deux addons charges.</summary>
    public const string KnownGoodDlssG = "310.7.129";
    public const string KnownGoodStreamline = "2.12.129";

    /// <summary>
    /// Ordonne une version en un entier comparable. Les trois premiers champs
    /// suffisent : Streamline ne distingue rien au-dela de la revision.
    /// </summary>
    private static long? Rank(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var parts = version.Split('.');
        long rank = 0;
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length || !int.TryParse(parts[i], out var n)) n = 0;
            rank = rank * 100000 + n;
        }
        return rank;
    }

    public static CompatVerdict Evaluate(GameInfo game, GpuInfo gpu)
    {
        var dlssG = game.Dlls.FirstOrDefault(d => d.Kind == DllKind.DlssG)?.Display;
        var sl = game.StreamlineVersion;
        var bothAddons = game.HasMfgUnlock && game.HasDlss5Addon;

        if (dlssG is null || sl is null)
            return new CompatVerdict
            {
                State = UiStatus.Idle,
                Title = Loc.T("compat.incomplete.title"),
                Detail = Loc.T(dlssG is null ? "compat.incomplete.no_dlssg" : "compat.incomplete.no_sl")
            };

        var dynamicStack = dlssG.StartsWith(DynamicDlssG, StringComparison.Ordinal)
                           && sl.StartsWith(DynamicStreamline, StringComparison.Ordinal);

        var knownGoodCombo = dlssG.StartsWith(KnownGoodDlssG, StringComparison.Ordinal)
                             && sl.StartsWith(KnownGoodStreamline, StringComparison.Ordinal);

        var sl214 = sl.StartsWith("2.14", StringComparison.Ordinal);
        var dlss3109 = dlssG.StartsWith("310.9", StringComparison.Ordinal);

        // Verdict prioritaire : sur la generation DLSS 3, il n'y a rien a debrider.
        if (dlssG.StartsWith("3.", StringComparison.Ordinal))
            return new CompatVerdict
            {
                State = UiStatus.Error,
                Title = Loc.T("compat.old_runtime.title"),
                Detail = Loc.T("compat.old_runtime.detail", dlssG),
                Suggestion = Loc.T("compat.old_runtime.fix")
            };

        // Streamline est le wrapper qui expose DLSS-G au moteur : un runtime 310.x
        // pose a cote d'un wrapper anterieur aux combinaisons validees ne rend pas
        // le multi-images accessible, quel que soit le debrideur charge. C'est le
        // cas le plus frequent apres un simple remplacement de nvngx_dlssg.dll.
        if (Rank(sl) is { } rank && rank < Rank(KnownGoodStreamline))
            return new CompatVerdict
            {
                State = UiStatus.Warning,
                Title = Loc.T("compat.old_sl.title"),
                Detail = Loc.T("compat.old_sl.detail", sl, dlssG, KnownGoodStreamline),
                Suggestion = Loc.T("compat.old_sl.fix", KnownGoodStreamline, DynamicStreamline)
            };

        // Le cas signale : les deux addons charges sur 2.14 + 310.9.
        if (bothAddons && sl214 && dlss3109 && !dynamicStack)
            return new CompatVerdict
            {
                State = UiStatus.Warning,
                Title = Loc.T("compat.clash.title"),
                Detail = Loc.T("compat.clash.detail", sl, dlssG),
                Suggestion = Loc.T("compat.clash.fix", KnownGoodStreamline, KnownGoodDlssG)
            };

        if (dynamicStack)
            return new CompatVerdict
            {
                State = UiStatus.Ready,
                Title = Loc.T("compat.dynamic.title"),
                Detail = Loc.T("compat.dynamic.detail", dlssG, sl)
                         + (bothAddons ? " " + Loc.T("compat.dynamic.both") : ""),
                DynamicMfgSupported = gpu.Generation == GpuGeneration.AdaLovelace
                                      || gpu.SupportsNativeMfg
            };

        if (knownGoodCombo)
            return new CompatVerdict
            {
                State = UiStatus.Ready,
                Title = Loc.T("compat.knowngood.title"),
                Detail = Loc.T("compat.knowngood.detail", sl, dlssG),
                Suggestion = Loc.T("compat.need_dynamic", DynamicDlssG, DynamicStreamline)
            };

        if (sl.StartsWith("2.12", StringComparison.Ordinal))
            return new CompatVerdict
            {
                State = UiStatus.Detected,
                Title = Loc.T("compat.legacy.title"),
                Detail = Loc.T("compat.legacy.detail", sl),
                Suggestion = Loc.T("compat.need_dynamic", DynamicDlssG, DynamicStreamline)
            };

        if (sl.StartsWith("2.13", StringComparison.Ordinal))
            return new CompatVerdict
            {
                State = UiStatus.Detected,
                Title = Loc.T("compat.mid.title"),
                Detail = Loc.T("compat.mid.detail", sl, dlssG),
                Suggestion = Loc.T("compat.mid.fix", DynamicDlssG, DynamicStreamline, KnownGoodDlssG, KnownGoodStreamline)
            };

        return new CompatVerdict
        {
            State = UiStatus.Detected,
            Title = Loc.T("compat.unlisted.title"),
            Detail = Loc.T("compat.unlisted.detail", dlssG, sl),
            Suggestion = Loc.T("compat.need_dynamic", DynamicDlssG, DynamicStreamline)
        };
    }
}
