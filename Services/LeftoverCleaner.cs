using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Nettoyage profond : retire d'un jeu tout ce que d'autres outils y ont laisse, y compris
/// avant Prism, et remet les originaux qu'ils avaient renommes.
///
/// Rien n'est devine. Trois sources, chacune tiree du code de l'outil concerne :
///  - RHI (RankFTW/RHI) : avant d'ecrire X, il renomme l'original en « X.original » ; si X
///    n'existait pas, il cree un « X.original » vide (AuxInstallService.SentinelBackup). Il
///    tient aussi la liste de ce qu'il a pose dans « rhi_install.txt » (RhiInstallManifest) ;
///  - OptiScaler : le binaire garde « OptiScaler.dll » comme nom d'origine quel que soit son
///    nom de proxy — c'est ainsi que son propre script d'installation le retrouve ;
///  - les mods deja reconnus par <see cref="ForeignModScanner"/>.
///
/// Chaque fichier retire est d'abord copie dans un dossier de nettoyage : un passage entier
/// s'annule d'un clic. Ce que Prism a pose n'est jamais touche ici : son propre retrait s'en charge.
/// </summary>
public sealed class LeftoverCleaner
{
    private const string Src = "clean";
    public const string RhiManifest = "rhi_install.txt";
    private const string OriginalSuffix = ".original";
    private const string SessionFile = "session.json";

    /// <summary>Fichiers qu'OptiScaler et son script ecrivent a cote du jeu (setup_windows.bat).</summary>
    private static readonly string[] OptiScalerFiles =
    {
        "OptiScaler.ini", "OptiScaler.log", "OptiScaler.asi", "OptiScaler.dll",
        "Remove OptiScaler.bat", "Remove_OptiScaler.bat", "fakenvapi.dll", "fakenvapi.ini", "fakenvapi.log"
    };

    /// <summary>Sous-dossiers d'OptiScaler, retires seulement si OptiScaler est bien la.</summary>
    private static readonly string[] OptiScalerFolders = { "OptiScaler", "D3D12_Optiscaler" };

    /// <summary>Noms sous lesquels OptiScaler se fait charger (RHI : SupportedDllNames, plus nvngx.dll).</summary>
    private static readonly string[] OptiScalerProxyNames =
    {
        "dxgi.dll", "winmm.dll", "d3d11.dll", "d3d12.dll", "dbghelp.dll", "version.dll",
        "wininet.dll", "winhttp.dll", "nvngx.dll"
    };

    private readonly ChangesService _changes;
    private readonly ProfileStore _profiles;

    public LeftoverCleaner(ChangesService changes, ProfileStore profiles)
    {
        _changes = changes;
        _profiles = profiles;
    }

    // ------------------------------------------------------------------ Plan

