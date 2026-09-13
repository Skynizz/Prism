namespace Prism.Models;

/// <summary>Famille d'un mod RenoDX, telle que la page Mods du wiki la range.</summary>
public enum HdrModKind
{
    /// <summary>Mod dedie a un jeu, liste principale.</summary>
    Game,
    /// <summary>UE Extended : le mod generique Unreal recommande.</summary>
    UeExtended,
    /// <summary>Ancien mod generique Unreal, conserve pour les titres valides avec lui.</summary>
    UnrealLegacy,
    /// <summary>Mod generique Unity.</summary>
    Unity
}

public enum HdrModStatus { Unknown, Working, InProgress }

/// <summary>Une ligne de la page Mods du wiki RenoDX.</summary>
public sealed class HdrWikiEntry
{
    public required string Name { get; init; }
    public required HdrModKind Kind { get; init; }
    public string Maintainer { get; init; } = "";
    public HdrModStatus Status { get; init; }

    /// <summary>Note de la ligne : infobulle du statut, ou colonne Notes des tables moteur.</summary>
    public string? Note { get; init; }

    public string? Url64 { get; init; }
    public string? Url32 { get; init; }
    public string? NexusUrl { get; init; }
    public string? ThreadUrl { get; init; }

    /// <summary>Nom normalise, pour l'appariement.</summary>
    public string Key { get; init; } = "";
}

/// <summary>Nature d'une etape du plan HDR.</summary>
public enum HdrStepKind
{
    /// <summary>Telecharger et poser l'addon.</summary>
    Addon,
    /// <summary>Ecrire une cle RenoDX dans ReShade.ini.</summary>
    ReShadeKey,
    /// <summary>Ecrire des cles dans l'Engine.ini du jeu Unreal.</summary>
    EngineIni,
    /// <summary>Prerequis verifie par Prism (ReShade, architecture).</summary>
    Requirement,
    /// <summary>A faire soi-meme dans le jeu : Prism ne peut pas le faire a votre place.</summary>
    Manual
}

/// <summary>Une etape du plan : ce qui sera fait, et si Prism le fait lui-meme.</summary>
public sealed class HdrStep
{
    public required HdrStepKind Kind { get; init; }

    /// <summary>Texte court et traduit.</summary>
    public required string Text { get; init; }

    /// <summary>Texte d'origine du wiki, montre tel quel quand il ne se traduit pas en action.</summary>
    public string? WikiText { get; init; }

    public string? Key { get; init; }
    public string? Value { get; init; }

    /// <summary>Cles Engine.ini : section, cle, valeur.</summary>
    public IReadOnlyList<(string Section, string Key, string Value)> IniLines { get; init; }
        = Array.Empty<(string, string, string)>();

    public UiStatus State { get; set; } = UiStatus.Idle;

    public bool Automatic => Kind is HdrStepKind.Addon or HdrStepKind.ReShadeKey or HdrStepKind.EngineIni;
}

/// <summary>
/// Ce que Prism fera pour donner un vrai HDR a un titre : quel addon, depuis quelle
/// ligne du wiki, et chaque reglage ou fichier que la ligne exige.
/// </summary>
public sealed class HdrPlan
{
    public required HdrModKind Kind { get; init; }

    /// <summary>Ligne du wiki qui justifie le plan ; null pour un mod generique non liste.</summary>
    public HdrWikiEntry? Entry { get; init; }

    /// <summary>Pourquoi ce mod : AppID Steam, nom, moteur.</summary>
    public required string MatchReason { get; init; }

    public string? AddonUrl { get; init; }
    public string? AddonFileName { get; init; }
    public bool Is32Bit { get; init; }

    /// <summary>Page a ouvrir quand le mod n'est distribue que sur Nexus.</summary>
    public string? ExternalUrl { get; init; }

    public List<HdrStep> Steps { get; } = new();

    public string? BlockedReason { get; set; }

    public bool CanInstall => AddonUrl is not null && BlockedReason is null;

    public string Title => Entry?.Name ?? Kind.ToString();

    public HdrModStatus Status => Entry?.Status ?? HdrModStatus.Unknown;
}
