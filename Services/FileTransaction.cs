using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Ecritures groupees dans un jeu : tout est pose et verifie, ou rien ne change.
///
/// Sequence, pour chaque fichier et dans l'ordre :
///  1. copie de l'etat actuel dans un dossier d'annulation propre a la transaction ;
///  2. sauvegarde durable de l'original s'il n'a pas ete pose par Prism — sans elle, rien
///     n'est ecrase ;
///  3. ecriture dans un fichier temporaire voisin, puis remplacement d'un seul geste ;
///  4. relecture et comparaison des empreintes avec la source.
/// Au premier echec, les fichiers deja touches reprennent leur contenu d'avant et les
/// ajouts sont retires. Le registre des deploiements n'est mis a jour qu'une fois tout pose.
/// </summary>
public sealed class FileTransaction
{
    private const string Src = "tx";
    private const string TempSuffix = ".prism-new";

    private enum Op { Write, Delete }

    private sealed class Step
    {
        public Op Op;
        public string Dest = "";
        public string Source = "";
        public string Component = "";
        public string Version = "";
        public DllKind Kind;
        public string Md5 = "";
        public bool Track = true;

        public bool Touched;
        public bool Existed;
        public bool Ours;
        public string? Undo;
    }

    private readonly GameInfo _game;
    private readonly BackupService _backups;
    private readonly DeploymentStore _deployments;
    private readonly string? _origin;
    private readonly List<Step> _steps = new();
    private readonly string _work;

    public FileTransaction(GameInfo game, BackupService backups, DeploymentStore deployments, string? origin)
    {
        _game = game;
        _backups = backups;
        _deployments = deployments;
        _origin = origin;
        _work = Path.Combine(AppPaths.Root, "tx", Guid.NewGuid().ToString("N"));
    }

    public int Count => _steps.Count;

    /// <summary>Fichiers que la transaction va toucher.</summary>
    public IEnumerable<string> Targets => _steps.Select(s => s.Dest);

    /// <param name="track">Faux pour un fichier modifie qui ne doit pas changer de proprietaire au registre.</param>
    public void Copy(string source, string dest, string component, string version,
        DllKind kind = default, string md5 = "", bool track = true)
    {
        _steps.RemoveAll(s => s.Dest.Equals(dest, StringComparison.OrdinalIgnoreCase));
        _steps.Add(new Step
        {
            Op = Op.Write, Source = source, Dest = dest, Component = component,
            Version = version, Kind = kind, Md5 = md5, Track = track
        });
    }

    /// <summary>Contenu texte a poser, prepare hors du jeu avant la validation.</summary>
    public void WriteText(string dest, string content, string component, string version, bool track = true)
    {
        Directory.CreateDirectory(_work);
        var source = Path.Combine(_work, $"text{_steps.Count}.src");
        File.WriteAllText(source, content);
        Copy(source, dest, component, version, track: track);
    }

    /// <summary>Retrait d'un fichier, restaure si la transaction echoue.</summary>
    public void Delete(string path)
    {
        if (_steps.Any(s => s.Dest.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        _steps.Add(new Step { Op = Op.Delete, Dest = path });
    }

    public InstallResult Commit()
    {
        if (_steps.Count == 0) return new InstallResult(true, "", 0);

        if (GameGuard.Check(_game, _steps.Select(s => s.Dest)) is { } blocked) return blocked;

        Step? current = null;
        try
        {
            Directory.CreateDirectory(_work);

            for (var i = 0; i < _steps.Count; i++)
            {
                var step = current = _steps[i];
                step.Existed = File.Exists(step.Dest);
                step.Ours = _deployments.WasDeployed(step.Dest);

                if (step.Existed)
                {
                    step.Undo = Path.Combine(_work, $"{i}.undo");
                    File.Copy(step.Dest, step.Undo, overwrite: true);
                }

                if (step.Op == Op.Delete)
                {
                    if (!step.Existed) continue;
                    step.Touched = true;
                    DllInstaller.ClearReadOnly(step.Dest);
                    File.Delete(step.Dest);
                    continue;
                }

                if (!File.Exists(step.Source))
                    throw new FileNotFoundException(Loc.T("tx.source_missing", Path.GetFileName(step.Source)));

                // Un original du jeu ou d'un autre outil n'est jamais ecrase sans sauvegarde durable.
                if (step.Existed && !step.Ours && _backups.Capture(_game, step.Dest, _origin) is null)
                    throw new IOException(Loc.T("tx.no_backup", Path.GetFileName(step.Dest)));

                Directory.CreateDirectory(Path.GetDirectoryName(step.Dest)!);
                var temp = step.Dest + TempSuffix;
                File.Copy(step.Source, temp, overwrite: true);

                step.Touched = true;
                DllInstaller.ClearReadOnly(step.Dest);
                File.Move(temp, step.Dest, overwrite: true);

                if (!string.Equals(DownloadService.Sha256Cached(step.Dest), DownloadService.Sha256Cached(step.Source),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Loc.T("tx.verify", Path.GetFileName(step.Dest)));
            }

            // Tout est en place : le registre suit.
            foreach (var step in _steps)
            {
                if (step.Op == Op.Delete)
                {
                    var entry = _deployments.All.FirstOrDefault(e =>
                        string.Equals(e.Path, step.Dest, StringComparison.OrdinalIgnoreCase));
                    if (entry is not null) _deployments.Remove(entry, deleteFile: false);
                }
                else if (step.Track && (!step.Existed || step.Ours))
                {
                    _deployments.Record(_game, step.Dest, step.Component, step.Version, step.Kind, step.Md5,
                        DownloadService.Sha256Cached(step.Dest), _origin);
                }
            }

            Log.Info(Src, $"{_game.Name}: {_steps.Count} write(s) committed{(_origin is null ? "" : $" ({_origin})")}");
            return new InstallResult(true, "", _steps.Count(s => s.Touched));
        }
        catch (Exception ex)
        {
            var rolledBack = Rollback();
            var name = Path.GetFileName(current?.Dest ?? "");
            Log.Error(Src, $"{_game.Name}: failed on {current?.Dest} ({ex.Message}), {rolledBack} file(s) restored");

            return new InstallResult(false, GameGuard.IsSharingViolation(ex)
                ? Loc.T("guard.locked", name)
                : Loc.T("tx.failed", name, ex.Message.TrimEnd('.', ' ')));
        }
        finally
        {
            foreach (var step in _steps.Where(s => s.Op == Op.Write))
                TryDelete(step.Dest + TempSuffix);
            try { if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true); }
            catch { /* dossier temporaire : sans consequence */ }
        }
    }

    /// <summary>Remet chaque fichier touche dans son etat d'avant, du dernier au premier.</summary>
    private int Rollback()
    {
        var restored = 0;
        for (var i = _steps.Count - 1; i >= 0; i--)
        {
            var step = _steps[i];
            if (!step.Touched) continue;
            try
            {
                DllInstaller.ClearReadOnly(step.Dest);
                if (step.Undo is not null && File.Exists(step.Undo))
                    File.Copy(step.Undo, step.Dest, overwrite: true);
                else if (File.Exists(step.Dest))
                    File.Delete(step.Dest);
                restored++;
            }
            catch (Exception ex)
            {
                Log.Error(Src, $"Rollback failed for {step.Dest}: {ex.Message}");
            }
        }
        return restored;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* fichier temporaire */ }
    }
}
