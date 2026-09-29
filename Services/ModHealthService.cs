using System.Text.RegularExpressions;
using Prism.Core;
using Prism.Models;

namespace Prism.Services;

public enum HealthFixKind { None, NexusDownload, SteamInstall, Reinstall, OpenFile }

/// <summary>Une base (chargeur de mods) du jeu : presente, manquante, ou pas encore utile.</summary>
public sealed record FrameworkState(string Name, bool Installed, bool Needed, bool Optional, string? Note,
    long? NexusModId, long? SteamApp);

/// <summary>Un probleme trouve, et ce que Prism peut faire pour le corriger.</summary>
public sealed class HealthFinding
{
    public required string Id { get; init; }
    public required UiStatus Severity { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public HealthFixKind Fix { get; init; }
    /// <summary>Pour un telechargement Nexus : domaine, mod, nom ; pour une reinstallation : l'installation.</summary>
    public string? Domain { get; init; }
    public long? ModId { get; init; }
    public long? FileId { get; init; }
    public string? FixName { get; init; }
    public string? Origin { get; init; }
    public long? SteamApp { get; init; }
    public string? Path { get; init; }
}

public sealed class HealthReport
{
    public List<FrameworkState> Frameworks { get; } = new();
    public List<HealthFinding> Findings { get; } = new();
    public int Problems => Findings.Count(f => f.Severity is UiStatus.Error or UiStatus.Warning);
}

/// <summary>
/// Bilan des mods d'un jeu, pour que « j'installe → ca marche » soit vrai :
///  1. les bases : chaque chargeur exige par les mods presents est installe (regles du jeu : CET,
///     RED4ext, redscript, ArchiveXL, TweakXL, REDmod ; moteur : SKSE/F4SE..., UE4SS, BepInEx, MelonLoader) ;
///  2. l'integrite : chaque fichier pose par Prism pour un mod Nexus est encore la ;
///  3. les prerequis Nexus de chaque mod installe ;
///  4. les conflits (noms de DLL, doublons) ;
///  5. les journaux des chargeurs apres le dernier lancement (erreurs RED4ext, redscript, CET).
/// </summary>
public sealed class ModHealthService
{
    private const string Src = "health";

    private readonly DeploymentStore _deployments;
    private readonly NexusRequirements _requirements;
    private readonly NexusService _nexus;

    public ModHealthService(DeploymentStore deployments, NexusRequirements requirements, NexusService nexus)
    {
        _deployments = deployments;
        _requirements = requirements;
        _nexus = nexus;
    }

