using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Recupere et pose ReShade, version add-on — la seule qui charge les .addon64 dont
/// RenoDX depend.
///
/// Inspire de RHI plutot que de l'installeur interactif :
///  - un ReShade add-on 6.8+ deja charge par le jeu, sous n'importe quel nom, est garde tel
///    quel : rien n'est telecharge ni reecrit ;
///  - une version trop ancienne ou standard est remplacee sur place, sous le meme nom,
///    l'original en sauvegarde ;
///  - sinon le nom vient des imports de l'executable (d3d9, opengl32, sinon dxgi) ;
///  - un proxy deja pris par OptiScaler fait passer ReShade par OptiScaler
///    (ReShade64.dll + <c>[Plugins] LoadReshade=true</c>) ; pris par un autre outil, il
///    n'est jamais ecrase ;
///  - la DLL est extraite de l'installeur officiel de reshade.me, sans l'executer.
/// </summary>
public sealed class ReShadeService
{
    private const string Src = "reshade";
    private const string HomePage = "https://reshade.me/";
    private const string DownloadBase = "https://reshade.me/downloads/";

    /// <summary>Nom de l'installation dans le registre.</summary>
    public const string Origin = "ReShade";

    private readonly DownloadService _downloads;
    private readonly BackupService _backups;
    private readonly DeploymentStore _deployments;

    public ReShadeService(DownloadService downloads, BackupService backups, DeploymentStore deployments)
    {
        _downloads = downloads;
        _backups = backups;
        _deployments = deployments;
    }

    public string? LatestVersion { get; private set; }
    public string? LatestUrl { get; private set; }

    private static string CacheDir => AppPaths.Ensure(Path.Combine(AppPaths.ComponentCache, "reshade"));

    /// <summary>Lit la page d'accueil pour en extraire le numero de version courant.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var html = await _downloads.GetStringAsync(HomePage, ct);
            // Le lien de telechargement porte le numero : ReShade_Setup_6.8.0_Addon.exe
            var m = Regex.Matches(html, @"ReShade_Setup_([0-9]+\.[0-9]+\.[0-9]+)_Addon\.exe", RegexOptions.IgnoreCase)
                         .OfType<Match>()
                         .OrderByDescending(x => Version.TryParse(x.Groups[1].Value, out var v) ? v : new Version(0, 0))
                         .FirstOrDefault();

            if (m is null)
            {
                Log.Write("ReShade version not found on the home page.");
                return;
            }

