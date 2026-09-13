using Prism.Models;
using Prism.Core;

namespace Prism.Services;

public sealed record InstallResult(bool Success, string Message, int FilesChanged = 0);

/// <summary>
/// Pose les runtimes NVIDIA dans un jeu, selon deux modes distincts :
///
///  - <b>remplacement</b> : le jeu embarque deja la bibliotheque, on met a jour sa
///    version. L'original part en sauvegarde avant toute ecriture.
///  - <b>ajout</b> : le jeu ne l'embarque pas. Rien n'est ecrase, donc rien a
///    sauvegarder ; en revanche le fichier est inscrit au registre de deploiement
///    pour pouvoir etre retire proprement.
///
/// Dans les deux cas la sequence est invariable : verifier la signature, telecharger,
/// controler le MD5, puis seulement ecrire.
/// </summary>
public sealed class DllInstaller
{
    private const string Src = "runtime";

    private readonly DownloadService _downloads;
    private readonly BackupService _backups;
    private readonly DeploymentStore _deployments;

    public DllInstaller(DownloadService downloads, BackupService backups, DeploymentStore deployments)
    {
        _downloads = downloads;
        _backups = backups;
        _deployments = deployments;
    }

    public static string FileNameFor(DllKind kind) => kind switch
    {
        DllKind.Dlss => "nvngx_dlss.dll",
        DllKind.DlssG => "nvngx_dlssg.dll",
        DllKind.DlssD => "nvngx_dlssd.dll",
        DllKind.FsrDx12 => "amd_fidelityfx_dx12.dll",
        DllKind.FsrVk => "amd_fidelityfx_vk.dll",
        DllKind.XeSS => "libxess.dll",
        DllKind.XeSSFg => "libxess_fg.dll",
        DllKind.XeLL => "libxell.dll",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string LabelFor(DllKind kind) => kind switch
    {
        DllKind.Dlss => "DLSS — Super Resolution",
        DllKind.DlssG => "DLSS-G — Frame Generation",
        DllKind.DlssD => "DLSS-D — Ray Reconstruction",
        DllKind.FsrDx12 => "FSR 3.1 — DirectX 12",
        DllKind.FsrVk => "FSR 3.1 — Vulkan",
        DllKind.XeSS => "XeSS — Super Sampling",
        DllKind.XeSSFg => "XeSS — Frame Generation",
        DllKind.XeLL => "XeLL — Low Latency",
        _ => kind.ToString()
    };

    /// <summary>
    /// Met a jour toutes les occurrences de cette bibliotheque dans le jeu, ou la
    /// depose a cote de l'executable si le jeu ne la contient pas encore.
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        GameInfo game,
        DllRecord record,
        IProgress<double>? progress = null,
        bool deployIfMissing = false,
        CancellationToken ct = default)
    {
        if (!record.IsSignatureValid)
            return new InstallResult(false, Loc.T("dll.err.signature"));

        var fileName = FileNameFor(record.Kind);
        var targets = game.Dlls.Where(d => d.Kind == record.Kind).Select(d => d.Path).ToList();
        var deploying = targets.Count == 0;

        if (deploying)
        {
            if (!deployIfMissing)
                return new InstallResult(false, Loc.T("dll.err.absent_use_deploy", fileName));

            var dir = TargetDirectory(game);
            if (!Directory.Exists(dir))
                return new InstallResult(false, Loc.T("dll.err.no_target"));

            targets.Add(Path.Combine(dir, fileName));
        }

        string dll;
        try
        {
            dll = await FetchAsync(record, fileName, progress, ct);
        }
        catch (Exception ex)
        {
            Log.Error(Src, $"Telechargement de {fileName} {record.Version} echoue : {ex.Message}");
            return new InstallResult(false, Loc.T("err.download_failed", ex.Message));
        }

        var changed = 0;
        var failures = new List<string>();

        foreach (var target in targets)
        {
            try
            {
                // Un fichier que Prism a lui-meme depose reste un ajout : le mettre a
                // jour ne doit pas creer de sauvegarde, sinon la meme ecriture
                // apparaitrait deux fois au registre des modifications.
                var ours = _deployments.WasDeployed(target);
                if (File.Exists(target) && !ours) _backups.Capture(game, target);

                ClearReadOnly(target);
                File.Copy(dll, target, overwrite: true);

                if (deploying || ours) _deployments.Record(game, target, record.Kind, record);
                changed++;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(target)} : {ex.Message}");
                Log.Error(Src, $"Ecriture impossible sur {target} : {ex.Message}");
            }
        }

