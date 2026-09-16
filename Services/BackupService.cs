using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Conserve une copie de chaque fichier d'origine avant remplacement, et sait le
/// remettre en place. Rien n'est ecrase dans un jeu sans passer par ici.
/// </summary>
public sealed class BackupService
{
    private readonly object _gate = new();
    private List<BackupEntry> _entries;

    public BackupService()
    {
        _entries = JsonStore.Load(AppPaths.BackupIndexFile, () => new List<BackupEntry>());
    }

    public IReadOnlyList<BackupEntry> All
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public IEnumerable<BackupEntry> For(string gameId)
        => All.Where(e => e.GameId == gameId);

    public bool HasBackup(string originalPath)
        => All.Any(e => string.Equals(e.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase) && e.StillExists);

    /// <summary>
    /// Met de cote le fichier d'origine. Ne fait rien si une sauvegarde valide existe
    /// deja : la toute premiere version installee par le jeu est celle qu'on veut garder,
    /// pas celle d'un remplacement precedent.
    /// </summary>
    public BackupEntry? Capture(GameInfo game, string filePath, string? origin = null)
    {
        if (!File.Exists(filePath)) return null;

        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(e =>
                string.Equals(e.OriginalPath, filePath, StringComparison.OrdinalIgnoreCase));
            // L'original deja mis de cote reste celui du premier remplacement, origine comprise.
            if (existing is not null && existing.StillExists) return existing;

            var dir = AppPaths.BackupDirFor(game.Id);
            // Le chemin relatif est aplati pour supporter deux DLL homonymes dans deux sous-dossiers.
            var relative = Path.GetRelativePath(game.InstallDir, filePath).Replace('\\', '_').Replace('/', '_');
            var dest = Path.Combine(dir, relative);

            try
            {
                File.Copy(filePath, dest, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Write($"Cannot back up {filePath}: {ex.Message}");
                return null;
            }

            var entry = new BackupEntry
            {
                GameId = game.Id,
                GameName = game.Name,
                OriginalPath = filePath,
                BackupPath = dest,
                FileVersion = DllDetector.ReadVersion(filePath),
                CreatedAt = DateTimeOffset.Now,
                Origin = origin
            };

            if (existing is not null) _entries.Remove(existing);
            _entries.Add(entry);
            Persist();
            return entry;
        }
    }

    /// <summary>Remet le fichier d'origine en place. La sauvegarde est conservee.</summary>
    public bool Restore(BackupEntry entry)
    {
        try
        {
            if (!File.Exists(entry.BackupPath)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
            // Un Engine.ini passe en lecture seule refuserait sinon d'etre remplace.
            DllInstaller.ClearReadOnly(entry.OriginalPath);
            File.Copy(entry.BackupPath, entry.OriginalPath, overwrite: true);
            Log.Write($"Restored: {entry.OriginalPath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Cannot restore {entry.OriginalPath}: {ex.Message}");
            return false;
        }
    }

    public int RestoreGame(string gameId)
        => For(gameId).Count(Restore);

    public void Forget(BackupEntry entry)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => string.Equals(e.OriginalPath, entry.OriginalPath, StringComparison.OrdinalIgnoreCase));
            try { if (File.Exists(entry.BackupPath)) File.Delete(entry.BackupPath); }
            catch (Exception ex) { Log.Write($"Cannot delete backup: {ex.Message}"); }
            Persist();
        }
    }

    private void Persist() => JsonStore.Save(AppPaths.BackupIndexFile, _entries);
}
