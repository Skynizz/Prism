namespace Prism.Models;

/// <summary>Implementation de Neural Rendering proposee pour un titre.</summary>
public enum Dlss5Backend
{
    /// <summary>OptiScaler + passe DLSSNR — la voie DirectX 12.</summary>
    OptiScalerNr,
    /// <summary>Fork multipass PreSR d'OptiScaler — DirectX 12, reglages plus fins.</summary>
    OptiScalerMultipass,
    /// <summary>Installeur unique couvrant DX11 et DX12, avec ou sans DLSS natif.</summary>
    OneClick,
    /// <summary>Pont ReShade — utile uniquement en DirectX 11 et Vulkan.</summary>
    Bridge,
    /// <summary>Addon RenoDX « DLSS5 Tool », avec sa pile appariee.</summary>
    RenoDxDlss5,
    /// <summary>
    /// Addon « DLSS Tool » de ShortFuse, l'auteur de RenoDX, avec la pile complete. C'est la
    /// methode que RHI recommande pour la plupart des jeux ayant DLSS.
    /// </summary>
    ShortFuse
}

/// <summary>
/// Variante de l'addon RenoDX de rendu neural. Les deux s'excluent : un seul addon neural par
/// jeu, sinon ils se disputent la meme evaluation NGX.
/// </summary>
public sealed record Dlss5Addon(string TagPrefix, string FileName, string Origin, string Label)
{
    /// <summary>« DLSS5 Tool » : renodx-dlss5.addon64, tags renodx-dlss5-x.y.z.</summary>
    public static readonly Dlss5Addon Tool =
        new("renodx-dlss5-", "renodx-dlss5.addon64", "RenoDX DLSS 5", "RenoDX DLSS 5");

    /// <summary>ShortFuse : renodx-dlss.addon64, tags renodx-dlss-SF-aa.mmjj.hhmm.</summary>
    public static readonly Dlss5Addon ShortFuse =
        new("renodx-dlss-SF-", "renodx-dlss.addon64", "RenoDX DLSS ShortFuse", "RenoDX DLSS · ShortFuse");

    public static readonly Dlss5Addon[] All = { Tool, ShortFuse };

    /// <summary>L'autre variante, a retirer quand celle-ci est posee.</summary>
    public Dlss5Addon Other => this == Tool ? ShortFuse : Tool;

    /// <summary>Variante posee par une voie DLSS 5, ou null si la voie n'utilise pas d'addon RenoDX.</summary>
    public static Dlss5Addon? For(Dlss5Backend? backend) => backend switch
    {
        Dlss5Backend.RenoDxDlss5 => Tool,
        Dlss5Backend.ShortFuse => ShortFuse,
        _ => null
    };
}

/// <summary>
/// Une voie DLSS 5. Comme pour la generation d'images, chaque voie porte les API
/// qu'elle couvre : le pont ReShade ne sert a rien sur un titre DirectX 12, ou
/// OptiScaler fait le travail nativement.
/// </summary>
public sealed class Dlss5Option
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required Dlss5Backend Backend { get; init; }
    public required GameApi[] Apis { get; init; }

    public string? SourceUrl { get; init; }
    public string? Version { get; init; }

    /// <summary>Vrai si Prism sait telecharger et poser cette voie lui-meme.</summary>
    public bool AutoInstall { get; init; }

    /// <summary>Vrai si aucun fichier du jeu n'est remplace.</summary>
    public bool NonDestructive { get; init; }

    public bool Recommended { get; init; }

    /// <summary>Raison d'indisponibilite, ou null.</summary>
    public string? BlockedReason { get; init; }
    public bool Available => BlockedReason is null;

    public string ApiLabel => string.Join(" · ", Apis.Select(a => a.Short()));
}

/// <summary>Etat global du chemin DLSS 5 pour un titre.</summary>
public enum Dlss5Readiness
{
    /// <summary>Le runtime neural et une voie sont en place.</summary>
    Active,
    /// <summary>Installable : il ne manque que ce que Prism sait poser.</summary>
    Ready,
    /// <summary>Il manque un element que Prism ne peut pas fournir de facon sure.</summary>
    Incomplete,
    /// <summary>Le materiel ou le titre ne s'y prete pas.</summary>
    Unsupported
}
