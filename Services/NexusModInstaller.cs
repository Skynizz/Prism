using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>Ou poser un mod.</summary>
public enum ModLayout
{
    /// <summary>L'archive reproduit l'arborescence du jeu (bin\, archive\, Engine\, &lt;Projet&gt;\...).</summary>
    GameRoot,
    /// <summary>A cote de l'executable : addons ReShade, preset, .asi, DLL proxy.</summary>
    ExeDir,
    /// <summary>Archives Unreal seules (.pak, .utoc, .ucas) : &lt;Projet&gt;\Content\Paks\~mods.</summary>
    UnrealPaks,
    /// <summary>Rien de sur : l'utilisateur choisit.</summary>
    Unknown
}

/// <summary>Ce que Prism compte faire d'une archive, avant d'ecrire quoi que ce soit.</summary>
public sealed class ModPlan
{
    public required string Archive { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Domain { get; init; }
    public long? ModId { get; init; }

    /// <summary>Racine utile de l'archive extraite, emballages retires.</summary>
    public required string Source { get; init; }
    public ModLayout Layout { get; set; }
    public string? Target { get; set; }

    /// <summary>Chemins relatifs a <see cref="Source"/>, notices exclues.</summary>
    public List<string> Files { get; } = new();

    /// <summary>Un addon, un preset ou un shader ReShade : ReShade doit etre en place.</summary>
    public bool NeedsReShade { get; set; }

    /// <summary>Pourquoi l'emplacement est incertain, ou pourquoi l'installation est refusee.</summary>
    public string? Note { get; set; }
    public bool Blocked { get; set; }

    public string Origin => $"Nexus · {Name}";
}

/// <summary>Trace d'un mod Nexus installe, pour retrouver sa page et ses mises a jour.</summary>
public sealed class NexusInstall
{
    public string GameId { get; set; } = "";
    public string Origin { get; set; } = "";
    public string? Domain { get; set; }
    public long? ModId { get; set; }
    public string? Version { get; set; }
    public string Archive { get; set; } = "";
    public string Target { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
}

/// <summary>
/// Pose un mod recu de Nexus au bon endroit. L'archive est d'abord extraite et lue :
///  - elle reproduit l'arborescence du jeu → racine du jeu ;
///  - elle ne contient que des archives Unreal → &lt;Projet&gt;\Content\Paks\~mods ;
///  - addons, presets, .asi, DLL proxy, dossiers deja presents a cote de l'exe → dossier de l'exe ;
///  - sinon, et pour un installeur FOMOD, l'utilisateur choisit.
/// L'ecriture passe par <see cref="FileTransaction"/> : originaux sauvegardes, tout ou rien,
/// et chaque fichier est inscrit sous l'origine « Nexus · nom » pour un retrait en un clic.
/// </summary>
public sealed class NexusModInstaller
{
    private const string Src = "nexus";

    private readonly BackupService _backups;
    private readonly DeploymentStore _deployments;

    public NexusModInstaller(BackupService backups, DeploymentStore deployments)
    {
        _backups = backups;
        _deployments = deployments;
    }

    private static string StoreFile => Path.Combine(AppPaths.Root, "nexus-installs.json");

    public static List<NexusInstall> Installed() => JsonStore.Load(StoreFile, () => new List<NexusInstall>());

    /// <summary>Fichiers du dossier de l'exe que tout jeu Unreal ou autre possede deja : ils ne disent rien de la cible.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    { "bin", "data", "content", "plugins", "config", "x64", "win64" };

    private static readonly string[] ProxyDlls =
    { "dxgi.dll", "d3d9.dll", "d3d11.dll", "d3d12.dll", "dinput8.dll", "version.dll", "winmm.dll", "dwmapi.dll", "winhttp.dll", "opengl32.dll" };

    private static readonly string[] UnrealArchives = { ".pak", ".utoc", ".ucas", ".sig" };

    // ------------------------------------------------------------------ Analyse

