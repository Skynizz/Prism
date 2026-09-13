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
    /// <summary>Addon RenoDX DLSS5 servi par un paquet local, avec sa pile appariee.</summary>
    RenoDxDlss5
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
