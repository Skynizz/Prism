using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Restes de mods retires. Prism ne retire que les fichiers qu'il a poses ; un mod, lui, cree en jeu
/// ses reglages et journaux (WindowUtils\data\settings.json), et ses dossiers restent vides.
///
/// Un dossier de mod est un enfant direct d'un conteneur connu du jeu (ModRules : Cyberpunk 2077 →
/// red4ext\plugins, ...\cyber_engine_tweaks\mods, r6\scripts, r6\tweaks, mods). Il est orphelin quand
/// il ne contient plus le fichier qui fait un mod (une DLL, init.lua, un .reds...) ni aucun fichier
/// suivi par Prism : ce qui y reste est mis de cote (annulable), puis les dossiers vides partent.
/// </summary>
public static class ModCleanup
{
    private const string Src = "cleanup";

    public const string Source = "Mods";

    /// <summary>Conteneurs de mods du jeu, en chemins absolus, avec leurs fichiers-reperes.</summary>
    public static List<(string Path, ModContainer Rule)> Containers(GameInfo game)
    {
        var result = new List<(string, ModContainer)>();
        if (ModRules.ForGame(game) is not { } rule) return result;
        foreach (var c in rule.ModContainers)
        {
            var path = Path.GetFullPath(Path.Combine(game.InstallDir, c.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (Directory.Exists(path)) result.Add((path, c));
        }
        return result;
    }

    /// <summary>Dossiers de mods orphelins : plus aucun fichier-repere, plus aucun fichier suivi par Prism.</summary>
    public static List<string> OrphanDirs(GameInfo game, ISet<string> tracked)
    {
        var result = new List<string>();
        foreach (var (container, rule) in Containers(game))
        {
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(container).ToList(); }
            catch { continue; }
            foreach (var dir in children)
            {
                var files = SafeFilesDeep(dir);
                if (files.Any(f => tracked.Contains(f))) continue;
                if (files.Any(f => rule.IsKey(Path.GetFileName(f)))) continue;
                result.Add(dir);
            }
        }
        return result;
    }

    /// <summary>Fichiers restant dans les dossiers orphelins, a mettre de cote.</summary>
    public static IEnumerable<string> OrphanFiles(GameInfo game, ISet<string> tracked)
        => OrphanDirs(game, tracked).SelectMany(SafeFilesDeep);

    /// <summary>Supprime les dossiers vides sous les conteneurs (jamais les conteneurs eux-memes).</summary>
    public static int PruneEmpty(GameInfo game)
    {
        var removed = 0;
        foreach (var (container, _) in Containers(game))
        {
            List<string> dirs;
            try { dirs = Directory.EnumerateDirectories(container, "*", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            // Les plus profonds d'abord : un parent se vide quand ses enfants sont partis.
            foreach (var dir in dirs.OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                    Directory.Delete(dir);
                    removed++;
                }
                catch (Exception ex) { Log.Warn(Src, $"Cannot remove {dir}: {ex.Message}"); }
            }
        }
        if (removed > 0) Log.Info(Src, $"{game.Name}: {removed} empty mod folder(s) removed");
        return removed;
    }

    private static List<string> SafeFilesDeep(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList(); }
        catch { return new List<string>(); }
    }
}
