using Prism.Core;
namespace Prism.Models;

public sealed class GpuInfo
{
    public string Name { get; init; } = "Unknown GPU";
    /// <summary>Version WDDM telle qu'inscrite au registre (32.0.16.1692).</summary>
    public string DriverVersion { get; init; } = "";
    /// <summary>Version commerciale NVIDIA correspondante (616.92), si deductible.</summary>
    public string? DriverBranch { get; init; }
    public GpuGeneration Generation { get; init; } = GpuGeneration.Unknown;
    public long VramBytes { get; init; }
    public string DirectXLevel { get; init; } = "DirectX 12";

    /// <summary>DLSS-G x2 signe par le pilote (Ada et plus).</summary>
    public bool SupportsNativeFg => Generation is GpuGeneration.AdaLovelace
        or GpuGeneration.Blackwell or GpuGeneration.NewerNvidia;

    /// <summary>MFG x3/x4+ officiel : verrouille sur Blackwell (flip metering materiel).</summary>
    public bool SupportsNativeMfg => Generation is GpuGeneration.Blackwell or GpuGeneration.NewerNvidia;

    /// <summary>Les tenseurs necessaires au modele transformer DLSS.</summary>
    public bool SupportsDlss => Generation is GpuGeneration.Turing or GpuGeneration.Ampere
        or GpuGeneration.AdaLovelace or GpuGeneration.Blackwell or GpuGeneration.NewerNvidia;

    public bool IsNvidia => Generation is not (GpuGeneration.NonNvidia or GpuGeneration.Unknown);

    public string Architecture => Generation switch
    {
        GpuGeneration.Turing => "Turing",
        GpuGeneration.Ampere => "Ampere",
        GpuGeneration.AdaLovelace => "Ada Lovelace",
        GpuGeneration.Blackwell => "Blackwell",
        GpuGeneration.NewerNvidia => "NVIDIA",
        GpuGeneration.NonNvidia => "Non-NVIDIA",
        _ => Loc.T("common.unknown")
    };

    public string SeriesLabel => Generation switch
    {
        GpuGeneration.Turing => "RTX 20",
        GpuGeneration.Ampere => "RTX 30",
        GpuGeneration.AdaLovelace => "RTX 40",
        GpuGeneration.Blackwell => "RTX 50",
        _ => ""
    };

    public string GenerationLabel =>
        string.IsNullOrEmpty(SeriesLabel) ? Architecture : $"{Architecture} · {SeriesLabel}";

    /// <summary>Multiplicateur maximal atteignable sans mod.</summary>
    public int NativeMaxMultiplier => SupportsNativeMfg ? 4 : SupportsNativeFg ? 2 : 1;
}

/// <summary>
/// Une option de frame generation proposee pour un couple (GPU, jeu).
/// L'interface classe les options par <see cref="Recommended"/> puis par risque.
/// </summary>
public sealed class FgOption
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required FgBackend Backend { get; init; }
    public required int MaxMultiplier { get; init; }

    /// <summary>Multiplicateurs selectionnables pour ce chemin.</summary>
    public int[] Multipliers { get; init; } = Array.Empty<int>();

    public bool Recommended { get; init; }
    public bool Experimental { get; init; }


    /// <summary>Vrai si aucun fichier du jeu n'est remplace (chargement en memoire).</summary>
    public bool NonDestructive { get; init; }

    /// <summary>Methode d'accrochage, affichee dans le panneau d'injection.</summary>
    public string Method { get; init; } = "—";

    /// <summary>
    /// Vrai si la voie execute le vrai DLSS-G de NVIDIA. Faux pour les voies qui
    /// reroutent la generation vers FSR 3.1 : le resultat n'est pas le meme.
    /// </summary>
    public bool Native { get; init; }

    /// <summary>Vrai si Prism sait telecharger et poser cette voie lui-meme.</summary>
    public bool AutoInstall { get; init; } = true;

    /// <summary>Page du projet, pour les voies que Prism ne peut pas installer.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>APIs de rendu couvertes par la voie.</summary>
    public GameApi[] Apis { get; init; } = { GameApi.DirectX12 };

    /// <summary>Prerequis de fichiers, evalues sur le titre selectionne.</summary>
    public IReadOnlyList<PrereqCheck> Requirements { get; init; } = Array.Empty<PrereqCheck>();

    /// <summary>Vrai si un prerequis bloquant n'est pas satisfait.</summary>
    public bool HasUnmetRequirements => Requirements.Any(r => r.Blocking);

    /// <summary>Raison pour laquelle l'option est indisponible, ou null.</summary>
    public string? BlockedReason { get; init; }
    public bool Available => BlockedReason is null;

    public string MultiplierLabel => MaxMultiplier <= 1 ? "—" : $"×{MaxMultiplier}";

    public string RiskLabel => Loc.T(NonDestructive ? "fg.risk.nondestructive" : Experimental ? "fg.risk.experimental" : "fg.risk.official");

    public string KindLabel => Native ? Loc.T("fg.kind.native") : "FSR 3.1";
}
