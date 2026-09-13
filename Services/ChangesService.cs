using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Registre unifie de tout ce que Prism a ecrit dans les jeux.
///
/// Deux mecanismes coexistent — la sauvegarde d'un original remplace, et la trace
/// d'un fichier ajoute — et ils se defont differemment : l'un se restaure, l'autre
/// se supprime. Ce service les presente comme une seule liste, pour que rien de ce
/// qui a ete touche depuis le debut n'echappe au regard ni au bouton.
/// </summary>
public sealed class ChangesService
{
    private readonly BackupService _backups;
    private readonly DeploymentStore _deployments;

    public ChangesService(BackupService backups, DeploymentStore deployments)
    {
        _backups = backups;
        _deployments = deployments;
    }

    /// <summary>Toutes les modifications, de la plus recente a la plus ancienne.</summary>
    public List<ChangeEntry> All()
    {
        var rows = new List<ChangeEntry>();

        foreach (var b in _backups.All)
        {
            var component = ComponentOf(b.FileName);
            rows.Add(new ChangeEntry
            {
                Kind = ChangeKind.Replaced,
                GameId = b.GameId,
                GameName = b.GameName,
                Path = b.OriginalPath,
                Component = component,
                Origin = b.Origin ?? OriginOf(component),
                OriginInferred = b.Origin is null,
                Version = b.FileVersion,
                At = b.CreatedAt,
                BackupPath = b.BackupPath,
                StillApplies = b.StillExists
            });
        }

        foreach (var d in _deployments.All)
        {
            var component = string.IsNullOrWhiteSpace(d.Component) ? ComponentOf(d.FileName) : d.Component;
            rows.Add(new ChangeEntry
            {
                Kind = ChangeKind.Added,
                GameId = d.GameId,
                GameName = d.GameName,
                Path = d.Path,
                Component = component,
                Origin = d.Origin ?? OriginOf(component),
                OriginInferred = d.Origin is null,
                Version = d.Version,
                At = d.DeployedAt,
                StillApplies = d.StillExists
            });
        }

        return rows.OrderByDescending(r => r.At).ToList();
    }

    public List<ChangeEntry> For(string gameId) => All().Where(r => r.GameId == gameId).ToList();

    public int Count => All().Count;

    /// <summary>Nombre de titres touches, pour le resume de la barre d'etat.</summary>
    public int GameCount => All().Select(r => r.GameId).Distinct().Count();

    // ------------------------------------------------------ Par installation

