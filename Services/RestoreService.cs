using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>Etat reel d'une modification, confronte au disque.</summary>
public enum DriftState
{
    /// <summary>Le fichier est bien celui que Prism a ecrit.</summary>
    Intact,
    /// <summary>Le fichier a ete remplace par quelqu'un d'autre depuis.</summary>
    Modified,
    /// <summary>Le fichier a disparu.</summary>
    Missing,
    /// <summary>La sauvegarde de l'original n'est plus la : la restauration est impossible.</summary>
    BackupLost
}

public sealed record DriftReport(ChangeEntry Entry, DriftState State)
{
    public string Label => State switch
    {
        DriftState.Intact => Loc.T("drift.intact"),
        DriftState.Modified => Loc.T("drift.modified"),
        DriftState.Missing => Loc.T("drift.missing"),
        DriftState.BackupLost => Loc.T("drift.backup_lost"),
        _ => "?"
    };

    public UiStatus Status => State switch
    {
        DriftState.Intact => UiStatus.Ready,
        DriftState.Modified => UiStatus.Warning,
        DriftState.Missing => UiStatus.Idle,
        _ => UiStatus.Error
    };
}

/// <summary>Un fichier de mod trouve dans un titre mais absent du registre de Prism.</summary>
public sealed record OrphanFile(string Path, string Kind)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>Ce que ferait un retour a l'etat d'origine, avant de le faire.</summary>
public sealed class RestorePlan
{
    public required string GameName { get; init; }
    public List<ChangeEntry> ToRestore { get; init; } = new();
    public List<ChangeEntry> ToRemove { get; init; } = new();
    public List<DriftReport> Blocked { get; init; } = new();
    public List<OrphanFile> Orphans { get; init; } = new();
    public int IniEntries { get; init; }

    public int Total => ToRestore.Count + ToRemove.Count;
    public bool IsEmpty => Total == 0 && Orphans.Count == 0 && IniEntries == 0;

