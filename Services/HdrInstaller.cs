using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Applique un plan HDR : l'addon RenoDX, les cles <c>[renodx]</c> de ReShade.ini et,
/// pour Unreal, l'Engine.ini. Chaque ecriture est retenue pour etre defaite a
/// l'identique — valeurs precedentes des cles, sauvegarde de l'Engine.ini.
/// </summary>
public sealed class HdrInstaller
{
    private const string Src = "hdr";

    /// <summary>Section des reglages globaux des mods RenoDX dans ReShade.ini.</summary>
    public const string Section = "renodx";

    private readonly DownloadService _downloads;
    private readonly DeploymentStore _deployments;
    private readonly BackupService _backups;
    private readonly ProfileStore _profiles;

    public HdrInstaller(DownloadService downloads, DeploymentStore deployments, BackupService backups, ProfileStore profiles)
    {
        _downloads = downloads;
        _deployments = deployments;
        _backups = backups;
        _profiles = profiles;
    }

    // ----------------------------------------------------------------- Etat

    /// <summary>Etat reel de chaque etape, lu sur disque.</summary>
    public void Evaluate(GameInfo game, HdrPlan plan)
    {
        var dir = DllInstaller.TargetDirectory(game);
        var iniPath = ReShadeConfig.PathFor(dir);
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath) : Array.Empty<string>();
        var engineIni = EngineIniPath(game);
        var engineLines = engineIni is not null && File.Exists(engineIni) ? File.ReadAllLines(engineIni) : Array.Empty<string>();