    public CleanPlan Plan(GameInfo game)
    {
        var changes = _changes.For(game.Id).ToList();
        var prism = changes.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var origins = changes.Select(c => c.Origin).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // OptiScaler pose par Prism : son journal et ses reglages crees en jeu lui appartiennent.
        var prismOptiScaler = origins.Any(o => o.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase));
        var reShadeByPrism = _profiles.Get(game.Id).ReShadeInstalled;
        var items = new Dictionary<string, CleanItem>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, CleanAction action, string source)
        {
            if (prism.Contains(path) || items.ContainsKey(path)) return;
            items[path] = new CleanItem { Path = path, Action = action, Source = source, Display = Relative(game, path) };
        }

        var dirs = Directories(game);

        // 1. Temoins « .original » de RHI : ils disent exactement quoi rendre au jeu.
        foreach (var dir in dirs.ToList())
        {
            foreach (var sentinel in SafeFiles(dir, "*" + OriginalSuffix))
            {
                var target = sentinel[..^OriginalSuffix.Length];
                if (prism.Contains(target)) continue;

                var empty = SafeLength(sentinel) == 0;
                // Temoin vide et fichier deja parti : il ne reste que le temoin.
                if (empty && !File.Exists(target)) Add(sentinel, CleanAction.Remove, "RHI");
                else Add(target, empty ? CleanAction.DropSentinel : CleanAction.RestoreOriginal, "RHI");
            }
        }

        // 2. Manifeste RHI : tout ce qu'il dit avoir pose, dossiers compris.
        foreach (var dir in dirs.ToList())
        {
            var manifest = Path.Combine(dir, RhiManifest);
            if (!File.Exists(manifest)) continue;

            var (files, folders) = ReadRhiManifest(manifest);
            foreach (var rel in files)
            {
                var path = Combine(dir, rel);
                if (path is not null && File.Exists(path) && !File.Exists(path + OriginalSuffix))
                    Add(path, CleanAction.Remove, "RHI");
            }
            foreach (var rel in folders)
                if (Combine(dir, rel) is { } folder && Directory.Exists(folder))
                    foreach (var f in SafeFilesDeep(folder)) Add(f, CleanAction.Remove, "RHI");
            Add(manifest, CleanAction.Remove, "RHI");
        }

        // 3. OptiScaler, sous n'importe quel nom de proxy, et ce qui l'accompagne.
        foreach (var dir in dirs)
        {
            var found = false;
            foreach (var name in OptiScalerProxyNames)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path) && IsOptiScaler(path)) { Add(path, CleanAction.Remove, "OptiScaler"); found = true; }
            }
            if (prismOptiScaler) continue;
            foreach (var name in OptiScalerFiles)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) { Add(path, CleanAction.Remove, "OptiScaler"); found = true; }
            }
            if (!found) continue;
            foreach (var folder in OptiScalerFolders.Select(f => Path.Combine(dir, f)).Where(Directory.Exists))
                foreach (var f in SafeFilesDeep(folder)) Add(f, CleanAction.Remove, "OptiScaler");
        }

        // 4. Mods reconnus a coup sur, poses a la main ou par un autre outil.
        var removesReShade = false;
        foreach (var mod in ForeignModScanner.Scan(game, prism, reShadeByPrism, origins).Where(m => m.Certain && m.Removable))
            foreach (var file in mod.Files)
            {
                Add(file, CleanAction.Remove, mod.Label);
                if (mod.Label == "ReShade" && ReShadeLocator.IsReShade(file)) removesReShade = true;
            }

        // 5. ReShade retire : ses shaders et son journal partent avec lui.
        if (removesReShade && !reShadeByPrism)
        {
            var target = DllInstaller.TargetDirectory(game);
            var log = Path.Combine(target, "ReShade.log");
            if (File.Exists(log)) Add(log, CleanAction.Remove, "ReShade");
            var shaders = Path.Combine(target, "reshade-shaders");
            if (Directory.Exists(shaders))
                foreach (var f in SafeFilesDeep(shaders)) Add(f, CleanAction.Remove, "ReShade");
        }

        return new CleanPlan
        {
            Items = items.Values
                .OrderBy(i => i.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Display, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    /// <summary>Restes d'un autre outil : temoins RHI ou manifeste. Sert aux conflits.</summary>
    public static bool HasRhiTraces(GameInfo game)
        => Directories(game).Any(d => File.Exists(Path.Combine(d, RhiManifest)) || SafeFiles(d, "*" + OriginalSuffix).Any());

    // ------------------------------------------------------------- Execution

    /// <summary>Applique le plan. Chaque fichier est copie a l'abri avant d'etre touche.</summary>
    public InstallResult Execute(GameInfo game, CleanPlan plan)
    {
        if (plan.IsEmpty) return new InstallResult(false, Loc.T("clean.none"));

        var root = Path.Combine(AppPaths.Cleanups, AppPaths.Sanitize(game.Id), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);

        var session = new CleanSession { GameId = game.Id, GameName = game.Name, At = DateTimeOffset.Now };
        var failed = new List<string>();
        var index = 0;

        foreach (var item in plan.Items)
        {
            try
            {
                string? stored = null;
                if (File.Exists(item.Path))
                {
                    stored = Path.Combine(root, $"{index++:D4}_{Path.GetFileName(item.Path)}");
                    File.Copy(item.Path, stored, overwrite: true);
                }

                var sentinel = item.Path + OriginalSuffix;
                switch (item.Action)
                {
                    case CleanAction.Remove:
                        Delete(item.Path);
                        break;
                    case CleanAction.DropSentinel:
                        Delete(item.Path);
                        Delete(sentinel);
                        break;
                    case CleanAction.RestoreOriginal:
                        Delete(item.Path);
                        File.Move(sentinel, item.Path);
                        break;
                }

                session.Records.Add(new CleanRecord { Path = item.Path, Action = item.Action, Stored = stored });
            }
            catch (Exception ex)
            {
                failed.Add(Path.GetFileName(item.Path));
                Log.Warn(Src, $"Cannot clean {item.Path}: {ex.Message}");
            }
        }

        RemoveEmptyFolders(plan);
        JsonStore.Save(Path.Combine(root, SessionFile), session);
        DllDetector.Inspect(game);

        var done = session.Records.Count;
        var msg = Loc.T("clean.done", game.Name, done);
        if (failed.Count > 0) msg += " " + Loc.T("restore.refused", string.Join(", ", failed));
        Log.Info(Src, $"{game.Name}: {done} file(s) cleaned, {failed.Count} refused, stored in {root}");
        return new InstallResult(done > 0, msg, done);
    }

    // ---------------------------------------------------------------- Annuler

    /// <summary>Dernier nettoyage encore annulable pour ce jeu.</summary>
    public CleanSession? LastSession(GameInfo game, out string? folder)
    {
        folder = null;
        var dir = Path.Combine(AppPaths.Cleanups, AppPaths.Sanitize(game.Id));
        if (!Directory.Exists(dir)) return null;

        foreach (var candidate in Directory.GetDirectories(dir).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var file = Path.Combine(candidate, SessionFile);
            if (!File.Exists(file)) continue;
            var session = JsonStore.Load<CleanSession?>(file, () => null);
            if (session is null || session.Records.Count == 0) continue;
            folder = candidate;
            return session;
        }
        return null;
    }

    /// <summary>Remet le jeu tel qu'il etait avant le dernier nettoyage, puis oublie ce passage.</summary>
    public InstallResult Undo(GameInfo game)
    {
        if (LastSession(game, out var folder) is not { } session || folder is null)
            return new InstallResult(false, Loc.T("clean.undo_none"));

        var restored = 0;
        var failed = new List<string>();

        // Dans l'ordre inverse : un dossier recree avant ses fichiers.
        foreach (var record in Enumerable.Reverse(session.Records))
        {
            try
            {
                var sentinel = record.Path + OriginalSuffix;
                switch (record.Action)
                {
                    case CleanAction.RestoreOriginal:
                        // L'original remis en place redevient « X.original ».
                        if (File.Exists(record.Path) && !File.Exists(sentinel)) File.Move(record.Path, sentinel);
                        break;
                    case CleanAction.DropSentinel:
                        if (!File.Exists(sentinel)) File.WriteAllBytes(sentinel, Array.Empty<byte>());
                        break;
                }

                if (record.Stored is not null && File.Exists(record.Stored))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(record.Path)!);
                    File.Copy(record.Stored, record.Path, overwrite: true);
                }
                restored++;
            }
            catch (Exception ex)
            {
                failed.Add(Path.GetFileName(record.Path));
                Log.Warn(Src, $"Cannot undo {record.Path}: {ex.Message}");
            }
        }

        if (failed.Count == 0)
            try { Directory.Delete(folder, recursive: true); } catch { /* garde la copie si le disque refuse */ }

        DllDetector.Inspect(game);
        var msg = Loc.T("clean.undone", game.Name, restored);
        if (failed.Count > 0) msg += " " + Loc.T("restore.refused", string.Join(", ", failed));
        Log.Info(Src, $"{game.Name}: cleanup undone, {restored} file(s) back");
        return new InstallResult(restored > 0, msg, restored);
    }

    // ---------------------------------------------------------------- Liste

    /// <summary>Tout ce qui s'ecarte de l'origine, en texte : pour verifier ou desinstaller a la main.</summary>
    public string FileList(GameInfo game, CleanPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {game.Name}");
        sb.AppendLine($"# {DllInstaller.TargetDirectory(game)}");
        sb.AppendLine($"# Prism {UpdateService.DisplayLabel} · {DateTime.Now:yyyy-MM-dd HH:mm}");

        var prism = _changes.For(game.Id).ToList();
        sb.AppendLine();
        sb.AppendLine($"## Prism ({prism.Count})");
        foreach (var c in prism.OrderBy(c => c.Origin).ThenBy(c => c.Path))
            sb.AppendLine($"{(c.Kind == ChangeKind.Replaced ? "replaced" : "added"),-9} {c.Origin,-24} {c.Path}");

        sb.AppendLine();
        sb.AppendLine($"## Outside Prism ({plan.Items.Count})");
        foreach (var i in plan.Items)
            sb.AppendLine($"{i.Action.ToString().ToLowerInvariant(),-15} {i.Source,-24} {i.Path}");
        return sb.ToString();
    }

    // ------------------------------------------------------------ Utilitaires

    /// <summary>Ou les mods agissent : a cote de l'executable, de Streamline, des DLL NGX.</summary>
    private static List<string> Directories(GameInfo game)
    {
        var dirs = new List<string> { DllInstaller.TargetDirectory(game) };
        dirs.AddRange(game.StreamlineDirectories);
        dirs.AddRange(game.Dlls.Select(d => Path.GetDirectoryName(d.Path)).OfType<string>());
        return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsOptiScaler(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(info.OriginalFilename?.Trim(), "OptiScaler.dll", StringComparison.OrdinalIgnoreCase)
                   || (info.ProductName ?? "").Contains("OptiScaler", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Lit files, folders et components.*.files ; un manifeste illisible ne donne rien.</summary>
    private static (List<string> Files, List<string> Folders) ReadRhiManifest(string path)
    {
        var files = new List<string>();
        var folders = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Strings(root, "files", files);
            Strings(root, "folders", folders);
            if (root.TryGetProperty("sharedFiles", out var shared) && shared.ValueKind == JsonValueKind.Object)
                files.AddRange(shared.EnumerateObject().Select(p => p.Name));
            if (root.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Object)
                foreach (var comp in comps.EnumerateObject())
                    Strings(comp.Value, "files", files);
        }
        catch (Exception ex) { Log.Warn(Src, $"Unreadable {path}: {ex.Message}"); }
        return (files, folders);

        static void Strings(JsonElement e, string name, List<string> into)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                into.AddRange(arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));
        }
    }

    /// <summary>Chemin relatif du manifeste, refuse s'il sort du dossier du jeu.</summary>
    private static string? Combine(string dir, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(dir, relative.Replace('/', '\\')));
        var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static void Delete(string path)
    {
        if (!File.Exists(path)) return;
        DllInstaller.ClearReadOnly(path);
        File.Delete(path);
    }

    /// <summary>Dossiers vides apres coup (OptiScaler\, reshade-shaders\...) : retires, du plus profond au plus haut.</summary>
    private static void RemoveEmptyFolders(CleanPlan plan)
    {
        var dirs = plan.Items.Select(i => Path.GetDirectoryName(i.Path)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(d => d.Length);
        foreach (var dir in dirs)
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { /* dossier encore utilise */ }
    }

    private static string Relative(GameInfo game, string path)
    {
        try { return Path.GetRelativePath(game.InstallDir, path); }
        catch { return path; }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFilesDeep(string dir)
    {
        try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories); }
        catch { return Array.Empty<string>(); }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return -1; }
    }
}