    public string Summary
    {
        get
        {
            if (IsEmpty) return Loc.T("restore.vanilla_already");
            var parts = new List<string>();
            if (ToRestore.Count > 0) parts.Add(Loc.T("restore.plan.restore", ToRestore.Count));
            if (ToRemove.Count > 0) parts.Add(Loc.T("restore.plan.remove", ToRemove.Count));
            if (IniEntries > 0) parts.Add(Loc.T("restore.plan.ini", IniEntries));
            if (Orphans.Count > 0) parts.Add(Loc.T("restore.plan.orphans", Orphans.Count));
            if (Blocked.Count > 0) parts.Add(Loc.T("restore.plan.blocked", Blocked.Count));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Ramene un titre a son etat d'origine, et sait dire avant de le faire ce qui sera
/// touche et ce qui resistera.
///
/// Trois cas se distinguent, et c'est la toute la difference entre « ca a marche »
/// et « le jeu ne demarre plus » :
///
///  - un fichier <b>remplace</b> se restaure depuis sa sauvegarde ;
///  - un fichier <b>ajoute</b> se supprime ;
///  - un fichier <b>modifie hors Prism</b> depuis l'installation n'est plus le notre.
///    On ne l'ecrase pas sans le dire : l'utilisateur a peut-etre mis la une version
///    qu'il voulait garder.
///
/// S'y ajoutent les fichiers de mod presents dans le dossier mais absents du
/// registre — installes a la main avant Prism. Ils sont signales, jamais supprimes
/// sans demande explicite.
/// </summary>
public sealed class RestoreService
{
    private const string Src = "restore";

    /// <summary>Noms et motifs qui trahissent un mod graphique dans un dossier de jeu.</summary>
    private static readonly string[] OrphanExact =
    {
        "OptiScaler.dll", "OptiScaler.asi", "OptiScaler.ini", "dlss-enabler.dll",
        "nvngx_dlssnr.dll", "RTXMFG.dll", "dlssg_sm86.ini", "sm75_backend.dll",
        "ReShade64.dll", "ReShade32.dll"
    };

    private readonly ChangesService _changes;
    private readonly BackupService _backups;
    private readonly DeploymentStore _deployments;
    private readonly HdrInstaller _hdr;

    public RestoreService(ChangesService changes, BackupService backups, DeploymentStore deployments, HdrInstaller hdr)
    {
        _changes = changes;
        _backups = backups;
        _deployments = deployments;
        _hdr = hdr;
    }

    // ------------------------------------------------------------ Verification

    /// <summary>
    /// Confronte chaque modification au disque. Sert autant a diagnostiquer un jeu
    /// qui ne demarre plus qu'a verifier qu'une desinstallation sera propre.
    /// </summary>
    public List<DriftReport> Verify(string gameId)
    {
        var rows = new List<DriftReport>();

        foreach (var change in _changes.For(gameId))
        {
            if (!File.Exists(change.Path))
            {
                rows.Add(new DriftReport(change, DriftState.Missing));
                continue;
            }

            if (change.Kind == ChangeKind.Replaced)
            {
                var backup = _backups.All.FirstOrDefault(b =>
                    string.Equals(b.OriginalPath, change.Path, StringComparison.OrdinalIgnoreCase));

                rows.Add(new DriftReport(change,
                    backup is null || !backup.StillExists ? DriftState.BackupLost : DriftState.Intact));
                continue;
            }

            // Pour un ajout, l'empreinte enregistree tranche : si elle differe, le
            // fichier a ete remplace par quelqu'un d'autre depuis.
            var deployed = _deployments.All.FirstOrDefault(d =>
                string.Equals(d.Path, change.Path, StringComparison.OrdinalIgnoreCase));

            if (deployed is null || string.IsNullOrEmpty(deployed.Sha256))
            {
                rows.Add(new DriftReport(change, DriftState.Intact));
                continue;
            }

            string actual;
            try { actual = DownloadService.Sha256Cached(change.Path); }
            catch { rows.Add(new DriftReport(change, DriftState.Intact)); continue; }

            rows.Add(new DriftReport(change,
                actual.Equals(deployed.Sha256, StringComparison.OrdinalIgnoreCase)
                    ? DriftState.Intact
                    : DriftState.Modified));
        }

        return rows;
    }

    // --------------------------------------------------------------- Orphelins

    /// <summary>
    /// Fichiers de mod presents dans le dossier du titre mais absents du registre.
    /// Presque toujours : une installation manuelle anterieure a Prism.
    /// </summary>
    public List<OrphanFile> FindOrphans(GameInfo game)
    {
        var dir = DllInstaller.TargetDirectory(game);
        var known = _changes.For(game.Id)
            .Select(c => c.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphans = new List<OrphanFile>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (known.Contains(file)) continue;

                var name = Path.GetFileName(file);
                var kind = ClassifyOrphan(name, file);
                if (kind is not null) orphans.Add(new OrphanFile(file, kind));
            }
        }
        catch (Exception ex) { Log.Warn(Src, $"Balayage des orphelins impossible : {ex.Message}"); }

        return orphans;
    }

    private static string? ClassifyOrphan(string name, string fullPath)
    {
        if (OrphanExact.Contains(name, StringComparer.OrdinalIgnoreCase))
            return name.StartsWith("ReShade", StringComparison.OrdinalIgnoreCase) ? "ReShade" : "Mod";

        if (name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase))
            return "Addon";

        if (name.StartsWith("RTX40MFG", StringComparison.OrdinalIgnoreCase)) return "Mod";

        // Un proxy n'est un orphelin que si son binaire se reclame d'une surcouche
        // connue : un dxgi.dll systeme ne doit jamais etre propose a la suppression.
        if (DllDetector.ProxyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(fullPath);
                var tag = $"{info.FileDescription} {info.ProductName} {info.CompanyName}";
                if (tag.Contains("ReShade", StringComparison.OrdinalIgnoreCase)) return "ReShade";
                if (tag.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("MFG", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("DLSS Enabler", StringComparison.OrdinalIgnoreCase)) return "Proxy";
            }
            catch { }
        }

        return null;
    }

    // ------------------------------------------------------------------- Plan

    /// <summary>Ce qui serait fait, sans rien faire encore.</summary>
    public RestorePlan Plan(GameInfo game)
    {
        var drift = Verify(game.Id);
        var dir = DllInstaller.TargetDirectory(game);

        var plan = new RestorePlan
        {
            GameName = game.Name,
            IniEntries = ReShadeConfig.Exists(dir) ? CountIniTraces(dir) : 0,
            Orphans = FindOrphans(game)
        };

        foreach (var d in drift)
        {
            switch (d.State)
            {
                case DriftState.Intact when d.Entry.Kind == ChangeKind.Replaced:
                    plan.ToRestore.Add(d.Entry);
                    break;
                case DriftState.Intact when d.Entry.Kind == ChangeKind.Added:
                    plan.ToRemove.Add(d.Entry);
                    break;
                case DriftState.Missing:
                    break; // deja absent : rien a faire
                default:
                    plan.Blocked.Add(d);
                    break;
            }
        }

        return plan;
    }

    private static int CountIniTraces(string dir)
    {
        var n = 0;
        if (ReShadeConfig.ReadMfg(dir).MaxCount != 4 ||
            ReShadeConfig.ReadMfg(dir).ForceMultiplier != 0) n++;

        foreach (var addon in new[] { "renodx-dlss5.addon64", "renodx-mfgunlock.addon64", "dlss5-bridge.addon64" })
            if (ReShadeConfig.IsEarlyLoaded(dir, addon)) n++;

        return n;
    }

    // ---------------------------------------------------------------- Execution

    /// <summary>
    /// Ramene le titre a son etat d'origine. Les fichiers modifies hors Prism ne
    /// sont touches que si <paramref name="force"/> est vrai ; les orphelins
    /// seulement si <paramref name="includeOrphans"/> l'est.
    /// </summary>
    public InstallResult RestoreVanilla(GameInfo game, bool includeOrphans = false, bool force = false)
    {
        var plan = Plan(game);
        if (plan.IsEmpty) return new InstallResult(false, Loc.T("restore.vanilla_already"));

        var restored = 0;
        var removed = 0;
        var refused = new List<string>();

        foreach (var entry in plan.ToRestore.Concat(plan.ToRemove))
        {
            var result = _changes.Revert(entry);
            if (!result.Success) { refused.Add(entry.FileName); continue; }
            if (entry.Kind == ChangeKind.Replaced) restored++; else removed++;
        }

        // Les fichiers qui ont derive ne sont repris qu'a la demande expresse.
        if (force)
        {
            foreach (var blocked in plan.Blocked.Where(b => b.State == DriftState.Modified))
            {
                var result = _changes.Revert(blocked.Entry);
                if (result.Success) removed++;
                else refused.Add(blocked.Entry.FileName);
            }
        }

        if (includeOrphans)
        {
            foreach (var orphan in plan.Orphans)
            {
                try
                {
                    DllInstaller.ClearReadOnly(orphan.Path);
                    File.Delete(orphan.Path);
                    removed++;
                    Log.Info(Src, $"Orphelin retire : {orphan.Path}");
                }
                catch (Exception ex)
                {
                    refused.Add(orphan.FileName);
                    Log.Warn(Src, $"Suppression de {orphan.Path} impossible : {ex.Message}");
                }
            }
        }

        var dir = DllInstaller.TargetDirectory(game);
        var ini = ReShadeConfig.CleanUp(dir,
            new[] { "renodx-dlss5.addon64", "renodx-mfgunlock.addon64", "dlss5-bridge.addon64" });

        // Cles [renodx] ecrites pour le HDR : remises a leur valeur d'avant Prism.
        ini += _hdr.ForgetTraces(game);

        DllDetector.Inspect(game);

        var parts = new List<string>();
        if (restored > 0) parts.Add(Loc.T("restore.done.restored", restored));
        if (removed > 0) parts.Add(Loc.T("restore.done.removed", removed));
        if (ini > 0) parts.Add(Loc.T("restore.done.ini", ini));

        var msg = parts.Count > 0
            ? Loc.T("restore.done", game.Name, string.Join(", ", parts))
            : Loc.T("restore.nothing", game.Name);

        if (refused.Count > 0) msg += " " + Loc.T("restore.refused", string.Join(", ", refused));
        if (!force && plan.Blocked.Any(b => b.State == DriftState.Modified))
            msg += " " + Loc.T("restore.kept_modified");

        Log.Info(Src, msg);
        return new InstallResult(restored + removed > 0, msg, restored + removed);
    }
}
