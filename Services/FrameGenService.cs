using System.Diagnostics;
using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Determine et installe les chemins de frame generation disponibles pour un couple
/// (GPU, jeu).
///
/// Etat du materiel, qui conditionne tout le reste :
///  - Blackwell (RTX 50) : MFG x2 a x4 officiel, signe par le pilote.
///  - Ada (RTX 40)       : DLSS-G x2 officiel. Les multiplicateurs superieurs sont
///                         debridables, soit par un addon en memoire, soit par un
///                         proxy DLL.
///  - Ampere / Turing    : aucun DLSS-G. On passe par OptiScaler ou DLSS Enabler,
///                         qui reroutent la generation vers FSR 3.1.
/// Dans tous les cas, le jeu doit deja embarquer une integration Streamline DLSS-G.
/// </summary>
public sealed class FrameGenService
{
    public const string MfgUnlockRepo = "dashdogy/RTX40MFG-Unlock";
    public const string MfgAdaRepo = "mavismmg/MFGAdaUnlock-RenoDx";
    public const string OptiScalerRepo = "optiscaler/OptiScaler";
    public const string DlssEnablerRepo = "artur-graniszewski/DLSS-Enabler";

    private const string Src = "framegen";

    /// <summary>Fork OptiScaler qui sait sortir du vrai DLSS-G depuis l'upscaler du jeu (FGOutput=dlssg).</summary>
    public const string InjectedFgRepo = "wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass";
    public const string InjectedFgOrigin = "OptiScaler DLSS FG";

