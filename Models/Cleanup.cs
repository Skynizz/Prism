using Prism.Core;

namespace Prism.Models;

/// <summary>Ce que le nettoyage fera d'un fichier.</summary>
public enum CleanAction
{
    /// <summary>Fichier de mod : mis de cote puis retire.</summary>
    Remove,
    /// <summary>« X.original » non vide : l'original du jeu reprend sa place, la copie posee est mise de cote.</summary>
    RestoreOriginal,
    /// <summary>« X.original » vide : l'outil a cree X de toutes pieces ; X et le temoin sont retires.</summary>
    DropSentinel
}

/// <summary>Une ligne du plan de nettoyage : un fichier, ce qui lui arrivera, et d'ou il vient.</summary>
public sealed class CleanItem
{
    public required string Path { get; init; }
    public required CleanAction Action { get; init; }

    /// <summary>Outil ou mod d'origine : « RHI », « OptiScaler », « ReShade »...</summary>
    public required string Source { get; init; }

    /// <summary>Chemin affiche, relatif au dossier du jeu.</summary>
    public string Display { get; init; } = "";

    public string ActionLabel => Action switch
    {
        CleanAction.RestoreOriginal => Loc.T("clean.act.restore"),
        CleanAction.DropSentinel => Loc.T("clean.act.sentinel"),
        _ => Loc.T("clean.act.remove")
    };

    public UiStatus State => Action == CleanAction.RestoreOriginal ? UiStatus.Warning : UiStatus.Detected;
}

/// <summary>Plan complet, montre fichier par fichier avant toute ecriture.</summary>
public sealed class CleanPlan
{
    public static readonly CleanPlan Empty = new() { Items = Array.Empty<CleanItem>() };

    public required IReadOnlyList<CleanItem> Items { get; init; }

    public bool IsEmpty => Items.Count == 0;

    public string Summary => IsEmpty
        ? Loc.T("clean.none")
        : Loc.T("clean.summary", Items.Count,
            string.Join(" · ", Items.GroupBy(i => i.Source).Select(g => $"{g.Key} {g.Count()}")));
}

/// <summary>Un fichier touche par un nettoyage, et de quoi le remettre.</summary>
public sealed class CleanRecord
{
    public string Path { get; set; } = "";
    public CleanAction Action { get; set; }
    /// <summary>Copie mise de cote du fichier retire ; null si rien n'existait.</summary>
    public string? Stored { get; set; }
}

/// <summary>Un nettoyage passe, annulable tant que son dossier existe.</summary>
public sealed class CleanSession
{
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public List<CleanRecord> Records { get; set; } = new();
}

/// <summary>Deux mods qui ne doivent pas cohabiter, ou des restes a nettoyer.</summary>
public sealed record ModConflict(string Id, UiStatus State, string Title, string Evidence, DiagnosisFix Fix);

/// <summary>Jeu ajoute a la main par son executable.</summary>
public sealed class ManualGame
{
    public string Name { get; set; } = "";
    public string Executable { get; set; } = "";
}
