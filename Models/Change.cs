using Prism.Core;
namespace Prism.Models;

/// <summary>Nature d'une modification apportee par Prism a un titre.</summary>
public enum ChangeKind
{
    /// <summary>Un fichier d'origine a ete remplace ; l'original est en sauvegarde.</summary>
    Replaced,
    /// <summary>Un fichier absent a ete ajoute ; il suffit de le retirer.</summary>
    Added
}

/// <summary>
/// Une ligne du registre des modifications. Elle unifie les deux mecanismes —
/// sauvegarde d'un original remplace, et trace d'un fichier ajoute — pour que tout
/// ce que Prism a touche depuis le debut se lise au meme endroit et se defasse d'un
/// seul geste.
/// </summary>
public sealed class ChangeEntry
{
    public required ChangeKind Kind { get; init; }
    public required string GameId { get; init; }
    public required string GameName { get; init; }
    public required string Path { get; init; }
    /// <summary>Composant a l'origine de l'ecriture : "DLSS SR", "MFGAdaUnlock", "RenoDX"...</summary>
    public required string Component { get; init; }
    /// <summary>Installation d'ou vient la modification : c'est l'unite que l'on retire d'un clic.</summary>
    public required string Origin { get; init; }
    /// <summary>Vrai si l'origine est deduite du nom : entree anterieure au suivi des installations.</summary>
    public bool OriginInferred { get; init; }
    public string? Version { get; init; }
    public DateTimeOffset At { get; init; }
    /// <summary>Copie de l'original, pour un fichier remplace.</summary>
    public string? BackupPath { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);
    public bool StillApplies { get; init; }

    public string KindLabel => Loc.T(Kind == ChangeKind.Replaced ? "change.replaced" : "change.added");

    /// <summary>Ce que fera le bouton : restaurer l'original, ou retirer le fichier.</summary>
    public string ActionLabel => Loc.T(Kind == ChangeKind.Replaced ? "btn.restore" : "btn.remove");

    public UiStatus State => StillApplies
        ? Kind == ChangeKind.Replaced ? UiStatus.Warning : UiStatus.Injected
        : UiStatus.Idle;
}

/// <summary>
/// Une installation encore en place dans un jeu — le paquet DLSS 5, le mod HDR,
/// ReShade — avec tout ce qu'elle a pose. C'est ce que l'utilisateur voit en revenant
/// sur un jeu, et ce qu'il retire d'un seul clic.
/// </summary>
public sealed class InstalledGroup
{
    public required string Origin { get; init; }
    public string? Version { get; init; }
    public DateTimeOffset At { get; init; }
    public string Summary { get; init; } = "";
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    public string FilesLabel => string.Join(Environment.NewLine, Files);
    public string When => At == default ? "" : At.ToLocalTime().ToString("g", Loc.I.Culture);
}

/// <summary>
/// Un mod present dans le jeu sans que Prism l'y ait pose : ReShade, addon, OptiScaler,
/// runtime NVIDIA remplace a la main... Ce qui n'appartient pas a la version d'origine.
/// </summary>
public sealed class DetectedMod
{
    public required string Label { get; init; }
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Certain : fichier propre a un mod, ou DLL proxy qui s'en reclame. Sinon deduit
    /// des dates — un runtime cree bien apres le jeu peut aussi venir d'une mise a jour.
    /// </summary>
    public bool Certain { get; init; }

    /// <summary>Vrai si les fichiers peuvent etre mis de cote : un ajout, pas un original remplace.</summary>
    public bool Removable { get; init; }

    public string Reason { get; init; } = "";

    public string Summary => Certain ? Reason : $"{Reason} · {Loc.T("detected.probable")}";
    public string FilesLabel => string.Join(Environment.NewLine, Files.Select(System.IO.Path.GetFileName));
}

/// <summary>
/// Prerequis que Prism sait resoudre lui-meme. L'identifiant est porte par la ligne
/// de controle, ce qui permet a l'interface d'offrir le correctif sur place.
/// </summary>
public enum PrereqFix
{
    None,
    /// <summary>Deployer nvngx_dlss.dll depuis le catalogue signe.</summary>
    DeployDlss,
    /// <summary>Deployer nvngx_dlssg.dll dans la version exigee.</summary>
    DeployDlssG,
    /// <summary>Deployer le paquet Streamline apparie.</summary>
    DeployStreamline,
    /// <summary>Installer ReShade en variante Addon.</summary>
    InstallReShade,
    /// <summary>Copier le runtime neural depuis le pilote installe.</summary>
    AdoptNeuralRuntime,
    /// <summary>Telecharger et poser renodx-mfgunlock.addon64.</summary>
    InstallMfgAddon,
    /// <summary>Poser RenoDX DLSS 5 et sa pile runtime complete, verifiee.</summary>
    InstallDlss5Addon
}

/// <summary>Etendue de la configuration ecrite dans OptiScaler.ini.</summary>
public enum OptiProfile
{
    /// <summary>Uniquement la passe Neural Rendering : ni upscaler, ni generation d'images.</summary>
    Dlss5Only,
    /// <summary>Uniquement la generation d'images multi-images.</summary>
    MfgOnly,
    /// <summary>Tout ce que le paquet sait faire.</summary>
    Full
}
