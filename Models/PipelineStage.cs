namespace Prism.Models;

/// <summary>
/// Un maillon de la chaine graphique, du jeu jusqu'a l'ecran. La vue en fait une
/// representation de pipeline : chaque etage est soit traverse, soit inactif.
/// </summary>
public sealed class PipelineStage
{
    public required string Name { get; init; }
    /// <summary>Valeur technique : version, mode, multiplicateur.</summary>
    public string Detail { get; init; } = "—";
    public UiStatus State { get; init; } = UiStatus.Idle;
    /// <summary>Faux pour un etage present mais non emprunte par le rendu.</summary>
    public bool Engaged { get; init; }
    public bool IsFirst { get; init; }

    public double NodeOpacity => Engaged ? 1.0 : 0.42;
}