    public async Task<ModPlan> AnalyzeAsync(GameInfo game, string archive, string? domain, string? pageTitle, CancellationToken ct = default)
    {
        var (fileName, modId, version) = NexusService.ParseArchiveName(archive);
        var name = NexusService.ModNameFromTitle(pageTitle) ?? fileName;

        var work = Path.Combine(NexusService.Downloads, "extract", AppPaths.Sanitize(Path.GetFileNameWithoutExtension(archive)));
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        await ArchiveExtractor.ExtractAsync(archive, work, ct);

        var source = Unwrap(work, game);
        var plan = new ModPlan
        {
            Archive = archive, Name = name, Version = version, Domain = domain, ModId = modId, Source = source
        };

        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, f);
            // Notices a la racine de l'archive : jamais utiles au jeu.
            if (!rel.Contains(Path.DirectorySeparatorChar) && !ArchiveExtractor.IsPayload(rel)) continue;
            plan.Files.Add(rel);
        }

        if (plan.Files.Count == 0)
        {
            plan.Blocked = true;
            plan.Note = Loc.T("nexus.err.empty");
            return plan;
        }

        Classify(game, plan);
        CheckArchitecture(game, plan);
        Log.Info(Src, $"{name}: {plan.Files.Count} file(s), layout {plan.Layout} → {plan.Target ?? "?"}");
        return plan;
    }

    /// <summary>Descend les dossiers d'emballage (« Mod v1.2\ ») tant qu'ils ne sont pas des dossiers du jeu.</summary>
    private static string Unwrap(string dir, GameInfo game)
    {
        var exeDir = DllInstaller.TargetDirectory(game);
        for (var depth = 0; depth < 4; depth++)
        {
            var files = Directory.GetFiles(dir).Where(f => ArchiveExtractor.IsPayload(f)).ToList();
            var dirs = Directory.GetDirectories(dir);
            if (files.Count > 0 || dirs.Length != 1) break;
            var only = Path.GetFileName(dirs[0]);
            if (only.Equals("fomod", StringComparison.OrdinalIgnoreCase)) break;
            if (Directory.Exists(Path.Combine(game.InstallDir, only)) || Directory.Exists(Path.Combine(exeDir, only))) break;
            dir = dirs[0];
        }
        return dir;
    }

    private static void Classify(GameInfo game, ModPlan plan)
    {
        var root = game.InstallDir;
        var exeDir = DllInstaller.TargetDirectory(game);
        var top = plan.Files.Select(f => f.Split(Path.DirectorySeparatorChar)[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var topDirs = top.Where(t => Directory.Exists(Path.Combine(plan.Source, t))).ToList();
        var exts = plan.Files.Select(f => Path.GetExtension(f).ToLowerInvariant()).ToList();

        plan.NeedsReShade = exts.Any(e => e is ".addon64" or ".addon32" or ".fx" or ".fxh")
                            || plan.Files.Any(f => f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && IsReShadePreset(Path.Combine(plan.Source, f)));

        if (Directory.Exists(Path.Combine(plan.Source, "fomod")))
        {
            plan.Layout = ModLayout.Unknown;
            plan.Note = Loc.T("nexus.note.fomod");
            return;
        }

        // Dossiers propres a cote de l'exe (reshade-shaders, ue4ss...) quand l'exe n'est pas a la racine.
        bool ExistsIn(string baseDir, string sub) => Directory.Exists(Path.Combine(baseDir, sub));
        if (!SameDir(exeDir, root) && topDirs.Any(d => !Generic.Contains(d) && ExistsIn(exeDir, d)))
        {
            Set(plan, ModLayout.ExeDir, exeDir);
            return;
        }

        if (topDirs.Any(d => ExistsIn(root, d)))
        {
            Set(plan, ModLayout.GameRoot, root);
            return;
        }

        if (exts.All(e => UnrealArchives.Contains(e)) && PaksDir(game) is { } paks)
        {
            // Aplatis : ~mods ne lit que son propre niveau et un sous-dossier par mod.
            Set(plan, ModLayout.UnrealPaks, Path.Combine(paks, "~mods"));
            return;
        }

        var exeSide = plan.Files.All(f =>
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            return ext is ".addon64" or ".addon32" or ".asi" or ".ini" or ".toml" or ".json" or ".fx" or ".fxh" or ".dll"
                   || f.StartsWith("reshade-shaders", StringComparison.OrdinalIgnoreCase)
                   || f.StartsWith("Shaders", StringComparison.OrdinalIgnoreCase)
                   || f.StartsWith("Textures", StringComparison.OrdinalIgnoreCase);
        });
        if (exeSide || plan.Files.Any(f => ProxyDlls.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)))
        {
            Set(plan, ModLayout.ExeDir, exeDir);
            return;
        }

        plan.Layout = ModLayout.Unknown;
        plan.Note = Loc.T("nexus.note.unknown");
    }