        DllDetector.Inspect(game);

        if (changed == 0)
            return new InstallResult(false, Loc.T("dll.err.failed", string.Join(" | ", failures)));

        var msg = Loc.T(deploying ? "dll.ok.added" : "dll.ok.updated", LabelFor(record.Kind), record.Version, changed);
        if (failures.Count > 0) msg += " " + Loc.T("dll.partial", failures.Count);

        Log.Info(Src, msg);
        return new InstallResult(true, msg, changed);
    }

    /// <summary>
    /// Dossier ou deposer une bibliotheque absente : celui de l'executable, car c'est
    /// la que le chargeur Windows ira la chercher.
    /// </summary>
    public static string TargetDirectory(GameInfo game)
        => (string.IsNullOrWhiteSpace(game.Executable) ? null : Path.GetDirectoryName(game.Executable))
           ?? game.InstallDir;

    /// <summary>Telecharge l'archive, verifie les deux empreintes, renvoie la DLL extraite.</summary>
    private async Task<string> FetchAsync(DllRecord record, string fileName, IProgress<double>? progress, CancellationToken ct)
    {
        var stem = $"{record.Kind}_{record.Version}_{record.Md5}";
        var zip = Path.Combine(AppPaths.DllCache, AppPaths.Sanitize(stem) + ".zip");
        var dir = Path.Combine(AppPaths.DllCache, AppPaths.Sanitize(stem));
        var dll = Path.Combine(dir, fileName);

        // Deja en cache et intact : on ressort directement.
        if (File.Exists(dll) && await DownloadService.Md5Async(dll, ct) == record.Md5.ToUpperInvariant())
        {
            progress?.Report(100);
            return dll;
        }

        await _downloads.DownloadAsync(record.DownloadUrl, zip, record.ZipMd5, progress, ct);
        await ArchiveExtractor.ExtractAsync(zip, dir, ct);

        var extracted = File.Exists(dll) ? dll : ArchiveExtractor.FindFile(dir, fileName);
        if (extracted is null)
            throw new FileNotFoundException(Loc.T("dll.err.not_in_archive", fileName));

        var actual = await DownloadService.Md5Async(extracted, ct);
        if (actual != record.Md5.ToUpperInvariant())
            throw new InvalidDataException(Loc.T("dll.err.md5", record.Md5, actual));

        return extracted;
    }

    /// <summary>Retire une bibliotheque ajoutee par Prism dans ce titre.</summary>
    public InstallResult Undeploy(GameInfo game, DllKind kind)
    {
        var entry = _deployments.Find(game.Id, kind);
        if (entry is null)
            return new InstallResult(false, Loc.T("dll.err.not_deployed", LabelFor(kind)));

        var ok = _deployments.Remove(entry);
        DllDetector.Inspect(game);

        return ok
            ? new InstallResult(true, Loc.T("dll.ok.undeployed", entry.FileName), 1)
            : new InstallResult(false, Loc.T("changes.msg.remove_failed", entry.FileName));
    }

    /// <summary>
    /// Remet le jeu dans son etat d'origine : les fichiers remplaces sont restaures,
    /// les fichiers ajoutes sont retires.
    /// </summary>
    public InstallResult RestoreAll(GameInfo game)
    {
        var restored = _backups.RestoreGame(game.Id);
        var removed = _deployments.RemoveGame(game.Id);
        DllDetector.Inspect(game);

        if (restored == 0 && removed == 0)
            return new InstallResult(false, Loc.T("dll.err.nothing_to_restore"));

        var parts = new List<string>();
        if (restored > 0) parts.Add(Loc.T("dll.ok.restored_n", restored));
        if (removed > 0) parts.Add(Loc.T("dll.ok.removed_n", removed));

        var msg = string.Join(", ", parts) + ".";
        Log.Info(Src, $"{game.Name} : {msg}");
        return new InstallResult(true, msg, restored + removed);
    }

    /// <summary>Les installeurs de jeux posent parfois l'attribut lecture seule sur les DLL.</summary>
    internal static void ClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch { /* sans consequence : la copie remontera l'erreur reelle */ }
    }
}
