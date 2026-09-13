using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Depose un paquet Streamline complet et coherent dans un titre.
///
/// MFGAdaUnlock est formel : il ne faut jamais melanger sl.interposer.dll,
/// sl.common.dll, sl.dlss_g.dll ou sl.reflex.dll issus de paquets differents. Un
/// melange produit un echec silencieux, un plantage au demarrage ou un effondrement
/// des performances. Prism installe donc l'ensemble apparie, tire du SDK officiel
/// publie par NVIDIA — la seule source faisant autorite pour ces binaires.
/// </summary>
public sealed class StreamlineService
{
    private const string Src = "streamline";

    /// <summary>Depot officiel NVIDIA. NVIDIAGameWorks/Streamline y redirige.</summary>
    public const string Repo = "NVIDIA-RTX/Streamline";

    /// <summary>Version exigee par le mode MFG dynamique de MFGAdaUnlock.</summary>
    public const string DynamicMfgVersion = "2.14.1";

    private readonly GitHubService _github;
    private readonly DownloadService _downloads;
    private readonly BackupService _backups;
    private readonly DeploymentStore _deployments;

    public StreamlineService(GitHubService github, DownloadService downloads, BackupService backups,
        DeploymentStore deployments)
    {
        _github = github;
        _downloads = downloads;
        _backups = backups;
        _deployments = deployments;
    }

    public string? LatestVersion { get; private set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var release = await _github.LatestAsync(Repo, ct);
        LatestVersion = release?.Tag?.TrimStart('v');
    }

    /// <summary>Vrai si le titre porte exactement la version demandee.</summary>
    public static bool Matches(GameInfo game, string version)
        => game.StreamlineVersion is { } v && v.StartsWith(version, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Telecharge le SDK officiel et remplace l'ensemble des composants Streamline du
    /// titre par le jeu apparie. Chaque original part en sauvegarde.
    /// </summary>
    public async Task<InstallResult> DeployAsync(
        GameInfo game, string version, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var tag = version.StartsWith('v') ? version : "v" + version;
        var release = await _github.ByTagAsync(Repo, tag, ct);

        // Le SDK x64 est le seul utile ici : on ecarte aarch64 et arm64ec.
        var asset = release?.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("streamline-sdk-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            !a.Name.Contains("aarch64", StringComparison.OrdinalIgnoreCase) &&
            !a.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase));

        if (release is null || asset is null)
            return new InstallResult(false, Loc.T("sl.err.not_found", tag));

        var targetDir = DllInstaller.TargetDirectory(game);
        if (!Directory.Exists(targetDir))
            return new InstallResult(false, Loc.T("err.target_missing"));

        try
        {
            var zip = Path.Combine(AppPaths.ComponentCache, "streamline", asset.Name);
            Log.Info(Src, $"Telechargement du SDK Streamline {tag} ({asset.Size / 1024 / 1024} Mo)");
            await _downloads.DownloadAsync(asset.Url, zip, null, progress, ct);

            var extractDir = Path.Combine(AppPaths.ComponentCache, "streamline", tag);
            if (!Directory.Exists(extractDir) || !Directory.EnumerateFileSystemEntries(extractDir).Any())
                await ArchiveExtractor.ExtractAsync(zip, extractDir, ct);

            // Les binaires livrables vivent sous bin/x64 dans le SDK.
            var binDir = Directory.EnumerateDirectories(extractDir, "x64", SearchOption.AllDirectories)
                .FirstOrDefault(d => Directory.EnumerateFiles(d, "sl.interposer.dll").Any());

            if (binDir is null)
                return new InstallResult(false, Loc.T("sl.err.no_interposer"));

            // On ne remplace que les composants deja presents dans le titre : ajouter
            // un plugin dont le jeu n'a pas besoin ne ferait qu'ajouter du risque.
            var present = Directory.EnumerateFiles(targetDir, "sl.*.dll")
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (present.Count == 0)
                return new InstallResult(false,
                    Loc.T("sl.err.none_in_game"));

            var skipped = new List<string>();

            // Le paquet apparie entier, ou rien : un melange de composants sl.* est
            // precisement ce que MFGAdaUnlock interdit.
            var tx = new FileTransaction(game, _backups, _deployments, null);
            foreach (var name in present)
            {
                var source = Path.Combine(binDir, name!);
                if (!File.Exists(source)) { skipped.Add(name!); continue; }
                tx.Copy(source, Path.Combine(targetDir, name!), "Streamline", version);
            }

            var committed = tx.Commit();
            DllDetector.Inspect(game);
            if (!committed.Success) return committed;
            var copied = tx.Count;

            var msg = Loc.T("sl.ok", version, copied);
            if (skipped.Count > 0) msg += " " + Loc.T("sl.skipped", string.Join(", ", skipped));
            Log.Info(Src, msg);

            return copied > 0
                ? new InstallResult(true, msg, copied)
                : new InstallResult(false, Loc.T("sl.err.none_matched", msg));
        }
        catch (Exception ex)
        {
            Log.Error(Src, $"Deploiement Streamline echoue : {ex.Message}");
            return new InstallResult(false, Loc.T("err.deploy_failed", ex.Message));
        }
    }
}