    public async Task<HealthReport> CheckAsync(GameInfo game, CancellationToken ct = default)
    {
        var report = new HealthReport();
        var root = game.InstallDir;
        var exeDir = DllInstaller.TargetDirectory(game);
        var domain = _nexus.CachedDomain(game) ?? await _nexus.DomainForAsync(game, ct);
        var rule = ModRules.ForGame(game) ?? ModRules.For(domain);

        // 1. Bases
        foreach (var fw in rule?.Frameworks ?? new List<FrameworkRule>())
        {
            var installed = fw.Files.All(f => Present(root, f));
            var needed = Needed(root, fw);
            report.Frameworks.Add(new FrameworkState(fw.Name, installed, needed, fw.Optional, fw.Note is null ? null : Loc.T(fw.Note), fw.Nexus, fw.SteamApp));
            if (installed || (!needed && !fw.Optional)) continue;
            report.Findings.Add(new HealthFinding
            {
                Id = "fw-" + fw.Id,
                Severity = needed ? UiStatus.Error : UiStatus.Warning,
                Title = needed ? Loc.T("health.fw_missing", fw.Name) : Loc.T("health.fw_optional", fw.Name),
                Detail = fw.Note is null ? null : Loc.T(fw.Note),
                Fix = fw.SteamApp is not null ? HealthFixKind.SteamInstall : fw.Nexus is not null ? HealthFixKind.NexusDownload : HealthFixKind.None,
                Domain = domain, ModId = fw.Nexus, FixName = fw.Name, SteamApp = fw.SteamApp
            });
        }
        foreach (var f in EngineFrameworks(game, root, exeDir)) { report.Frameworks.Add(f.State); if (f.Finding is not null) report.Findings.Add(f.Finding); }

        // 2. Integrite et 3. prerequis des mods Nexus poses par Prism
        var frameworkMods = report.Frameworks.Where(f => f.NexusModId is not null).Select(f => f.NexusModId!.Value).ToHashSet();
        var requested = new HashSet<long>();
        foreach (var install in NexusModInstaller.Installed().Where(i => i.GameId == game.Id))
        {
            var files = _deployments.For(game.Id).Where(e => e.Origin == install.Origin).ToList();
            if (files.Count == 0) continue;   // retire : plus rien a verifier
            var missing = files.Where(e => !File.Exists(e.Path)).ToList();
            var name = install.Origin.StartsWith("Nexus · ", StringComparison.Ordinal) ? install.Origin[8..] : install.Origin;
            if (missing.Count > 0)
                report.Findings.Add(new HealthFinding
                {
                    Id = "files-" + install.Origin, Severity = UiStatus.Error,
                    Title = Loc.T("health.files_missing", name, missing.Count, files.Count),
                    Detail = string.Join(", ", missing.Take(4).Select(m => System.IO.Path.GetFileName(m.Path))),
                    Fix = HealthFixKind.Reinstall, Domain = install.Domain,
                    ModId = install.ModId ?? NexusService.ParseArchiveName(install.Archive).ModId,
                    FileId = install.FileId, FixName = name, Origin = install.Origin
                });

            var modId = install.ModId ?? NexusService.ParseArchiveName(install.Archive).ModId;
            if (install.Domain is null || modId is null) continue;
            try
            {
                foreach (var req in await _requirements.AllAsync(install.Domain, modId.Value, ct))
                {
                    if (req.External || frameworkMods.Contains(req.ModId) || !requested.Add(req.ModId)) continue;
                    if (await _requirements.StateAsync(game, req, ct) != RequirementState.Missing) continue;
                    report.Findings.Add(new HealthFinding
                    {
                        Id = "req-" + req.ModId, Severity = UiStatus.Error,
                        Title = Loc.T("health.req_missing", name, req.Name),
                        Fix = HealthFixKind.NexusDownload, Domain = req.GameDomain, ModId = req.ModId, FixName = req.Name
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn(Src, $"Requirements of {name}: {ex.Message}"); }
        }

        // 4. Conflits
        foreach (var c in ConflictService.Evaluate(game))
            report.Findings.Add(new HealthFinding { Id = c.Id, Severity = c.State, Title = c.Title, Detail = c.Evidence });

        // 5. Journaux du dernier lancement
        foreach (var log in rule?.Logs ?? new List<LogRule>())
        {
            var dir = System.IO.Path.Combine(root, log.Dir.Replace('/', System.IO.Path.DirectorySeparatorChar));
            string? latest;
            try { latest = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, log.Pattern).OrderByDescending(File.GetLastWriteTime).FirstOrDefault() : null; }
            catch { latest = null; }
            if (latest is null) continue;
            var errors = ReadErrors(latest, log.Error);
            if (errors.Count == 0) continue;
            report.Findings.Add(new HealthFinding
            {
                Id = "log-" + System.IO.Path.GetFileName(latest), Severity = UiStatus.Warning,
                Title = Loc.T("health.log_errors", System.IO.Path.GetFileName(latest), errors.Count),
                Detail = errors[^1], Fix = HealthFixKind.OpenFile, Path = latest
            });
        }

        Log.Info(Src, $"{game.Name}: {report.Frameworks.Count} base(s), {report.Problems} problem(s)");
        return report;
    }

    /// <summary>Lignes d'erreur d'un journal (lu partage : le jeu peut l'avoir ouvert).</summary>
    private static List<string> ReadErrors(string path, string pattern)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            var list = new List<string>();
            for (string? line; (line = reader.ReadLine()) is not null;)
                if (regex.IsMatch(line)) list.Add(line.Length > 220 ? line[..220] + "…" : line.Trim());
            return list;
        }
        catch { return new List<string>(); }
    }

    private static bool Present(string root, string rel)
    {
        var path = System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return false;
        // Un nom de proxy occupe par OptiScaler n'est pas le chargeur attendu (winmm.dll de RED4ext).
        return !LeftoverCleaner.IsOptiScaler(path);
    }

