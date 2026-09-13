namespace Prism.Models;

/// <summary>Verdict de compatibilite pour une combinaison de versions.</summary>
public sealed class CompatVerdict
{
    public required UiStatus State { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    /// <summary>Combinaison conseillee, quand la combinaison courante pose probleme.</summary>
    public string? Suggestion { get; init; }
    public bool DynamicMfgSupported { get; init; }
}
