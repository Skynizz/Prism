using System.Reflection;
using System.Text.Json;
using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>Un pack de shaders ReShade du catalogue (ReShadePacks/packs.json).</summary>
public sealed class ShaderPack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    /// <summary>hdr, base, color, light, photo, special.</summary>
    public string Category { get; set; } = "special";
    public bool Essential { get; set; }
    /// <summary>zip : archive de branche GitHub ; release : derniere release GitHub.</summary>
    public string Kind { get; set; } = "zip";
    public string? Asset { get; set; }
    public string Url { get; set; } = "";
    /// <summary>Termes techniques courts, identiques dans toutes les langues (« MXAO · RTGI »).</summary>
    public string Tags { get; set; } = "";
    public List<string>? Requires { get; set; }

    public string Origin => ShaderPackService.OriginPrefix + Name;
}

/// <summary>
/// Packs de shaders ReShade, par jeu. Chaque pack est pose dans reshade-shaders\Shaders et
/// \Textures a cote de ReShade, comme le fait l'installeur officiel, et chaque fichier est inscrit
/// sous l'origine « Shaders · nom » : un pack se retire seul, sans toucher aux autres.
///  - Un fichier deja present (autre pack, ajout de l'utilisateur) n'est jamais ecrase.
///  - Les en-tetes communs (ReShade.fxh, ReShadeUI.fxh) ont leur propre origine : retirer un pack
///    ne casse pas ceux qui les utilisent encore.
///  - ReShade.ini recoit les chemins de recherche par defaut de l'installeur officiel
///    (.\reshade-shaders\Shaders\** et .\reshade-shaders\Textures\**) s'ils manquent : sans eux,
///    ReShade ne voit pas les effets.
/// </summary>
public sealed class ShaderPackService
{
    private const string Src = "shaders";
    public const string OriginPrefix = "Shaders · ";
    public const string CommonOrigin = "Shaders · ReShade";
    private const string ResourceName = "ReShadePacks.packs.json";
    public const string RemoteUrl = "https://raw.githubusercontent.com/Skynizz/Prism/main/ReShadePacks/packs.json";

    public const string EffectPath = @".\reshade-shaders\Shaders\**";
    public const string TexturePath = @".\reshade-shaders\Textures\**";