    private static bool Needed(string root, FrameworkRule fw)
    {
        if (fw.NeededDir is null) return false;
        var dir = System.IO.Path.Combine(root, fw.NeededDir.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir)) return false;
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(20000).Any(f =>
                (fw.NeededFile is not null && System.IO.Path.GetFileName(f).Equals(fw.NeededFile, StringComparison.OrdinalIgnoreCase))
                || (fw.NeededExt is not null && fw.NeededExt.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                    && !IsOwnFile(root, fw, f)));
        }
        catch { return false; }
    }

    /// <summary>Les fichiers du chargeur lui-meme ne l'exigent pas (RED4ext n'est pas « exige » par sa propre DLL).</summary>
    private static bool IsOwnFile(string root, FrameworkRule fw, string file)
        => fw.Files.Any(f => System.IO.Path.GetFullPath(System.IO.Path.Combine(root, f)).Equals(System.IO.Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Chargeurs reconnus par le moteur, pour tous les jeux : extenseurs de script Bethesda (plugins dans
    /// Data\SKSE\Plugins sans skse64_loader.exe...), mods UE4SS sans UE4SS, plugins BepInEx ou MelonLoader
    /// sans leur noyau.
    /// </summary>
    private static IEnumerable<(FrameworkState State, HealthFinding? Finding)> EngineFrameworks(GameInfo game, string root, string exeDir)
    {
        (FrameworkState, HealthFinding?) Make(string id, string name, bool installed, bool needed)
            => (new FrameworkState(name, installed, needed, false, null, null, null),
                !installed && needed
                    ? new HealthFinding { Id = "fw-" + id, Severity = UiStatus.Error, Title = Loc.T("health.fw_missing", name), Detail = Loc.T("health.fw_manual") }
                    : null);

        if (NexusModInstaller.DataRoot(root) is { } data)
        {
            foreach (var (folder, name, loaders) in new[]
                     {
                         ("SKSE", "SKSE", new[] { "skse64_loader.exe", "skse_loader.exe", "sksevr_loader.exe" }),
                         ("F4SE", "F4SE", new[] { "f4se_loader.exe", "f4sevr_loader.exe" }),
                         ("SFSE", "SFSE", new[] { "sfse_loader.exe" }),
                         ("NVSE", "xNVSE", new[] { "nvse_loader.exe" }),
                         ("OBSE", "OBSE", new[] { "obse_loader.exe" })
                     })
            {
                var plugins = System.IO.Path.Combine(data, folder, "Plugins");
                var needed = Directory.Exists(plugins) && SafeAny(plugins, "*.dll");
                var installed = loaders.Any(l => File.Exists(System.IO.Path.Combine(root, l)));
                if (needed || installed) yield return Make(folder.ToLowerInvariant(), name, installed, needed);
            }
        }

        var ue4ssMods = new[] { System.IO.Path.Combine(exeDir, "ue4ss", "Mods"), System.IO.Path.Combine(exeDir, "Mods") }.FirstOrDefault(Directory.Exists);
        if (ue4ssMods is not null && SafeAny(ue4ssMods, "main.lua"))
        {
            var installed = File.Exists(System.IO.Path.Combine(exeDir, "UE4SS.dll")) || File.Exists(System.IO.Path.Combine(exeDir, "ue4ss", "UE4SS.dll"));
            yield return Make("ue4ss", "UE4SS", installed, true);
        }

        var bepPlugins = System.IO.Path.Combine(root, "BepInEx", "plugins");
        if (Directory.Exists(bepPlugins) && SafeAny(bepPlugins, "*.dll"))
            yield return Make("bepinex", "BepInEx", SafeAny(System.IO.Path.Combine(root, "BepInEx", "core"), "BepInEx*.dll"), true);

        var melonMods = System.IO.Path.Combine(root, "Mods");
        if (Directory.Exists(System.IO.Path.Combine(root, "MelonLoader")) || (Directory.Exists(melonMods) && SafeAny(melonMods, "*.dll") && File.Exists(System.IO.Path.Combine(root, "UnityPlayer.dll"))))
            yield return Make("melonloader", "MelonLoader", Directory.Exists(System.IO.Path.Combine(root, "MelonLoader")), SafeAny(melonMods, "*.dll"));
    }

    private static bool SafeAny(string dir, string pattern)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }
}
