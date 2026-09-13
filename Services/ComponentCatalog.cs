using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Composants tiers suivis par Prism. Aucune version n'est ecrite en dur : chaque
/// entree interroge sa source amont, et rapporte sa provenance (etoiles, licence,
/// date du dernier commit) pour que l'utilisateur puisse juger la source.
///
/// Descriptions et notes de confiance sont des cles de traduction : elles suivent la
/// langue de l'interface.
/// </summary>
public sealed class ComponentCatalog
{
    private const string Src = "catalog";

    /// <summary>
    /// Depots a ne jamais utiliser. L'equipe OptiScaler declare publiquement n'avoir
    /// aucune application de gestion officielle : tout depot qui s'en reclame est a
    /// ecarter, quel que soit son nombre d'etoiles.
    /// </summary>
    public static readonly IReadOnlyList<string> Blocklist = new[]
    {
        "Optiscaler-Client/Optiscaler-Client"
    };

    private readonly GitHubService _github;
    private readonly ReShadeService _reshade;
    private readonly RenoDxService _renodx;
    private readonly ManifestService _manifest;

    public ComponentCatalog(GitHubService github, ReShadeService reshade, RenoDxService renodx,
        ManifestService manifest)
    {
        _github = github;
        _reshade = reshade;
        _renodx = renodx;
        _manifest = manifest;

        Components = new List<ModComponent>
        {
            Entry("dlss", "NVIDIA DLSS Runtime", "https://github.com/beeradmoore/dlss-swapper",
                "beeradmoore/dlss-swapper", ComponentRole.Runtime),
            Entry("mfgada", "RenoDX MFG Unlock", "https://github.com/mavismmg/MFGAdaUnlock-RenoDx",
                FrameGenService.MfgAdaRepo, ComponentRole.FrameGen, nonDestructive: true),
            Entry("mfgunlock", "RTX40MFG-Unlock", "https://github.com/dashdogy/RTX40MFG-Unlock",
                FrameGenService.MfgUnlockRepo, ComponentRole.FrameGen),
            Entry("sm86", "dlssg for sm_86", FrameGenOptions.Sm86Url,
                FrameGenOptions.Sm86Repo, ComponentRole.FrameGen),
            Entry("sm75", "dlssg for sm_75", FrameGenOptions.Sm75Url,
                FrameGenOptions.Sm75Repo, ComponentRole.FrameGen),
            Entry("streamline", "NVIDIA Streamline SDK", "https://github.com/NVIDIA-RTX/Streamline",
                StreamlineService.Repo, ComponentRole.Runtime),
            Entry("optinr", "OptiScaler DLSSNR", "https://github.com/Dagherbou/OptiScaler_DLSSNR",
                Dlss5Service.OptiNrRepo, ComponentRole.Runtime),
            Entry("multipass", "OptiScaler PreSR Multipass", "https://github.com/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass",
                Dlss5Service.MultipassRepo, ComponentRole.Runtime),
            Entry("oneclick", "DLSS5 One-Click", "https://github.com/faisalkindi/DLSS5oneclick",
                Dlss5Service.OneClickRepo, ComponentRole.Runtime),
            Entry("optiscaler", "OptiScaler", "https://github.com/optiscaler/OptiScaler",
                FrameGenService.OptiScalerRepo, ComponentRole.FrameGen),
            Entry("dlssenabler", "DLSS Enabler", "https://github.com/artur-graniszewski/DLSS-Enabler",
                FrameGenService.DlssEnablerRepo, ComponentRole.FrameGen),
            Entry("dlss5bridge", "DLSS 5 Bridge", "https://github.com/NIGos/dlss5-bridge",
                Dlss5Service.BridgeRepo, ComponentRole.Runtime, nonDestructive: true),
            Entry("reshade", "ReShade", "https://reshade.me/", null, ComponentRole.PostProcess),
            Entry("renodx", "RenoDX", "https://github.com/clshortfuse/renodx",
                RenoDxService.Repo, ComponentRole.PostProcess, nonDestructive: true)
        };
    }

    private static ModComponent Entry(string id, string name, string home, string? repo,
        ComponentRole role, bool nonDestructive = false) => new()
    {
        Id = id,
        Name = name,
        HomeUrl = home,
        GitHubRepo = repo,
        Role = role,
        NonDestructive = nonDestructive,
        DescriptionKey = $"comp.{id}.desc",
        TrustNoteKey = $"comp.{id}.trust"
    };

    public List<ModComponent> Components { get; }

    public ModComponent? this[string id] => Components.FirstOrDefault(c => c.Id == id);

    /// <summary>Interroge chaque source : derniere version publiee et provenance.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // Sequentiel : l'API GitHub anonyme est limitee a 60 requetes par heure et par IP.
        foreach (var c in Components)
        {
            ct.ThrowIfCancellationRequested();
            await RefreshOneAsync(c, ct);
        }
    }

    private async Task RefreshOneAsync(ModComponent component, CancellationToken ct)
    {
        try
        {
            switch (component.Id)
            {
                case "reshade":
                    await _reshade.LoadAsync(ct);
                    component.LatestVersion = _reshade.LatestVersion;
                    break;

                case "renodx":
                    await _renodx.LoadAsync(ct);
                    component.LatestVersion = _renodx.PublishedAt is { } d ? $"snapshot {d:yyyy-MM-dd}" : null;
                    component.ReleasedAt = _renodx.PublishedAt;
                    break;

                case "streamline":
                    component.LatestVersion = StreamlineService.DynamicMfgVersion;
                    break;

                case "dlss":
                    // La version de reference est celle du manifeste amont, pas un tag de release.
                    var best = _manifest.Recommended(DllKind.Dlss);
                    component.LatestVersion = best?.Version;
                    component.ReleasedAt = best?.SignedAt;
                    break;

                default:
                    if (component.GitHubRepo is null) break;
                    var release = await _github.LatestAsync(component.GitHubRepo, ct);
                    component.LatestVersion = release?.Tag;
                    component.ReleasedAt = release?.PublishedAt;
                    break;
            }

            if (component.GitHubRepo is not null)
            {
                var repo = await _github.RepoAsync(component.GitHubRepo, ct);
                if (repo is not null)
                {
                    component.Stars = repo.Stars;
                    component.License = repo.License;
                    component.LastCommit = repo.PushedAt;

                    if (repo.Archived)
                        Log.Warn(Src, $"{component.Name} : depot archive en amont.");
                    else if (component.IsStale)
                        Log.Warn(Src, $"{component.Name} : aucun commit depuis {component.FreshnessLabel}.");
                }
            }

            Log.Trace(Src, $"{component.Name} -> {component.LatestVersion ?? "n/d"}");
        }
        catch (Exception ex)
        {
            Log.Warn(Src, $"Verification de {component.Name} impossible : {ex.Message}");
        }
    }
}