    private static void Set(ModPlan plan, ModLayout layout, string target)
    {
        plan.Layout = layout;
        plan.Target = target;
    }

    /// <summary>Un addon 32 bits dans un jeu 64 bits (ou l'inverse) ne serait jamais charge.</summary>
    private static void CheckArchitecture(GameInfo game, ModPlan plan)
    {
        var is32 = PeInfo.Is32Bit(game.Executable);
        var wrong = plan.Files.FirstOrDefault(f => f.EndsWith(is32 ? ".addon64" : ".addon32", StringComparison.OrdinalIgnoreCase));
        if (wrong is null) return;
        plan.Blocked = true;
        plan.Note = Loc.T("nexus.err.arch", Path.GetFileName(wrong));
    }

    /// <summary>Preset ReShade : un .ini qui declare ses techniques.</summary>
    private static bool IsReShadePreset(string path)
    {
        try { return File.ReadLines(path).Take(200).Any(l => l.StartsWith("Techniques=", StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    /// <summary>&lt;Projet&gt;\Content\Paks du jeu, celui qui porte le plus gros .pak.</summary>
    public static string? PaksDir(GameInfo game)
    {
        try
        {
            return Directory.EnumerateDirectories(game.InstallDir)
                .Where(d => !Path.GetFileName(d).Equals("Engine", StringComparison.OrdinalIgnoreCase))
                .Select(d => Path.Combine(d, "Content", "Paks"))
                .Where(Directory.Exists)
                .OrderByDescending(d => Directory.EnumerateFiles(d, "*.pak").Select(f => new FileInfo(f).Length).DefaultIfEmpty(0).Max())
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static bool SameDir(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------- Installation

    public InstallResult Install(GameInfo game, ModPlan plan)
    {
        if (plan.Blocked) return new InstallResult(false, plan.Note ?? Loc.T("nexus.err.empty"));
        if (plan.Target is null) return new InstallResult(false, Loc.T("nexus.note.unknown"));

        // Deja installe sous ce nom : l'ancienne version part dans la meme transaction.
        var previous = _deployments.For(game.Id)
            .Where(e => string.Equals(e.Origin, plan.Origin, StringComparison.Ordinal))
            .Select(e => e.Path).ToList();

        var tx = new FileTransaction(game, _backups, _deployments, plan.Origin);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in plan.Files)
        {
            var relDest = plan.Layout == ModLayout.UnrealPaks ? Path.GetFileName(rel) : rel;
            var dest = Path.GetFullPath(Path.Combine(plan.Target, relDest));
            // Garde-fou : rien ne sort du dossier cible.
            if (!dest.StartsWith(Path.GetFullPath(plan.Target), StringComparison.OrdinalIgnoreCase)) continue;
            tx.Copy(Path.Combine(plan.Source, rel), dest, plan.Name, plan.Version ?? "");
            targets.Add(dest);
        }
        foreach (var old in previous.Where(p => !targets.Contains(p))) tx.Delete(old);

        var result = tx.Commit();
        if (!result.Success) return result;

        var list = Installed();
        list.RemoveAll(i => i.GameId == game.Id && i.Origin == plan.Origin);
        list.Add(new NexusInstall
        {
            GameId = game.Id, Origin = plan.Origin, Domain = plan.Domain, ModId = plan.ModId, Version = plan.Version,
            Archive = Path.GetFileName(plan.Archive), Target = plan.Target, InstalledAt = DateTimeOffset.Now
        });
        JsonStore.Save(StoreFile, list);

        Log.Info(Src, $"{plan.Origin}: {targets.Count} file(s) → {plan.Target}");
        return new InstallResult(true, Loc.T("nexus.ok", plan.Name, targets.Count, plan.Target), targets.Count);
    }
}
