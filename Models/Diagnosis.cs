using Prism.Core;

namespace Prism.Models;

/// <summary>Ce que le dernier lancement du jeu dit du rendu neural.</summary>
public enum DiagnosisVerdict
{
    /// <summary>Aucun addon RenoDX de rendu neural dans ce jeu.</summary>
    NotApplicable,
    /// <summary>Pas encore de quoi conclure : jeu jamais lance depuis l'installation, ou DLSS inactif.</summary>
    Unknown,
    /// <summary>La passe neurale s'execute, sans signal d'echec.</summary>
    Working,
    /// <summary>L'addon tourne mais l'effet est absent ou incertain.</summary>
    Degraded,
    /// <summary>La passe neurale ne s'execute pas.</summary>
    Failed
}

/// <summary>Correctif propose par une constatation. Liste fermee : un catalogue distant ne peut rien declencher d'autre.</summary>
public enum DiagnosisFix { None, Reinstall, ReShade, ShortFuse }

/// <summary>Une constatation : ce qui ne va pas, la ligne de journal qui le prouve, et quoi faire.</summary>
public sealed class DiagnosisFinding
{
    public required string Id { get; init; }
    public required UiStatus State { get; init; }
    public required string Title { get; init; }

    /// <summary>Ligne du journal a l'origine de la constatation, raccourcie.</summary>
    public string? Evidence { get; init; }

    public DiagnosisFix Fix { get; init; }

    public bool HasFix => Fix != DiagnosisFix.None;
    public bool HasEvidence => !string.IsNullOrEmpty(Evidence);

    public string FixLabel => Fix switch
    {
        DiagnosisFix.Reinstall => Loc.T("diag.fix.reinstall"),
        DiagnosisFix.ReShade => Loc.T("diag.fix.reshade"),
        DiagnosisFix.ShortFuse => Loc.T("diag.fix.shortfuse"),
        _ => ""
    };
}

/// <summary>Diagnostic d'un jeu : verdict, resume, constatations et journal lu.</summary>
public sealed class Diagnosis
{
    public DiagnosisVerdict Verdict { get; init; }
    public string Summary { get; init; } = "";
    public string? LogPath { get; init; }
    public DateTime? LogTime { get; init; }
    public IReadOnlyList<DiagnosisFinding> Findings { get; init; } = Array.Empty<DiagnosisFinding>();

    public static Diagnosis Empty { get; } = new() { Verdict = DiagnosisVerdict.NotApplicable };

    public bool Applies => Verdict != DiagnosisVerdict.NotApplicable;
    public bool HasFindings => Findings.Count > 0;
    public bool HasLog => LogPath is not null && File.Exists(LogPath);

    public UiStatus State => Verdict switch
    {
        DiagnosisVerdict.Working => UiStatus.Injected,
        DiagnosisVerdict.Degraded => UiStatus.Warning,
        DiagnosisVerdict.Failed => UiStatus.Error,
        DiagnosisVerdict.Unknown => UiStatus.Idle,
        _ => UiStatus.Disabled
    };

    public string VerdictLabel => Verdict switch
    {
        DiagnosisVerdict.Working => Loc.T("diag.verdict.working"),
        DiagnosisVerdict.Degraded => Loc.T("diag.verdict.degraded"),
        DiagnosisVerdict.Failed => Loc.T("diag.verdict.failed"),
        _ => Loc.T("diag.verdict.unknown")
    };

    public string LogLabel => LogTime is { } t ? Loc.T("diag.last_launch", t.ToString("g", Loc.I.Culture)) : "";
}