        foreach (var step in plan.Steps)
        {
            step.State = step.Kind switch
            {
                HdrStepKind.Requirement => ReShadeReady(game) ? UiStatus.Ready : UiStatus.Error,
                HdrStepKind.Addon => plan.AddonFileName is not null && File.Exists(Path.Combine(dir, plan.AddonFileName))
                    ? UiStatus.Ready : UiStatus.Idle,
                HdrStepKind.ReShadeKey => IniFile.Read(lines, Section, step.Key!) == step.Value
                    ? UiStatus.Ready : UiStatus.Idle,
                HdrStepKind.EngineIni => engineIni is null ? UiStatus.Warning
                    : step.IniLines.All(l => IniFile.Read(engineLines, l.Section, l.Key) == l.Value) ? UiStatus.Ready
                    : UiStatus.Idle,
                _ => UiStatus.Idle
            };
        }
    }

    /// <summary>ReShade 6.8 ou plus recent, exige par la page Mods du wiki.</summary>
    public static bool ReShadeReady(GameInfo game) => ReShadeLocator.Scan(game).Ready;

    // ------------------------------------------------------------ Application

    public async Task<InstallResult> ApplyAsync(
        GameInfo game, HdrPlan plan, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!plan.CanInstall) return new InstallResult(false, plan.BlockedReason ?? Loc.T("hdr.none"));
        if (!ReShadeReady(game)) return new InstallResult(false, Loc.T("hdr.err.reshade"));
        if (GameGuard.Check(game) is { } blocked) return blocked;

        var dir = DllInstaller.TargetDirectory(game);
        if (!Directory.Exists(dir)) return new InstallResult(false, Loc.T("err.target_missing"));

        var profile = _profiles.Get(game.Id);
        var file = plan.AddonFileName!;

        // Un seul mod RenoDX de jeu par dossier : ReShade refuse le second et les deux
        // ecriraient les memes cles. Ceux que Prism a poses sont remplaces ; les autres
        // appartiennent a l'utilisateur et bloquent.
        var others = OtherGameMods(dir, file);
        var foreign = others.Where(p => !_deployments.WasDeployed(p)).Select(Path.GetFileName).ToList();
        if (foreign.Count > 0)
            return new InstallResult(false, Loc.T("hdr.err.foreign", string.Join(", ", foreign)));

        try
        {
            // Les builds snapshot evoluent sous le meme nom : un cache de plus de six
            // heures est retelecharge.
            var uri = new Uri(plan.AddonUrl!);
            var cache = Path.Combine(AppPaths.ComponentCache, "renodx-mods", AppPaths.Sanitize(uri.Host), file);
            if (File.Exists(cache) && DateTime.Now - File.GetLastWriteTime(cache) > TimeSpan.FromHours(6))
                File.Delete(cache);

            await _downloads.DownloadAsync(plan.AddonUrl!, cache, null, progress, ct);

            // Ancien mod Prism retire et nouveau pose d'un seul geste : un echec ne laisse
            // le jeu ni sans mod, ni avec les deux.
            var dest = Path.Combine(dir, file);
            var tx = new FileTransaction(game, _backups, _deployments, "RenoDX HDR");
            foreach (var old in others) tx.Delete(old);
            tx.Copy(cache, dest, "RenoDX HDR", plan.Title);
            var written = tx.Commit();
            if (!written.Success) return written;

            profile.HdrAddonPath = dest;
            profile.HdrKind = plan.Kind;
            profile.RenoDxAddon = file;

            var applied = WriteKeys(dir, plan, profile);

            string? engineNote = null;
            foreach (var step in plan.Steps.Where(s => s.Kind == HdrStepKind.EngineIni))
            {
                var r = WriteEngineIni(game, step, profile);
                if (r.Success) applied++;
                else engineNote = r.Message;
            }

            _profiles.Update(profile);
            DllDetector.Inspect(game);
            Evaluate(game, plan);

            var msg = Loc.T("hdr.ok.applied", plan.Title, applied);
            if (engineNote is not null) msg += " " + engineNote;

            Log.Info(Src, $"{plan.Title}: {file} placed, {applied} setting(s) written");
            return new InstallResult(true, msg, 1 + applied);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(Src, $"HDR install failed: {ex.Message}");
            return new InstallResult(false, Loc.T("err.install_failed", ex.Message));
        }
    }

    private static int WriteKeys(string dir, HdrPlan plan, GameProfile profile)
    {
        var steps = plan.Steps.Where(s => s.Kind == HdrStepKind.ReShadeKey && s.Key is not null && s.Value is not null).ToList();
        if (steps.Count == 0) return 0;

        var path = ReShadeConfig.PathFor(dir);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();

        foreach (var step in steps)
        {
            // La valeur d'origine n'est retenue qu'une fois : une seconde application ne
            // doit pas faire oublier ce que l'utilisateur avait avant Prism.
            if (!profile.HdrPreviousKeys.ContainsKey(step.Key!))
                profile.HdrPreviousKeys[step.Key!] = IniFile.Read(lines, Section, step.Key!);

            IniFile.Set(lines, Section, step.Key!, step.Value!);
        }

        DllInstaller.ClearReadOnly(path);
        File.WriteAllLines(path, lines);
        return steps.Count;
    }

    private InstallResult WriteEngineIni(GameInfo game, HdrStep step, GameProfile profile)
    {
        var path = EngineIniPath(game);
        if (path is null) return new InstallResult(false, Loc.T("hdr.err.engine_ini_missing"));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var existed = File.Exists(path);

            if (existed)
            {
                DllInstaller.ClearReadOnly(path);
                // Premiere ecriture de Prism dans ce fichier : l'original part en sauvegarde.
                if (!_deployments.WasDeployed(path) && profile.HdrEngineIniPath is null)
                    _backups.Capture(game, path, "RenoDX HDR");
            }

            var lines = existed ? File.ReadAllLines(path).ToList() : new List<string>();
            foreach (var (section, key, value) in step.IniLines)
                IniFile.Set(lines, section, key, value);

            File.WriteAllLines(path, lines);

            // Le wiki demande un Engine.ini en lecture seule : le jeu le reecrirait sinon.
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

            if (!existed)
            {
                _deployments.Record(game, path, "Engine.ini", "RenoDX HDR");
                profile.HdrEngineIniCreated = true;
            }

            profile.HdrEngineIniPath = path;
            return new InstallResult(true, "", 1);
        }
        catch (Exception ex)
        {
            Log.Warn(Src, $"Engine.ini not written: {ex.Message}");
            return new InstallResult(false, Loc.T("err.write_failed", Path.GetFileName(path), ex.Message));
        }
    }

    // --------------------------------------------------------------- Retrait

    /// <summary>Retire le mod HDR et remet ReShade.ini et Engine.ini dans leur etat d'avant.</summary>
    public InstallResult Remove(GameInfo game)
    {
        var profile = _profiles.Get(game.Id);
        var dir = DllInstaller.TargetDirectory(game);
        var removed = 0;

        try
        {
            if (profile.HdrAddonPath is { } addon)
            {
                if (RemoveDeployed(addon)) removed++;
            }
            else
            {
                foreach (var p in OtherGameMods(dir, "").Where(_deployments.WasDeployed))
                    if (RemoveDeployed(p)) removed++;
            }

            removed += ForgetTraces(game, profile);

            DllDetector.Inspect(game);
            return removed > 0
                ? new InstallResult(true, Loc.T("hdr.ok.removed"), removed)
                : new InstallResult(false, Loc.T("hdr.none_installed"));
        }
        catch (Exception ex)
        {
            return new InstallResult(false, Loc.T("err.remove_failed", ex.Message));
        }
    }

    /// <summary>
    /// Defait les cles et l'Engine.ini, puis oublie le tout. Appele aussi par le retour
    /// vanille, qui s'occupe lui-meme des fichiers.
    /// </summary>
    public int ForgetTraces(GameInfo game, GameProfile? profile = null)
    {
        profile ??= _profiles.Get(game.Id);
        var dir = DllInstaller.TargetDirectory(game);
        var count = 0;

        if (profile.HdrPreviousKeys.Count > 0)
        {
            var path = ReShadeConfig.PathFor(dir);
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path).ToList();
                foreach (var (key, previous) in profile.HdrPreviousKeys)
                {
                    if (previous is null) IniFile.Remove(lines, Section, key);
                    else IniFile.Set(lines, Section, key, previous);
                    count++;
                }
                DllInstaller.ClearReadOnly(path);
                File.WriteAllLines(path, lines);
            }
        }

        if (profile.HdrEngineIniPath is { } engine && File.Exists(engine))
        {
            DllInstaller.ClearReadOnly(engine);

            var backup = _backups.For(game.Id)
                .FirstOrDefault(b => string.Equals(b.OriginalPath, engine, StringComparison.OrdinalIgnoreCase));

            if (backup is not null && _backups.Restore(backup))
            {
                _backups.Forget(backup);
                count++;
            }
            else if (profile.HdrEngineIniCreated)
            {
                // Seul un Engine.ini que Prism a cree de toutes pieces est supprime. Un
                // fichier deja restaure par le retour vanille, lui, reste en place.
                var entry = _deployments.All.FirstOrDefault(e =>
                    string.Equals(e.Path, engine, StringComparison.OrdinalIgnoreCase));
                if (entry is not null && _deployments.Remove(entry)) count++;
            }
        }

        profile.HdrAddonPath = null;
        profile.HdrKind = null;
        profile.HdrPreviousKeys.Clear();
        profile.HdrEngineIniPath = null;
        profile.HdrEngineIniCreated = false;
        profile.RenoDxAddon = null;
        _profiles.Update(profile);

        return count;
    }

    private bool RemoveDeployed(string path)
    {
        DllInstaller.ClearReadOnly(path);
        var entry = _deployments.All.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) return _deployments.Remove(entry);

        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // ------------------------------------------------------------- Emplacements

    /// <summary>
    /// Mods RenoDX de jeu presents dans le dossier, hors le fichier vise. L'addon DLSS 5,
    /// DLSSFIX et MFG Unlock ne sont pas des mods de jeu et cohabitent.
    /// </summary>
    private static List<string> OtherGameMods(string dir, string ours)
    {
        if (!Directory.Exists(dir)) return new List<string>();

        return Directory.EnumerateFiles(dir, "renodx-*.addon*")
            .Where(p =>
            {
                var n = Path.GetFileName(p).ToLowerInvariant();
                return (n.EndsWith(".addon64") || n.EndsWith(".addon32"))
                       && !n.StartsWith("renodx-dlss5") && !n.StartsWith("renodx-dlssfix")
                       && !n.StartsWith("renodx-dlss.") && !n.StartsWith("renodx-dlss_")
                       && !n.Contains("mfgunlock")
                       && !n.Equals(ours, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
    }

    /// <summary>
    /// L'Engine.ini d'un jeu Unreal : <c>&lt;Projet&gt;\Saved\Config\&lt;Plateforme&gt;</c>, sous
    /// %LOCALAPPDATA% le plus souvent. Le projet est le dossier qui contient
    /// <c>Binaries</c>. Null si le jeu n'a encore jamais cree son dossier de configuration.
    /// </summary>
    public static string? EngineIniPath(GameInfo game)
    {
        if (string.IsNullOrWhiteSpace(game.Executable)) return null;

        string? project = null;
        for (var dir = Path.GetDirectoryName(game.Executable); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (Path.GetFileName(dir).Equals("Binaries", StringComparison.OrdinalIgnoreCase))
            {
                project = Path.GetFileName(Path.GetDirectoryName(dir));
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(project)) return null;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), project, "Saved", "Config"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", project, "Saved", "Config"),
            Path.Combine(profile, "Saved Games", project, "Saved", "Config")
        };
        var platforms = new[] { "Windows", "WindowsClient", "WinGDK", "WindowsNoEditor" };

        foreach (var root in roots)
            foreach (var platform in platforms)
                if (Directory.Exists(Path.Combine(root, platform)))
                    return Path.Combine(root, platform, "Engine.ini");

        foreach (var root in roots)
            if (Directory.Exists(root))
                return Path.Combine(root, "Windows", "Engine.ini");

        return null;
    }
}