    /// <summary>
    /// Ce que Prism a pose dans un jeu et qui s'y trouve encore, regroupe par
    /// installation. Un original deja restaure ne compte plus.
    /// </summary>
    public List<InstalledGroup> InstalledFor(string gameId) =>
        ActiveByOrigin(gameId)
            .GroupBy(x => x.Origin, x => x.Entry, StringComparer.OrdinalIgnoreCase)
            .Select(g => new InstalledGroup
            {
                Origin = g.Key,
                // La version de l'installation elle-meme, sinon celle d'un de ses fichiers.
                Version = (g.FirstOrDefault(e => OriginOf(e.Component).Equals(g.Key, StringComparison.OrdinalIgnoreCase)
                                                 && !string.IsNullOrWhiteSpace(e.Version))
                           ?? g.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Version)))?.Version,
                At = g.Max(e => e.At),
                Summary = Loc.T("installed.files",
                    g.Count(e => e.Kind == ChangeKind.Added),
                    g.Count(e => e.Kind == ChangeKind.Replaced)),
                Files = g.Select(e => e.FileName).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            })
            .OrderByDescending(g => g.At)
            .ToList();

    /// <summary>Defait tout ce qu'une installation a pose dans un jeu.</summary>
    public int RevertOrigin(string gameId, string origin) =>
        ActiveByOrigin(gameId)
            .Where(x => string.Equals(x.Origin, origin, StringComparison.OrdinalIgnoreCase))
            .Count(x => Revert(x.Entry).Success);

    /// <summary>Composants que le paquet RenoDX DLSS 5 pose avec son addon.</summary>
    private static readonly HashSet<string> Dlss5PackParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "DLSS SR", "DLSS-G", "DLSS-RR", "DLSS-NR", "Neural Rendering", "Streamline", "D3DCompiler"
    };

    /// <summary>
    /// Modifications actives et l'installation a laquelle elles appartiennent. Les entrees
    /// anterieures au suivi des origines n'ont que leur nom de composant : un runtime ou un
    /// Streamline ecrit dans les minutes qui entourent un addon RenoDX DLSS 5 fait partie de
    /// son paquet, et se retire avec lui. L'affichage et le retrait suivent la meme regle.
    /// </summary>
    private List<(ChangeEntry Entry, string Origin)> ActiveByOrigin(string gameId)
    {
        var active = For(gameId).Where(IsActive).ToList();

        var anchors = active
            .Where(e => e.Origin.Equals(Dlss5PackageInstaller.Origin, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.At)
            .ToList();

        return active.Select(e =>
        {
            var joinsPack = e.OriginInferred
                            && Dlss5PackParts.Contains(e.Component)
                            && anchors.Any(a => (e.At - a).Duration() <= TimeSpan.FromMinutes(10));
            return (e, joinsPack ? Dlss5PackageInstaller.Origin : e.Origin);
        }).ToList();
    }

    /// <summary>
    /// Vrai si la modification est encore en place : le fichier ajoute existe, ou le
    /// fichier remplace differe toujours de l'original mis de cote.
    /// </summary>
    public static bool IsActive(ChangeEntry e)
    {
        if (!File.Exists(e.Path)) return false;
        if (e.Kind == ChangeKind.Added) return true;
        if (e.BackupPath is null || !File.Exists(e.BackupPath)) return false;

        try
        {
            if (new FileInfo(e.Path).Length != new FileInfo(e.BackupPath).Length) return true;
            return !string.Equals(DownloadService.Sha256Cached(e.Path),
                DownloadService.Sha256Cached(e.BackupPath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    /// <summary>Installation a laquelle rattacher une entree anterieure au suivi des origines.</summary>
    public static string OriginOf(string component) => component switch
    {
        "RenoDX" or "RenoDX HDR" or "Engine.ini" => "RenoDX HDR",
        "MFGAdaUnlock" => "RenoDX MFG Unlock",
        // Nom employe par les premieres versions de l'installateur.
        "RenoDX DLSS5" => Dlss5PackageInstaller.Origin,
        _ => component
    };

    // ------------------------------------------------------------ Retrait

    /// <summary>Defait une modification : restaure l'original, ou retire le fichier ajoute.</summary>
    public InstallResult Revert(ChangeEntry entry)
    {
        if (entry.Kind == ChangeKind.Replaced)
        {
            var backup = _backups.All.FirstOrDefault(b =>
                string.Equals(b.OriginalPath, entry.Path, StringComparison.OrdinalIgnoreCase));

            if (backup is null) return new InstallResult(false, Loc.T("changes.msg.no_backup"));

            return _backups.Restore(backup)
                ? new InstallResult(true, Loc.T("changes.msg.restored", entry.FileName, entry.GameName), 1)
                : new InstallResult(false, Loc.T("changes.msg.restore_failed", entry.FileName));
        }

        var deployed = _deployments.All.FirstOrDefault(d =>
            string.Equals(d.Path, entry.Path, StringComparison.OrdinalIgnoreCase));

        if (deployed is null) return new InstallResult(false, Loc.T("changes.msg.no_entry"));

        return _deployments.Remove(deployed)
            ? new InstallResult(true, Loc.T("changes.msg.removed", entry.FileName, entry.GameName), 1)
            : new InstallResult(false, Loc.T("changes.msg.remove_failed", entry.FileName));
    }

    /// <summary>Remet un titre dans son etat d'origine, quel que soit le mecanisme employe.</summary>
    public InstallResult RevertGame(string gameId)
    {
        var rows = For(gameId);
        if (rows.Count == 0) return new InstallResult(false, Loc.T("changes.msg.untouched"));

        var done = rows.Count(r => Revert(r).Success);
        return done > 0
            ? new InstallResult(true, Loc.T("changes.msg.reverted", done, rows[0].GameName), done)
            : new InstallResult(false, Loc.T("changes.msg.none_reverted"));
    }

    /// <summary>Deduit le composant responsable a partir du nom de fichier.</summary>
    private static string ComponentOf(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        return n switch
        {
            "nvngx_dlss.dll" => "DLSS SR",
            "nvngx_dlssg.dll" => "DLSS-G",
            "nvngx_dlssd.dll" => "DLSS-NR",
            "nvngx_dlssnr.dll" => "Neural Rendering",
            "d3dcompiler_47.dll" => "D3DCompiler",
            _ when n.StartsWith("sl.") => "Streamline",
            _ when n.StartsWith("renodx-mfgunlock") => "MFGAdaUnlock",
            _ when n.StartsWith("renodx") => "RenoDX",
            _ when n.StartsWith("dlss5-bridge") => "DLSS 5 Bridge",
            _ when n.StartsWith("rtxmfg") || n.StartsWith("rtx40mfg") => "RTX40MFG-Unlock",
            _ when n.StartsWith("optiscaler") => "OptiScaler",
            _ when n.StartsWith("libxess") => "XeSS",
            _ when n.StartsWith("amd_fidelityfx") => "FSR",
            _ => "Composant"
        };
    }
}