    private static readonly HashSet<string> CommonHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "ReShade.fxh", "ReShadeUI.fxh" };

    private static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg", ".dds", ".bmp", ".tga" };

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private readonly DownloadService _downloads;
    private readonly BackupService _backups;
    private readonly DeploymentStore _deployments;
    private int _version;

    public ShaderPackService(DownloadService downloads, BackupService backups, DeploymentStore deployments)
    {
        _downloads = downloads;
        _backups = backups;
        _deployments = deployments;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is not null) Load(new StreamReader(stream).ReadToEnd());
        }
        catch (Exception ex) { Log.Warn(Src, $"Embedded catalog unreadable: {ex.Message}"); }
    }

    public List<ShaderPack> Catalog { get; private set; } = new();

    public ShaderPack? Find(string id) => Catalog.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _downloads.GetStringAsync(RemoteUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var v) && v.GetInt32() > _version) Load(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Info(Src, $"Remote catalog unavailable: {ex.Message}"); }
    }

    private void Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Catalog = doc.RootElement.GetProperty("packs").Deserialize<List<ShaderPack>>(Options) ?? new();
        _version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetInt32() : 0;
        Log.Info(Src, $"Shader catalog v{_version}: {Catalog.Count} pack(s)");
    }

    // ---------------------------------------------------------------- Etat

    public static string ShadersRoot(GameInfo game) => Path.Combine(DllInstaller.TargetDirectory(game), "reshade-shaders");

    /// <summary>Un pack est installe tant qu'au moins un de ses fichiers est en place.</summary>
    public bool IsInstalled(GameInfo game, ShaderPack pack)
        => _deployments.For(game.Id).Any(e => e.Origin == pack.Origin && File.Exists(e.Path));

    public int FileCount(GameInfo game, ShaderPack pack)
        => _deployments.For(game.Id).Count(e => e.Origin == pack.Origin && File.Exists(e.Path));

    // ---------------------------------------------------------- Installation

    public async Task<InstallResult> InstallAsync(GameInfo game, ShaderPack pack, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (GameGuard.Check(game) is { } blocked) return blocked;
        var root = ShadersRoot(game);
        try
        {
            var source = await FetchAsync(pack, progress, ct);
            var (files, textures) = Map(source);
            if (files.Count == 0) return new InstallResult(false, Loc.T("shaders.err.empty", pack.Name));

            // Deja la (autre pack ou ajout de l'utilisateur) : on ne l'ecrase pas.
            var mine = _deployments.For(game.Id).Where(e => e.Origin == pack.Origin).Select(e => e.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var planned = new List<(string Src, string Dest, bool Common)>();
            foreach (var (src, rel) in files.Select(f => (f.Src, Path.Combine("Shaders", f.Rel)))
                         .Concat(textures.Select(t => (t.Src, Path.Combine("Textures", t.Rel)))))
            {
                var dest = Path.Combine(root, rel);
                if (File.Exists(dest) && !mine.Contains(dest)) continue;
                planned.Add((src, dest, CommonHeaders.Contains(Path.GetFileName(dest))));
            }
            var skipped = files.Count + textures.Count - planned.Count;

            // En-tetes communs d'abord, sous leur propre origine.
            var common = planned.Where(p => p.Common).ToList();
            if (common.Count > 0)
            {
                var ctx = new FileTransaction(game, _backups, _deployments, CommonOrigin);
                foreach (var (src, dest, _) in common) ctx.Copy(src, dest, "ReShade", "shaders");
                var rc = ctx.Commit();
                if (!rc.Success) return rc;
            }

            var tx = new FileTransaction(game, _backups, _deployments, pack.Origin);
            foreach (var (src, dest, _) in planned.Where(p => !p.Common)) tx.Copy(src, dest, pack.Name, pack.Author);
            var r = tx.Commit();
            if (!r.Success) return r;

            EnsureSearchPaths(DllInstaller.TargetDirectory(game));
            Log.Info(Src, $"{game.Name}: {pack.Name} — {planned.Count} file(s), {skipped} already present");
            return new InstallResult(true, Loc.T("shaders.ok", pack.Name, planned.Count), planned.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(Src, $"{pack.Name}: {ex.Message}");
            return new InstallResult(false, Loc.T("err.install_failed", ex.Message));
        }
    }

    /// <summary>Retire les fichiers du pack, puis les dossiers devenus vides sous reshade-shaders.</summary>
    public InstallResult Remove(GameInfo game, ShaderPack pack)
    {
        if (GameGuard.Check(game) is { } blocked) return blocked;
        var entries = _deployments.For(game.Id).Where(e => e.Origin == pack.Origin).ToList();
        var done = entries.Count(e => _deployments.Remove(e));
        PruneEmpty(ShadersRoot(game));
        Log.Info(Src, $"{game.Name}: {pack.Name} removed ({done} file(s))");
        return new InstallResult(true, Loc.T("shaders.removed", pack.Name, done), done);
    }

    /// <summary>Archive du pack, telechargee une fois par jour au plus, extraite a neuf.</summary>
    private async Task<string> FetchAsync(ShaderPack pack, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(AppPaths.ComponentCache, "shaders", AppPaths.Sanitize(pack.Id));
        Directory.CreateDirectory(dir);
        var url = pack.Url;
        var ext = ".zip";
        if (pack.Kind == "release")
        {
            using var doc = JsonDocument.Parse(await _downloads.GetStringAsync(pack.Url, ct));
            var wanted = pack.Asset ?? ".zip";
            var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").EndsWith(wanted, StringComparison.OrdinalIgnoreCase));
            url = asset.ValueKind == JsonValueKind.Object ? asset.GetProperty("browser_download_url").GetString()!
                : throw new InvalidOperationException(Loc.T("shaders.err.no_asset", pack.Name));
            ext = wanted;
        }

        var archive = Path.Combine(dir, "pack" + ext);
        if (File.Exists(archive) && DateTime.Now - File.GetLastWriteTime(archive) > TimeSpan.FromDays(1)) File.Delete(archive);
        await _downloads.DownloadAsync(url, archive, null, progress, ct);

        var files = Path.Combine(dir, "files");
        if (Directory.Exists(files)) Directory.Delete(files, recursive: true);
        await ArchiveExtractor.ExtractAsync(archive, files, ct);
        return files;
    }

    /// <summary>
    /// Fichiers d'effets et textures de l'archive. Le dossier « Shaders » le moins profond qui contient
    /// des .fx/.fxh fait foi (et son voisin « Textures ») ; a defaut, les .fx/.fxh et images du premier
    /// dossier qui en contient.
    /// </summary>
    public static (List<(string Src, string Rel)> Shaders, List<(string Src, string Rel)> Textures) Map(string extracted)
    {
        var shaders = new List<(string, string)>();
        var textures = new List<(string, string)>();

        var shaderDir = Directory.EnumerateDirectories(extracted, "*", SearchOption.AllDirectories)
            .Where(d => Path.GetFileName(d).Equals("Shaders", StringComparison.OrdinalIgnoreCase)
                        && Directory.EnumerateFiles(d, "*.fx*", SearchOption.AllDirectories).Any())
            .OrderBy(d => d.Length).FirstOrDefault();

        if (shaderDir is not null)
        {
            foreach (var f in Directory.EnumerateFiles(shaderDir, "*", SearchOption.AllDirectories))
                shaders.Add((f, Path.GetRelativePath(shaderDir, f)));
            var textureDir = Directory.EnumerateDirectories(extracted, "*", SearchOption.AllDirectories)
                .Where(d => Path.GetFileName(d).Equals("Textures", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Length).FirstOrDefault();
            if (textureDir is not null)
                foreach (var f in Directory.EnumerateFiles(textureDir, "*", SearchOption.AllDirectories))
                    textures.Add((f, Path.GetRelativePath(textureDir, f)));
            return (shaders, textures);
        }

        var first = Directory.EnumerateFiles(extracted, "*.fx*", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).OrderBy(d => d!.Length).FirstOrDefault();
        if (first is null) return (shaders, textures);
        foreach (var f in Directory.EnumerateFiles(first!))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".fx" or ".fxh") shaders.Add((f, Path.GetFileName(f)));
            else if (TextureExtensions.Contains(ext)) textures.Add((f, Path.GetFileName(f)));
        }
        return (shaders, textures);
    }

    /// <summary>Ajoute a ReShade.ini les chemins par defaut de l'installeur officiel s'ils manquent.</summary>
    public static void EnsureSearchPaths(string dir)
    {
        var path = ReShadeConfig.PathFor(dir);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var changed = Add(lines, "EffectSearchPaths", EffectPath) | Add(lines, "TextureSearchPaths", TexturePath);
        if (!changed) return;
        DllInstaller.ClearReadOnly(path);
        File.WriteAllLines(path, lines);
        Log.Info(Src, $"ReShade.ini search paths completed in {dir}");

        static bool Add(List<string> lines, string key, string value)
        {
            var current = IniFile.Read(lines, "GENERAL", key) ?? "";
            var parts = current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (parts.Any(p => p.Equals(value, StringComparison.OrdinalIgnoreCase))) return false;
            parts.Add(value);
            IniFile.Set(lines, "GENERAL", key, string.Join(",", parts));
            return true;
        }
    }

    private static void PruneEmpty(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
            catch { /* dossier occupe */ }
        }
    }
}