    /// <summary>
    /// Les six binaires que le fork attend dans OptiScaler\streamline, tires du SDK officiel
    /// Streamline 2.14.1 (bin/x64). Empreintes de son redist/streamline/manifest.json : un seul
    /// octet different et rien n'est pose.
    /// </summary>
    public const string InjectedFgStreamline = "2.14.1";
    private static readonly Dictionary<string, string> InjectedFgPins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sl.interposer.dll"] = "8C87C9499461DA561EDD529AA9BF7831D67D7B94EBB1C1A5ED54EF4934E1EA4C",
        ["sl.common.dll"] = "82924A8954DD671E09351C5DE0EB87AD0EB25B944CC9F9AB955CA1D9950DE15D",
        ["sl.dlss_g.dll"] = "F4A6B2B14DCC0B1485989E430D3B4E3A44AC1800B92BA1AD74F476E64FB2B09C",
        ["sl.reflex.dll"] = "0CE9725E3E03EA9E7F81D008B57F33EE365973D2E349131C8B1C3E3378FE2DB0",
        ["sl.pcl.dll"] = "F13D51CFA05F4CD514DF2026049E2DB8ADF359221713170AD386FD499915B582",
        ["nvngx_dlssg.dll"] = "FF6E90EB78B827927DFF5B4ECC6B1C870C2E9BCA29ED9F48C7D348CC9E170B82",
    };

    private readonly GitHubService _github;
    private readonly DownloadService _downloads;
    private readonly BackupService _backups;
    private readonly DeploymentStore _deployments;
    private readonly StreamlineService _streamline;

    public FrameGenService(GitHubService github, DownloadService downloads, BackupService backups,
        DeploymentStore deployments, StreamlineService streamline)
    {
        _github = github;
        _downloads = downloads;
        _backups = backups;
        _deployments = deployments;
        _streamline = streamline;
    }

    /// <summary>Pose les fichiers en une transaction : tout, ou le jeu tel qu'il etait.</summary>
    private InstallResult Commit(GameInfo game, IEnumerable<(string Source, string Dest)> files, string component, string version)
    {
        var tx = new FileTransaction(game, _backups, _deployments, null);
        foreach (var (source, dest) in files) tx.Copy(source, dest, component, version);
        return tx.Commit();
    }

    /// <summary>
    /// Voies proposees pour ce couple (GPU, titre). La construction et l'evaluation
    /// des prerequis vivent dans <see cref="FrameGenOptions"/>.
    /// </summary>
    public List<FgOption> OptionsFor(GpuInfo gpu, GameInfo game)
        => FrameGenOptions.Build(gpu, game);

    /// <summary>Aiguille vers l'installateur correspondant au chemin choisi.</summary>
    public Task<InstallResult> InstallAsync(
        GameInfo game, FgOption option, IProgress<double>? progress = null, CancellationToken ct = default)
        => option.Backend switch
        {
            FgBackend.MfgAdaUnlock => InstallMfgAdaAsync(game, progress, ct),
            FgBackend.Rtx40MfgUnlock => InstallMfgUnlockAsync(game, progress, ct),
            FgBackend.OptiScaler or FgBackend.OptiFg => InstallOptiScalerAsync(game, progress, ct),
            FgBackend.InjectedDlssG => InstallInjectedDlssGAsync(game, progress, ct),
            FgBackend.DlssEnabler => LaunchDlssEnablerAsync(game, progress, ct),
            // Ces portages ne publient pas d'archive de release : Prism ouvre la page
            // source plutot que de racler des binaires sans empreinte publiee.
            FgBackend.DlssgSm86 or FgBackend.DlssgSm75 => Task.FromResult(new InstallResult(false,
                Loc.T("fg.msg.no_release", option.Title))),
            FgBackend.NativeDlssG => Task.FromResult(new InstallResult(true,
                Loc.T("fg.msg.native"))),
            _ => Task.FromResult(new InstallResult(false, Loc.T("err.unknown_path")))
        };

    // -------------------------------------------------------- MFGAdaUnlock

    /// <summary>
    /// Addon RenoDX MFG Unlock : un unique .addon64 depose a cote de l'executable.
    /// Aucun fichier du titre n'est remplace, donc aucune sauvegarde n'est necessaire —
    /// mais le fichier, lui, est bel et bien indispensable.
    /// </summary>
    public async Task<InstallResult> InstallMfgAdaAsync(
        GameInfo game, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var release = await _github.LatestAsync(MfgAdaRepo, ct);
        var asset = release?.Assets.FirstOrDefault(a => a.Name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase));
        if (release is null || asset is null)
            return new InstallResult(false, Loc.T("err.release_not_found", "RenoDX MFG Unlock"));

        var dir = TargetDir(game);
        try
        {
            var cached = Path.Combine(AppPaths.ComponentCache, "mfgada", release.Tag, asset.Name);
            await _downloads.DownloadAsync(asset.Url, cached, null, progress, ct);

            var dest = Path.Combine(dir, asset.Name);
            var written = Commit(game, new[] { (cached, dest) }, "MFGAdaUnlock", release.Tag);
            if (!written.Success) return written;

            DllDetector.Inspect(game);
            Log.Info(Src, $"MFGAdaUnlock {release.Tag} placed in {dir}");

            var warn = game.HasReShade ? "" : " " + Loc.T("fg.msg.reshade_needed");
            return new InstallResult(true,
                Loc.T("fg.msg.mfg_installed", release.Tag) + warn, 1);
        }
        catch (Exception ex)
        {
            Log.Error(Src, $"MFGAdaUnlock install failed: {ex.Message}");
            return new InstallResult(false, Loc.T("err.install_failed", ex.Message));
        }
    }

    // ------------------------------------------------------- RTX40MFG-Unlock

    public async Task<InstallResult> InstallMfgUnlockAsync(
        GameInfo game, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var release = await _github.LatestAsync(MfgUnlockRepo, ct);
        var asset = release?.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (release is null || asset is null)
            return new InstallResult(false, Loc.T("err.release_not_found", "RTX40MFG-Unlock"));

        var dir = TargetDir(game);
        try
        {
            var zip = Path.Combine(AppPaths.ComponentCache, "mfgunlock", asset.Name);
            await _downloads.DownloadAsync(asset.Url, zip, null, progress, ct);

            var extractDir = Path.Combine(AppPaths.ComponentCache, "mfgunlock", release.Tag);
            await ArchiveExtractor.ExtractAsync(zip, extractDir, ct);

            var dll = ArchiveExtractor.FindFile(extractDir, "RTXMFG.dll");
            if (dll is null) return new InstallResult(false, Loc.T("err.missing_in_archive", "RTXMFG.dll"));

            // La DLL doit prendre le nom d'une bibliotheque que le jeu charge au demarrage.
            var proxy = PickProxyName(dir);
            if (proxy is null)
                return new InstallResult(false, Loc.T("err.no_proxy"));

            var dest = Path.Combine(dir, proxy);
            var written = Commit(game, new[] { (dll, dest) }, "RTX40MFG-Unlock", release.Tag);
            if (!written.Success) return written;

            DllDetector.Inspect(game);
            Log.Info(Src, $"RTX40MFG-Unlock {release.Tag} installed as {proxy}");
            return new InstallResult(true,
                Loc.T("fg.msg.rtx40_installed", release.Tag, proxy), 1);
        }
        catch (Exception ex)
        {
            Log.Error(Src, $"MFG Unlock install failed: {ex.Message}");
            return new InstallResult(false, Loc.T("err.install_failed", ex.Message));
        }
    }

    // ------------------------------------------------------------ OptiScaler

    public async Task<InstallResult> InstallOptiScalerAsync(
        GameInfo game, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var release = await _github.LatestAsync(OptiScalerRepo, ct);
        var asset = release?.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (release is null || asset is null)
            return new InstallResult(false, Loc.T("err.release_not_found", "OptiScaler"));

        var dir = TargetDir(game);

        // Deja la (DLSS 5, autre voie) : un seul OptiScaler par jeu, seule sa configuration change.
        // Le fork en place sait aussi sortir du FSR FG ; le remplacer casserait son DLSS 5.
        if (LoadedOptiScaler(dir) is { } present)
            return new InstallResult(true, Loc.T("opti.reuse", present), 0);

        try
        {
            var archive = Path.Combine(AppPaths.ComponentCache, "optiscaler", asset.Name);
            await _downloads.DownloadAsync(asset.Url, archive, null, progress, ct);

            var extractDir = Path.Combine(AppPaths.ComponentCache, "optiscaler", release.Tag);
            await ArchiveExtractor.ExtractAsync(archive, extractDir, ct);

            var core = ArchiveExtractor.FindFile(extractDir, "OptiScaler.dll")
                       ?? ArchiveExtractor.FindFile(extractDir, "OptiScaler.asi");
            if (core is null) return new InstallResult(false, Loc.T("err.missing_in_archive", "OptiScaler.dll"));

            var sourceDir = Path.GetDirectoryName(core)!;
            var proxy = PickProxyName(dir);
            if (proxy is null)
                return new InstallResult(false, Loc.T("err.no_proxy"));

            var staged = new List<(string Source, string Dest)>();
            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                var name = Path.GetFileName(file);
                // Scripts d'installation, notices et marqueurs : rien a poser dans le jeu.
                if (!ArchiveExtractor.IsPayload(name)) continue;

                var destName = name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ? proxy : name;
                var dest = Path.Combine(dir, destName);

                // Une configuration deja ajustee par l'utilisateur ne doit pas etre ecrasee.
                if (name.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase) && File.Exists(dest)) continue;

                staged.Add((file, dest));
            }

            var written = Commit(game, staged, "OptiScaler", release.Tag);
            if (!written.Success) return written;
            var copied = staged.Count;

            DllDetector.Inspect(game);
            Log.Info(Src, $"OptiScaler {release.Tag} installed as {proxy} ({copied} files)");
            return new InstallResult(true,
                Loc.T("opti.ok", "OptiScaler", release.Tag, proxy, copied), copied);
        }
        catch (Exception ex)
        {
            Log.Error(Src, $"OptiScaler install failed: {ex.Message}");
            return new InstallResult(false, Loc.T("err.install_failed", ex.Message));
        }
    }

    // ------------------------------------------------------ DLSS-G injecte

    /// <summary>
    /// Vrai DLSS Frame Generation dans un jeu qui n'a qu'un upscaler, en suivant a la lettre la
    /// notice du fork (docs/DLSS-FRAME-GENERATION.md) : son archive standard — jamais la variante
    /// « rtx40-mfg », non verifiee sur RTX 40 de l'aveu de l'auteur —, controlee par son .sha256,
    /// puis les binaires Streamline de production dans OptiScaler\streamline, controles un a un.
    /// La configuration (FGInput=upscaler, FGOutput=dlssg) est ecrite ensuite, avec le multiplicateur.
    /// </summary>
    public async Task<InstallResult> InstallInjectedDlssGAsync(
        GameInfo game, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var release = await _github.LatestAsync(InjectedFgRepo, ct);
        var asset = release?.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            !a.Name.Contains("rtx40", StringComparison.OrdinalIgnoreCase));
        if (release is null || asset is null)
            return new InstallResult(false, Loc.T("err.release_not_found", InjectedFgOrigin));

        var dir = TargetDir(game);
        try
        {
            var root = Path.Combine(AppPaths.ComponentCache, "injectedfg", AppPaths.Sanitize(release.Tag));
            var archive = Path.Combine(root, asset.Name);
            await _downloads.DownloadAsync(asset.Url, archive, null, progress, ct);

            // Empreinte publiee a cote de l'archive : une archive alteree n'est jamais extraite.
            var shaAsset = release.Assets.FirstOrDefault(a => a.Name.Equals(asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
            if (shaAsset is not null)
            {
                var published = (await _downloads.GetStringAsync(shaAsset.Url, ct)).Trim().Split(' ', '\t')[0];
                var actual = DownloadService.Sha256Cached(archive);
                if (!published.Equals(actual, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(archive);
                    return new InstallResult(false, Loc.T("fg.inj.err.hash", asset.Name));
                }
            }

            var extractDir = Path.Combine(root, "files");
            if (!Directory.Exists(extractDir) || !Directory.EnumerateFileSystemEntries(extractDir).Any())
                await ArchiveExtractor.ExtractAsync(archive, extractDir, ct);

            var core = ArchiveExtractor.FindFile(extractDir, "OptiScaler.dll");
            if (core is null) return new InstallResult(false, Loc.T("err.missing_in_archive", "OptiScaler.dll"));

            // Un fork deja en place (DLSS 5) fait deja tout : on n'y ajoute que Streamline. Un
            // OptiScaler officiel, lui, ne sait pas sortir du DLSS-G : le fork prend sa place.
            var present = LoadedOptiScaler(dir);
            var keepFork = present is not null && OptiScalerConfig.IsFork(dir);
            var proxy = present ?? PickProxyName(dir);
            if (proxy is null) return new InstallResult(false, Loc.T("err.no_proxy"));

            var sdkBin = await _streamline.SdkBinAsync(InjectedFgStreamline, null, ct);
            if (sdkBin is null) return new InstallResult(false, Loc.T("sl.err.no_interposer"));

            var tx = new FileTransaction(game, _backups, _deployments, null);
            foreach (var file in keepFork ? Array.Empty<string>() : Directory.EnumerateFiles(Path.GetDirectoryName(core)!))
            {
                var name = Path.GetFileName(file);
                if (!ArchiveExtractor.IsPayload(name)) continue;
                var dest = Path.Combine(dir, name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ? proxy : name);
                // Une configuration deja ajustee ne doit pas etre ecrasee.
                if (name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && File.Exists(dest)) continue;
                tx.Copy(file, dest, InjectedFgOrigin, release.Tag);
            }

            var slDir = Path.Combine(dir, "OptiScaler", "streamline");
            foreach (var (name, pin) in InjectedFgPins)
            {
                var source = Path.Combine(sdkBin, name);
                if (!File.Exists(source) || !DownloadService.Sha256Cached(source).Equals(pin, StringComparison.OrdinalIgnoreCase))
                    return new InstallResult(false, Loc.T("fg.inj.err.pin", name));
                tx.Copy(source, Path.Combine(slDir, name), InjectedFgOrigin, InjectedFgStreamline);
            }
            // Licences NVIDIA livrees avec les binaires, comme le demande la notice.
            foreach (var license in new[] { "nvngx_dlss.license.txt", "reflex.license.txt" })
                if (File.Exists(Path.Combine(sdkBin, license)))
                    tx.Copy(Path.Combine(sdkBin, license), Path.Combine(slDir, license), InjectedFgOrigin, InjectedFgStreamline);

            var written = tx.Commit();
            if (!written.Success) return written;

            DllDetector.Inspect(game);
            Log.Info(Src, $"{InjectedFgOrigin} {release.Tag} installed as {proxy} ({tx.Count} files, Streamline {InjectedFgStreamline})");
            return new InstallResult(true,
                Loc.T("opti.ok", "OptiScaler · DLSS FG", release.Tag, proxy, tx.Count) + " " + Loc.T("fg.nofg.upscaler_on"), tx.Count);
        }
        catch (Exception ex)
        {
            Log.Error(Src, $"{InjectedFgOrigin} install failed: {ex.Message}");
            return new InstallResult(false, Loc.T("err.install_failed", ex.Message));
        }
    }

    // --------------------------------------------------------- DLSS Enabler

    /// <summary>
    /// DLSS Enabler n'est distribue que sous forme d'installeur interactif : on le
    /// telecharge et on le lance, l'utilisateur designe le dossier du jeu.
    /// </summary>
    public async Task<InstallResult> LaunchDlssEnablerAsync(
        GameInfo game, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var release = await _github.LatestAsync(DlssEnablerRepo, ct);
        var asset = release?.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (release is null || asset is null)
            return new InstallResult(false, Loc.T("err.release_not_found", "DLSS Enabler"));

        try
        {
            var exe = Path.Combine(AppPaths.ComponentCache, "dlss-enabler", asset.Name);
            await _downloads.DownloadAsync(asset.Url, exe, null, progress, ct);

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return new InstallResult(true,
                Loc.T("common.launched", "DLSS Enabler", release.Tag, TargetDir(game)), 0);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, Loc.T("err.launch_failed", ex.Message));
        }
    }

    // ------------------------------------------------------------ Desinstall

    public static InstallResult RemoveOverlays(GameInfo game)
    {
        var dir = TargetDir(game);
        var removed = new List<string>();

        foreach (var file in SafeFiles(dir))
        {
            var name = Path.GetFileName(file);

            var isAddon = name.StartsWith("renodx-mfgunlock", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("RTX40MFG", StringComparison.OrdinalIgnoreCase);

            var isCandidate = isAddon
                              || DllDetector.ProxyNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                              || name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)
                              || name.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)
                              || name.Equals("dlss-enabler.dll", StringComparison.OrdinalIgnoreCase)
                              || name.Equals("RTXMFG.dll", StringComparison.OrdinalIgnoreCase);
            if (!isCandidate) continue;

            // On ne retire une DLL que si ses metadonnees la rattachent bien a une surcouche.
            if (!isAddon && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !IsOverlayBinary(file))
                continue;

            try
            {
                DllInstaller.ClearReadOnly(file);
                File.Delete(file);
                removed.Add(name);
            }
            catch (Exception ex) { Log.Warn(Src, $"Cannot delete {file}: {ex.Message}"); }
        }

        DllDetector.Inspect(game);
        return removed.Count > 0
            ? new InstallResult(true, Loc.T("common.removed_list", string.Join(", ", removed)), removed.Count)
            : new InstallResult(false, Loc.T("fg.msg.no_overlay"));
    }

    private static bool IsOverlayBinary(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var tag = $"{info.FileDescription} {info.ProductName} {info.CompanyName} {info.InternalName}";
            return tag.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase)
                   || tag.Contains("DLSS Enabler", StringComparison.OrdinalIgnoreCase)
                   || tag.Contains("RTXMFG", StringComparison.OrdinalIgnoreCase)
                   || tag.Contains("MFG", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ----------------------------------------------------------- Utilitaires

    /// <summary>Dossier de l'executable : c'est la que proxys et addons doivent atterrir.</summary>
    public static string TargetDir(GameInfo game)
        => (string.IsNullOrWhiteSpace(game.Executable) ? null : Path.GetDirectoryName(game.Executable))
           ?? game.InstallDir;

    /// <summary>
    /// Choisit un nom de DLL proxy encore libre. ReShade occupe souvent dxgi.dll ;
    /// ecraser ce fichier casserait les deux mods a la fois.
    /// </summary>
    public static string? PickProxyName(string dir)
        => DllDetector.ProxyNames
            .Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(n => !File.Exists(Path.Combine(dir, n)));

    /// <summary>Nom sous lequel un OptiScaler est deja charge dans ce dossier, s'il y en a un.</summary>
    public static string? LoadedOptiScaler(string dir)
        => DllDetector.ProxyNames
            .Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Append("nvngx.dll")
            .FirstOrDefault(n => File.Exists(Path.Combine(dir, n)) && LeftoverCleaner.IsOptiScaler(Path.Combine(dir, n)));

    /// <summary>
    /// Un seul OptiScaler par jeu : il regroupe upscaler, generation d'images et DLSS 5. Un
    /// deuxieme, pose sous un autre nom de proxy, serait charge en plus du premier — c'est le
    /// conflit que son propre script d'installation traque. Sa place existante est donc reprise.
    /// </summary>
    public static string? OptiScalerSlot(string dir) => LoadedOptiScaler(dir) ?? PickProxyName(dir);

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).ToList(); }
        catch { return Array.Empty<string>(); }
    }
}
