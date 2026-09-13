using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Racine de composition. Un seul point ou les services sont crees et cables,
/// ce qui evite de faire circuler une demi-douzaine de dependances partout.
/// </summary>
public sealed class AppServices
{
    public AppServices()
    {
        // Le materiel d'abord : certains services en dependent (runtime neural par generation).
        Gpu = GpuService.Detect();
        Display = SystemInfo.GetDisplay();

        Settings = new SettingsStore();
        Profiles = new ProfileStore();
        Downloads = new DownloadService();
        Backups = new BackupService();
        Deployments = new DeploymentStore();
        Manifest = new ManifestService();
        GitHub = new GitHubService(Downloads);
        ReShade = new ReShadeService(Downloads);
        RenoDx = new RenoDxService(GitHub, Downloads, Deployments);
        Wiki = new RenoDxWikiService(Downloads);
        Hdr = new HdrInstaller(Downloads, Deployments, Backups, Profiles);
        Rhi = new RhiRepoService(Downloads);
        FrameGen = new FrameGenService(GitHub, Downloads, Deployments);
        Installer = new DllInstaller(Downloads, Backups, Deployments);
        Dlss5 = new Dlss5Service(GitHub, Downloads, Deployments, Rhi,
            new Dlss5PackageInstaller(Rhi, Downloads, Backups, Deployments, () => Gpu));
        Changes = new ChangesService(Backups, Deployments);
        Restore = new RestoreService(Changes, Backups, Deployments, Hdr);
        Streamline = new StreamlineService(GitHub, Downloads, Backups);
        Catalog = new ComponentCatalog(GitHub, ReShade, RenoDx, Manifest);
        Scanner = new GameScanner { ExtraFolders = Settings.Current.ExtraLibraryFolders };


        Log.Info("system", $"GPU {Gpu.Name} · {Gpu.GenerationLabel} · pilote {Gpu.DriverBranch ?? Gpu.DriverVersion}");
        Log.Info("system", $"Ecran {Display.Summary} · {Gpu.DirectXLevel}");
    }

    public SettingsStore Settings { get; }
    public ProfileStore Profiles { get; }
    public DownloadService Downloads { get; }
    public BackupService Backups { get; }
    public DeploymentStore Deployments { get; }
    public ManifestService Manifest { get; }
    public GitHubService GitHub { get; }
    public ReShadeService ReShade { get; }
    public RenoDxService RenoDx { get; }
    public FrameGenService FrameGen { get; }
    public DllInstaller Installer { get; }
    public Dlss5Service Dlss5 { get; }
    public ChangesService Changes { get; }
    public RenoDxWikiService Wiki { get; }
    public HdrInstaller Hdr { get; }
    public RhiRepoService Rhi { get; }
    public RestoreService Restore { get; }
    public StreamlineService Streamline { get; }
    public ComponentCatalog Catalog { get; }
    public GameScanner Scanner { get; }
    public GpuInfo Gpu { get; }
    public DisplayInfo Display { get; }

    /// <summary>Identifiant du jeu actuellement en cours d'execution, ou null.</summary>
    public string? RunningGameId { get; set; }

    /// <summary>Recupere les catalogues distants. Une source en panne n'empeche pas les autres.</summary>
    public async Task InitializeAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(Loc.T("init.runtimes"));
        await Manifest.LoadAsync(false, ct);

        progress?.Report(Loc.T("init.components"));
        await Catalog.RefreshAsync(ct);

        progress?.Report(Loc.T("init.wiki"));
        await Wiki.LoadAsync(ct);

        progress?.Report(Loc.T("init.dlss5"));
        await Dlss5.LoadAsync(ct);

        progress?.Report(Loc.T("init.streamline"));
        await Streamline.LoadAsync(ct);
    }
}