            LatestVersion = m.Groups[1].Value;
            LatestUrl = DownloadBase + m.Value;
            Log.Write($"ReShade available: {LatestVersion}");
        }
        catch (Exception ex)
        {
            Log.Write($"Cannot check ReShade: {ex.Message}");
        }
    }

    /// <summary>L'installeur add-on le plus recent : en ligne, ou a defaut celui du cache.</summary>
    public async Task<string?> EnsureSetupAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (LatestUrl is null) await LoadAsync(ct);
        if (LatestUrl is null) return CachedSetup();

        var dest = Path.Combine(CacheDir, $"ReShade_Setup_{LatestVersion}_Addon.exe");
        try
        {
            await _downloads.DownloadAsync(LatestUrl, dest, null, progress, ct);
            return dest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write($"ReShade download failed: {ex.Message}");
            return CachedSetup();
        }
    }

    private static string? CachedSetup() =>
        Directory.EnumerateFiles(CacheDir, "ReShade_Setup_*_Addon.exe")
            .Select(p => (Path: p, Version: SetupVersion(p)))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .Select(x => x.Path)
            .FirstOrDefault();

    private static Version? SetupVersion(string path)
    {
        var m = Regex.Match(Path.GetFileName(path), @"ReShade_Setup_([0-9]+\.[0-9]+\.[0-9]+)_Addon", RegexOptions.IgnoreCase);
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    // ------------------------------------------------------------- Installation

    private sealed record Target(string Name, bool ViaOptiScaler, bool InPlace);

    public async Task<InstallResult> InstallAsync(
        GameInfo game, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(game.Executable) || !File.Exists(game.Executable))
            return new InstallResult(false, Loc.T("reshade.err.no_exe"));

        var dir = DllInstaller.TargetDirectory(game);
        var state = ReShadeLocator.Scan(dir);

        if (state.IsDuplicate)
            return new InstallResult(false, Loc.T("reshade.err.duplicate",
                string.Join(", ", state.Active.Select(l => l.Name))));

        // Deja pret : on ne telecharge rien et on ne touche a rien.
        if (state.Ready)
        {
            Log.Info(Src, $"{game.Name}: ReShade {state.VersionLabel} add-on already in place ({state.Primary!.Name})");
            return new InstallResult(true, Loc.T("reshade.ok.present", state.VersionLabel, state.Primary.Name), 0);
        }

        if (GameGuard.Check(game) is { } blocked) return blocked;

        var bits32 = PeInfo.Is32Bit(game.Executable);
        var (target, error) = PlanTarget(game, dir, state, bits32);
        if (target is null) return new InstallResult(false, error!);

        string dll;
        try
        {
            dll = await EnsureBinaryAsync(bits32, progress, ct) ?? "";
            if (dll.Length == 0) return new InstallResult(false, Loc.T("reshade.err.no_setup"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(Src, $"ReShade preparation failed: {ex.Message}");
            return new InstallResult(false, ex.Message);
        }

        var version = ReShadeLocator.VersionOf(dll)?.ToString(3) ?? LatestVersion ?? "";
        var dest = Path.Combine(dir, target.Name);

        var tx = new FileTransaction(game, _backups, _deployments, Origin);
        tx.Copy(dll, dest, "ReShade", version);
        // ReShade complete lui-meme sa configuration au premier lancement ; la section vide
        // suffit pour y inscrire les addons a chargement precoce.
        if (!ReShadeConfig.Exists(dir))
            tx.WriteText(ReShadeConfig.PathFor(dir), "[ADDON]\r\n", "ReShade.ini", version);

        var result = tx.Commit();
        if (!result.Success) return result;

        if (target.ViaOptiScaler) EnableInOptiScaler(dir);

        var after = ReShadeLocator.Scan(dir);
        if (!after.Ready)
        {
            Log.Error(Src, $"{game.Name}: ReShade placed as {target.Name} but final state {after.Label}");
            return new InstallResult(false, Loc.T("reshade.err.verify", after.Label), result.FilesChanged);
        }

        var msg = target.InPlace ? Loc.T("reshade.ok.upgraded", version, target.Name)
            : target.ViaOptiScaler ? Loc.T("reshade.ok.opti", version)
            : Loc.T("reshade.ok.installed", version, target.Name);

        Log.Info(Src, $"{game.Name}: ReShade {version} add-on as {target.Name}");
        return new InstallResult(true, msg, result.FilesChanged);
    }

    /// <summary>Nom sous lequel poser ReShade, ou la raison pour laquelle on s'abstient.</summary>
    private static (Target? Target, string? Error) PlanTarget(GameInfo game, string dir, ReShadeState state, bool bits32)
    {
        // Un ReShade trop ancien ou standard est remplace la ou le jeu le charge deja.
        if (state.Primary is { } existing)
            return (new Target(existing.Name, false, true), null);

        var imports = PeInfo.Imports(game.Executable);
        bool Has(string n) => imports.Contains(n);
        var direct3d = Has("dxgi.dll") || Has("d3d9.dll") || Has("d3d10.dll") || Has("d3d11.dll") || Has("d3d12.dll");

        // Vulkan passe par une couche globale de ReShade, pas par un proxy dans le jeu.
        if (!direct3d && !Has("opengl32.dll") && (Has("vulkan-1.dll") || (game.Api == GameApi.Vulkan && imports.Count > 0)))
            return (null, Loc.T("reshade.err.vulkan"));

        var primary = Has("d3d9.dll") && !Has("dxgi.dll") ? "d3d9.dll"
            : Has("opengl32.dll") && !direct3d ? "opengl32.dll"
            : "dxgi.dll";

        var candidates = new List<string> { primary };
        if (primary == "dxgi.dll")
        {
            if (game.Api == GameApi.DirectX12) candidates.Add("d3d12.dll");
            else if (game.Api == GameApi.DirectX11) candidates.Add("d3d11.dll");
        }

        string? owner = null;
        foreach (var name in candidates)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) return (new Target(name, false, false), null);

            var tag = Describe(path);
            // OptiScaler sait charger ReShade lui-meme : c'est la cohabitation que recommande RHI.
            if (!bits32 && tag.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(dir, "OptiScaler.ini")))
                return (new Target("ReShade64.dll", true, false), null);

            owner ??= string.IsNullOrWhiteSpace(tag) ? name : tag.Trim();
        }

        return (null, Loc.T("reshade.err.occupied", primary, owner ?? "?"));
    }

    private static string Describe(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return $"{info.ProductName} {info.FileDescription}".Trim();
        }
        catch { return ""; }
    }

    private static void EnableInOptiScaler(string dir)
    {
        var ini = Path.Combine(dir, "OptiScaler.ini");
        try
        {
            var lines = File.ReadAllLines(ini).ToList();
            if (string.Equals(IniFile.Read(lines, "Plugins", "LoadReshade"), "true", StringComparison.OrdinalIgnoreCase))
                return;
            IniFile.Set(lines, "Plugins", "LoadReshade", "true");
            DllInstaller.ClearReadOnly(ini);
            File.WriteAllLines(ini, lines);
            Log.Info(Src, $"OptiScaler.ini: LoadReshade=true ({dir})");
        }
        catch (Exception ex) { Log.Warn(Src, $"OptiScaler.ini not changed: {ex.Message}"); }
    }

    /// <summary>
    /// ReShade64.dll ou ReShade32.dll, extrait de l'installeur add-on. L'installeur porte une
    /// archive zip en son sein : on la retrouve par la fin de son repertoire central, sans
    /// jamais lancer l'executable.
    /// </summary>
    private async Task<string?> EnsureBinaryAsync(bool bits32, IProgress<double>? progress, CancellationToken ct)
    {
        var setup = await EnsureSetupAsync(progress, ct);
        if (setup is null) return null;

        var version = SetupVersion(setup)?.ToString(3) ?? "latest";
        var name = bits32 ? "ReShade32.dll" : "ReShade64.dll";
        var dll = Path.Combine(AppPaths.Ensure(Path.Combine(CacheDir, version)), name);

        if (File.Exists(dll) && ReShadeLocator.IsReShade(dll) && ReShadeLocator.IsAddonBuild(dll)) return dll;

        ExtractFromSetup(setup, name, dll);

        if (!ReShadeLocator.IsReShade(dll) || !ReShadeLocator.IsAddonBuild(dll))
        {
            try { File.Delete(dll); } catch { }
            throw new InvalidDataException(Loc.T("reshade.err.extract", name));
        }

        Log.Info(Src, $"{name} {version} extracted from {Path.GetFileName(setup)}");
        return dll;
    }

    private static void ExtractFromSetup(string setup, string entryName, string dest)
    {
        const uint EndOfCentralDirectory = 0x06054B50;
        var temp = dest + ".zip";

        using (var fs = new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var tail = (int)Math.Min(fs.Length, 22 + 65535);
            var buffer = new byte[tail];
            fs.Position = fs.Length - tail;
            fs.ReadExactly(buffer);

            long start = -1, end = -1;
            for (var i = tail - 22; i >= 0; i--)
            {
                if (BitConverter.ToUInt32(buffer, i) != EndOfCentralDirectory) continue;

                var eocd = fs.Length - tail + i;
                var cdSize = BitConverter.ToUInt32(buffer, i + 12);
                var cdOffset = BitConverter.ToUInt32(buffer, i + 16);
                var comment = BitConverter.ToUInt16(buffer, i + 20);

                // Les offsets de l'archive partent de son propre debut, pas de celui de l'installeur.
                start = eocd - cdSize - cdOffset;
                end = eocd + 22 + comment;
                if (start >= 0 && end <= fs.Length) break;
                start = -1;
            }

            if (start < 0) throw new InvalidDataException(Loc.T("reshade.err.extract", entryName));

            fs.Position = start;
            using var output = File.Create(temp);
            var remaining = end - start;
            var chunk = new byte[1 << 16];
            while (remaining > 0)
            {
                var read = fs.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                if (read <= 0) break;
                output.Write(chunk, 0, read);
                remaining -= read;
            }
        }

        try
        {
            using var zip = ZipFile.OpenRead(temp);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException(Loc.T("reshade.err.extract", entryName));
            var part = dest + ".part";
            entry.ExtractToFile(part, overwrite: true);
            File.Move(part, dest, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    // -------------------------------------------------------------------- Etat

    /// <summary>Version de ReShade charge par le jeu, ou null.</summary>
    public static string? InstalledVersion(GameInfo game)
    {
        var state = ReShadeLocator.Scan(game);
        return state.Present ? state.VersionLabel : null;
    }

    /// <summary>Retire les binaires ReShade et sa configuration, en laissant les presets de l'utilisateur.</summary>
    public static InstallResult Uninstall(GameInfo game)
    {
        if (GameGuard.Check(game) is { } blocked) return blocked;

        var dir = DllInstaller.TargetDirectory(game);
        var removed = 0;

        // ReShadePreset.ini et reshade-shaders contiennent le travail de l'utilisateur :
        // on ne retire que le moteur et sa configuration technique. Chaque binaire est
        // identifie par ses metadonnees : un dxgi.dll etranger n'est jamais supprime.
        var paths = ReShadeLocator.Scan(dir).Loaders.Select(l => l.Path).ToList();
        if (paths.Count > 0 && ReShadeConfig.Exists(dir)) paths.Add(ReShadeConfig.PathFor(dir));

        foreach (var path in paths)
        {
            try
            {
                DllInstaller.ClearReadOnly(path);
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) { Log.Warn(Src, $"Cannot delete {path}: {ex.Message}"); }
        }

        DllDetector.Inspect(game);
        return removed > 0
            ? new InstallResult(true, Loc.T("reshade.ok.removed", removed), removed)
            : new InstallResult(false, Loc.T("reshade.none"));
    }
}
