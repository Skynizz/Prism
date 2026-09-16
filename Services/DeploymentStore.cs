using Prism.Models;

namespace Prism.Services;

/// <summary>Un fichier depose par Prism dans un jeu qui ne le contenait pas.</summary>
public sealed class DeployedFile
{
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    /// <summary>Chemin absolu du fichier depose.</summary>
    public string Path { get; set; } = "";
    public DllKind Kind { get; set; }
    /// <summary>Composant a l'origine de l'ecriture : "DLSS SR", "MFGAdaUnlock", "RenoDX"...</summary>
    public string Component { get; set; } = "";
    public string Version { get; set; } = "";
    public string Md5 { get; set; } = "";
    /// <summary>Empreinte de ce que Prism a reellement ecrit, pour detecter une derive.</summary>
    public string Sha256 { get; set; } = "";
    public DateTimeOffset DeployedAt { get; set; }
    /// <summary>Installation a laquelle le fichier appartient ; null pour les anciennes entrees.</summary>
    public string? Origin { get; set; }

    public string FileName => System.IO.Path.GetFileName(Path);
    public bool StillExists => File.Exists(Path);
}

/// <summary>
/// Registre des fichiers ajoutes par Prism. Un remplacement passe par les
/// sauvegardes ; un ajout, lui, n'a pas d'original a restaurer — il faut donc
/// savoir exactement quels fichiers retirer pour revenir a l'etat d'origine.
/// </summary>
public sealed class DeploymentStore
{
    private const string Src = "deploy";

    private readonly object _gate = new();
    private readonly List<DeployedFile> _entries;

    public DeploymentStore()
    {
        _entries = JsonStore.Load(AppPaths.DeploymentsFile, () => new List<DeployedFile>());
    }

    public IReadOnlyList<DeployedFile> All
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public IEnumerable<DeployedFile> For(string gameId)
        => All.Where(e => e.GameId == gameId);

    public DeployedFile? Find(string gameId, DllKind kind)
        => All.FirstOrDefault(e => e.GameId == gameId && e.Kind == kind && e.StillExists);

    public bool WasDeployed(string path)
        => All.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase) && e.StillExists);

    public void Record(GameInfo game, string path, DllKind kind, DllRecord record)
        => Record(game, path, ShortName(kind), record.Version, kind, record.Md5);

    /// <summary>Libelle compact, aligne sur celui du registre des modifications.</summary>
    public static string ShortName(DllKind kind) => kind switch
    {
        DllKind.Dlss => "DLSS SR",
        DllKind.DlssG => "DLSS-G",
        DllKind.DlssD => "DLSS-NR",
        DllKind.FsrDx12 or DllKind.FsrVk => "FSR",
        DllKind.XeSS or DllKind.XeSSFg => "XeSS",
        DllKind.XeLL => "XeLL",
        _ => kind.ToString()
    };

    /// <summary>
    /// Inscrit tout fichier depose par Prism, runtime ou composant. C'est ce registre
    /// qui rend la desinstallation exacte : on ne supprime que ce qu'on a ecrit.
    /// </summary>
    public void Record(GameInfo game, string path, string component, string version,
        DllKind kind = default, string md5 = "", string sha256 = "", string? origin = null)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            _entries.Add(new DeployedFile
            {
                GameId = game.Id,
                GameName = game.Name,
                Path = path,
                Kind = kind,
                Component = component,
                Version = version,
                Md5 = md5,
                Sha256 = sha256,
                DeployedAt = DateTimeOffset.Now,
                Origin = origin
            });
            Persist();
        }
    }

    /// <summary>Inscrit d'un coup les fichiers poses par un composant.</summary>
    public void RecordMany(GameInfo game, IEnumerable<string> paths, string component, string version)
    {
        foreach (var p in paths) Record(game, p, component, version);
    }

    /// <summary>
    /// Retire un fichier depose par Prism. Le fichier n'est supprime que s'il est
    /// bien celui que nous avons ecrit : si l'utilisateur l'a remplace entre-temps,
    /// on se contente d'oublier l'entree.
    /// </summary>
    public bool Remove(DeployedFile entry, bool deleteFile = true)
    {
        lock (_gate)
        {
            var removed = false;
            try
            {
                if (deleteFile && File.Exists(entry.Path))
                {
                    DllInstaller.ClearReadOnly(entry.Path);
                    File.Delete(entry.Path);
                    removed = true;
                    Log.Info(Src, $"Removed: {entry.Path}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn(Src, $"Cannot delete {entry.Path}: {ex.Message}");
                return false;
            }

            _entries.RemoveAll(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            Persist();
            return removed;
        }
    }

    public int RemoveGame(string gameId)
        => For(gameId).Count(e => Remove(e));

    private void Persist() => JsonStore.Save(AppPaths.DeploymentsFile, _entries);
}
