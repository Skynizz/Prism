using Prism.Core;

namespace Prism.Models;

/// <summary>Role d'un composant dans la chaine graphique, pour le regroupement.</summary>
public enum ComponentRole { Runtime, FrameGen, Rendering, PostProcess }

/// <summary>Un composant tiers suivi par Prism, avec sa provenance.</summary>
public sealed class ModComponent : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Cle de traduction de la description.</summary>
    public string? DescriptionKey { get; init; }
    public string Description => DescriptionKey is null ? "" : Loc.T(DescriptionKey);
    public required string HomeUrl { get; init; }
    public ComponentRole Role { get; init; } = ComponentRole.Rendering;

    /// <summary>"owner/repo" pour GitHub, ou null si la source est ailleurs.</summary>
    public string? GitHubRepo { get; init; }

    /// <summary>Architecture ciblee, affichee dans le gestionnaire.</summary>
    public string Architecture { get; init; } = "x64";

    /// <summary>Vrai si aucun fichier du jeu n'est remplace par ce composant.</summary>
    public bool NonDestructive { get; init; }

    /// <summary>Note de confiance redigee a partir de la verification des sources.</summary>
    public string? TrustNoteKey { get; init; }
    public string? TrustNote => TrustNoteKey is null ? null : Loc.T(TrustNoteKey);

    /// <summary>Apres un changement de langue.</summary>
    public void Relocalize()
    {
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(TrustNote));
        OnPropertyChanged(nameof(FreshnessLabel));
        OnPropertyChanged(nameof(StatusLabel));
    }

    // ------------------------------------------------------------ Provenance

    private int _stars;
    public int Stars { get => _stars; set { Set(ref _stars, value); OnPropertyChanged(nameof(StarsLabel)); } }

    public string StarsLabel => Stars > 0 ? $"{Stars:N0} ★" : "—";

    private string? _license;
    public string? License { get => _license; set => Set(ref _license, value); }

    private DateTimeOffset? _lastCommit;
    public DateTimeOffset? LastCommit
    {
        get => _lastCommit;
        set { Set(ref _lastCommit, value); OnPropertyChanged(nameof(FreshnessLabel)); OnPropertyChanged(nameof(IsStale)); }
    }

    /// <summary>Au-dela de trois mois sans commit, la source merite un signalement.</summary>
    public bool IsStale => LastCommit is { } d && (DateTimeOffset.Now - d).TotalDays > 90;

    public string FreshnessLabel
    {
        get
        {
            if (LastCommit is not { } d) return "—";
            var days = (int)(DateTimeOffset.Now - d).TotalDays;
            return days switch
            {
                <= 0 => Loc.T("comp.fresh.today"),
                1 => Loc.T("comp.fresh.yesterday"),
                < 30 => Loc.T("comp.fresh.days", days),
                < 365 => Loc.T("comp.fresh.months", days / 30),
                _ => Loc.T("comp.fresh.years", days / 365)
            };
        }
    }

    // ----------------------------------------------------------------- Etat

    private string? _latestVersion;
    public string? LatestVersion
    {
        get => _latestVersion;
        set { Set(ref _latestVersion, value); OnPropertyChanged(nameof(StatusLabel)); OnPropertyChanged(nameof(State)); }
    }

    private DateTimeOffset? _releasedAt;
    public DateTimeOffset? ReleasedAt { get => _releasedAt; set => Set(ref _releasedAt, value); }

    private string? _localVersion;
    public string? LocalVersion
    {
        get => _localVersion;
        set { Set(ref _localVersion, value); OnPropertyChanged(nameof(StatusLabel)); OnPropertyChanged(nameof(State)); }
    }

    private string? _cachedPath;
    public string? CachedPath { get => _cachedPath; set => Set(ref _cachedPath, value); }

    private bool _busy;
    public bool Busy { get => _busy; set { Set(ref _busy, value); OnPropertyChanged(nameof(StatusLabel)); } }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    public InstallState State =>
        LatestVersion is null ? InstallState.Unknown
        : LocalVersion is null ? InstallState.NotInstalled
        : LocalVersion != LatestVersion ? InstallState.UpdateAvailable
        : InstallState.Installed;

    public string StatusLabel => Busy
        ? $"{Progress:0} %"
        : State switch
        {
            InstallState.Unknown => Loc.T("comp.status.unavailable"),
            InstallState.NotInstalled => Loc.T("comp.status.available"),
            InstallState.UpdateAvailable => Loc.T("comp.status.update"),
            InstallState.Installed => Loc.T("comp.status.current"),
            _ => "—"
        };
}

/// <summary>Un addon HDR RenoDX publie dans la release "snapshot".</summary>
public sealed class RenoDxAddon
{
    public required string AssetName { get; init; }
    public required string DownloadUrl { get; init; }
    public long Size { get; init; }
    /// <summary>Slug extrait du nom : "renodx-cyberpunk2077.addon64" -> "cyberpunk2077".</summary>
    public required string Slug { get; init; }
    public bool Is32Bit { get; init; }

    public string Architecture => Is32Bit ? "x86" : "x64";
}
