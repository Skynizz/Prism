using System.Net.Http;
using System.Text;
using System.Text.Json;
using Prism.Core;
using Prism.Models;

namespace Prism.Services;

public enum RequirementState { Checking, Installed, Missing, External }

/// <summary>Version installee d'un fichier Nexus face a la plus recente du meme fichier.</summary>
public sealed record ModUpdateInfo(NexusInstall Install, long? GameId, string? InstalledVersion, string? LatestVersion,
    long? LatestFileId, string? LatestName, bool HasUpdate, bool Known, long? ModId = null);

/// <summary>Un prerequis d'un mod, tel que le declare sa page Nexus.</summary>
public sealed record ModRequirement(string GameDomain, long GameId, long ModId, string Name, string? Url, bool External, string? Notes, int Depth)
{
    /// <summary>Onglet Fichiers du prerequis, ou son adresse externe.</summary>
    public string Page => External && !string.IsNullOrWhiteSpace(Url) ? Url!
        : $"{NexusService.Site}/{GameDomain}/mods/{ModId}?tab=files";
}

/// <summary>
/// Prerequis des mods, par l'API GraphQL v2 publique de Nexus (sans cle) :
///  - <c>mod.modRequirements.nexusRequirements</c> : les mods exiges, suivis sur trois niveaux
///    (un mod ArchiveXL exige ArchiveXL, qui exige RED4ext) ;
///  - <c>modFiles</c> + <c>modFileContents</c> : les chemins du fichier principal d'un prerequis.
/// Un prerequis est installe si Prism l'a pose, ou si ses fichiers caracteristiques sont deja
/// dans le jeu (installe a la main ou par un autre outil) : ArchiveXL se reconnait a
/// red4ext\plugins\ArchiveXL\ArchiveXL.dll.
/// </summary>
public sealed class NexusRequirements
{
    private const string Src = "nexus";
    private const string GraphQl = "https://api.nexusmods.com/v2/graphql";

    /// <summary>Fichiers qui identifient un mod ; les notices et les textures n'en disent rien.</summary>
    private static readonly string[] KeyExtensions =
    { ".dll", ".asi", ".exe", ".esp", ".esm", ".esl", ".archive", ".pak", ".lua", ".reds", ".xl", ".ba2", ".bsa", ".pex", ".addon64" };

    private readonly DeploymentStore _deployments;

    public NexusRequirements(DeploymentStore deployments) => _deployments = deployments;

    private readonly Dictionary<string, long> _gameIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(long, long), List<ModRequirement>> _direct = new();
    private readonly Dictionary<(long, long), List<(string Path, long Size)>> _contents = new();
    private readonly Dictionary<(long, long), List<long>> _mainFiles = new();

    /// <summary>Fichier principal le plus recent d'un mod : sa page de telechargement est celle a ouvrir.</summary>
    public async Task<long?> MainFileIdAsync(long gameId, long modId, CancellationToken ct = default)
    {
        var ids = await MainFileIdsAsync(gameId, modId, ct);
        return ids.Count > 0 ? ids[0] : null;
    }

    private async Task<List<long>> MainFileIdsAsync(long gameId, long modId, CancellationToken ct)
    {
        if (_mainFiles.TryGetValue((gameId, modId), out var cached)) return cached;
        using var files = await QueryAsync("query($m:ID!,$g:ID!){ modFiles(modId:$m, gameId:$g){ fileId category date } }",
            new { m = modId.ToString(), g = gameId.ToString() }, ct);
        var ids = files.RootElement.GetProperty("data").GetProperty("modFiles").EnumerateArray()
            .Where(f => f.GetProperty("category").GetString() == "MAIN")
            .OrderByDescending(f => f.GetProperty("date").GetInt64())
            .Select(f => f.GetProperty("fileId").GetInt64())
            .ToList();
        _mainFiles[(gameId, modId)] = ids;
        return ids;
    }

    public async Task<long?> GameIdAsync(string domain, CancellationToken ct = default)
    {
        if (_gameIds.TryGetValue(domain, out var id)) return id;
        using var doc = await QueryAsync("query($d:String!){ game(domainName:$d){ id } }", new { d = domain }, ct);
        var game = doc.RootElement.GetProperty("data").GetProperty("game");
        if (game.ValueKind != JsonValueKind.Object) return null;
        id = game.GetProperty("id").GetInt64();
        _gameIds[domain] = id;
        return id;
    }

    /// <summary>Prerequis d'un mod et ceux de ses prerequis, sans doublon, le plus direct d'abord.</summary>
    public async Task<List<ModRequirement>> AllAsync(string domain, long modId, CancellationToken ct = default)
    {
        var gameId = await GameIdAsync(domain, ct);
        if (gameId is null) return new List<ModRequirement>();

        var result = new List<ModRequirement>();
        var seen = new HashSet<long> { modId };
        var queue = new Queue<(long ModId, int Depth)>();
        queue.Enqueue((modId, 0));
        while (queue.Count > 0)
        {
            var (id, depth) = queue.Dequeue();
            if (depth >= 3) continue;
            foreach (var r in await DirectAsync(domain, gameId.Value, id, depth + 1, ct))
            {
                if (!r.External && !seen.Add(r.ModId)) continue;
                result.Add(r);
                if (!r.External) queue.Enqueue((r.ModId, depth + 1));
            }
        }
        return result;
    }

    private async Task<List<ModRequirement>> DirectAsync(string domain, long gameId, long modId, int depth, CancellationToken ct)
    {
        if (_direct.TryGetValue((gameId, modId), out var cached))
            return cached.Select(r => r with { Depth = depth }).ToList();

        using var doc = await QueryAsync(
            "query($m:ID!,$g:ID!){ mod(modId:$m, gameId:$g){ modRequirements { nexusRequirements { nodes { modId modName url externalRequirement notes gameId } } } } }",
            new { m = modId.ToString(), g = gameId.ToString() }, ct);
        var list = new List<ModRequirement>();
        var mod = doc.RootElement.GetProperty("data").GetProperty("mod");
        if (mod.ValueKind == JsonValueKind.Object &&
            mod.GetProperty("modRequirements").GetProperty("nexusRequirements").GetProperty("nodes") is { ValueKind: JsonValueKind.Array } nodes)
        {
            foreach (var n in nodes.EnumerateArray())
            {
                var external = n.GetProperty("externalRequirement").GetBoolean();
                long.TryParse(n.GetProperty("modId").ToString(), out var reqId);
                long.TryParse(n.GetProperty("gameId").ToString(), out var reqGame);
                // Un prerequis d'un autre jeu (rare : outils partages) garde son propre domaine inconnu : lien direct.
                list.Add(new ModRequirement(domain, reqGame == 0 ? gameId : reqGame, reqId,
                    n.GetProperty("modName").GetString() ?? "?", n.GetProperty("url").GetString(), external,
                    n.GetProperty("notes").GetString(), depth));
            }
        }
        _direct[(gameId, modId)] = list;
        return list;
    }

    /// <summary>
    /// Le fichier installe a-t-il une version plus recente ? Nexus range les versions successives d'un
    /// meme fichier dans un groupe (groupId) et passe les anciennes en OLD_VERSION : la plus recente du
    /// groupe, hors versions retirees, est la reference.
    /// </summary>
    public async Task<ModUpdateInfo> CheckUpdateAsync(NexusInstall install, CancellationToken ct = default)
    {
        // Traces anterieures : numero et version se lisent dans le nom d'archive.
        var parsed = NexusService.ParseArchiveName(install.Archive);
        var modId = install.ModId ?? parsed.ModId;
        var version = install.Version ?? parsed.Version;
        var unknown = new ModUpdateInfo(install, null, version, null, null, null, false, false, modId);
        if (install.Domain is null || modId is null) return unknown;
        var gameId = await GameIdAsync(install.Domain, ct);
        if (gameId is null) return unknown;

        using var doc = await QueryAsync("query($m:ID!,$g:ID!){ modFiles(modId:$m, gameId:$g){ fileId name version category groupId date } }",
            new { m = modId.Value.ToString(), g = gameId.Value.ToString() }, ct);
        var files = doc.RootElement.GetProperty("data").GetProperty("modFiles").EnumerateArray().Select(f => new
        {
            Id = f.GetProperty("fileId").GetInt64(),
            Name = f.GetProperty("name").GetString() ?? "",
            Version = f.GetProperty("version").GetString() ?? "",
            Category = f.GetProperty("category").GetString() ?? "",
            Group = f.GetProperty("groupId").ToString(),
            Date = f.GetProperty("date").GetInt64()
        }).ToList();

        // Le fichier installe : par son numero, sinon par son nom et sa version, sinon par sa version, sinon par son nom.
        var label = RenoDxWikiService.Normalize(install.FileLabel ?? parsed.Name);
        var mine = files.FirstOrDefault(f => install.FileId is not null && f.Id == install.FileId)
                   ?? files.FirstOrDefault(f => RenoDxWikiService.Normalize(f.Name) == label && f.Version == version)
                   ?? files.FirstOrDefault(f => version is not null && f.Version == version)
                   ?? files.Where(f => RenoDxWikiService.Normalize(f.Name) == label).OrderBy(f => f.Date).FirstOrDefault();
        if (mine is null) return unknown with { GameId = gameId };

        var latest = files.Where(f => f.Group == mine.Group && f.Category is not ("OLD_VERSION" or "ARCHIVED" or "DELETED" or "REMOVED"))
                          .OrderByDescending(f => f.Date).FirstOrDefault() ?? mine;
        var newer = latest.Id != mine.Id && latest.Date > mine.Date;
        return new ModUpdateInfo(install, gameId, version ?? mine.Version, latest.Version, latest.Id, latest.Name, newer, true, modId);
    }

    /// <summary>Etat d'un prerequis dans ce jeu.</summary>
    public async Task<RequirementState> StateAsync(GameInfo game, ModRequirement req, CancellationToken ct = default)
    {
        if (req.External) return RequirementState.External;

        // Pose par Prism et toujours en place : ses fichiers sont encore au registre et sur le disque.
        // Un mod retire garde sa trace d'installation, mais plus aucun fichier : il ne compte pas.
        if (NexusModInstaller.Installed().Any(i => i.GameId == game.Id && i.ModId == req.ModId && StillThere(game, i)))
            return RequirementState.Installed;

        var files = await MainFilesAsync(req.GameId, req.ModId, ct);

        // Un nom de DLL proxy (winmm.dll, version.dll...) ne prouve rien : OptiScaler ou ReShade
        // peuvent le porter. Il ne compte que si sa taille est exactement celle du prerequis.
        bool Generic(string path) => ProxyNames.Contains(Path.GetFileName(path));
        var keys = files.Where(f => KeyExtensions.Contains(Path.GetExtension(f.Path).ToLowerInvariant()) && !Generic(f.Path))
                        .OrderByDescending(f => f.Size).ToList();
        if (keys.Count > 0)
        {
            // Le fichier le plus caracteristique (le plus gros) doit etre la : RED4ext.dll, pas seulement winmm.dll.
            return ExistsInGame(game, keys[0].Path) ? RequirementState.Installed : RequirementState.Missing;
        }
        var proxies = files.Where(f => Generic(f.Path)).ToList();
        if (proxies.Count > 0)
            return proxies.All(p => ExistsInGame(game, p.Path, p.Size)) ? RequirementState.Installed : RequirementState.Missing;
        var any = files.OrderByDescending(f => f.Size).FirstOrDefault();
        return any.Path is not null && ExistsInGame(game, any.Path) ? RequirementState.Installed : RequirementState.Missing;
    }

    public bool IsDeployed(GameInfo game, NexusInstall install) => StillThere(game, install);

    private bool StillThere(GameInfo game, NexusInstall install)
        => _deployments.For(game.Id).Any(e => string.Equals(e.Origin, install.Origin, StringComparison.Ordinal) && File.Exists(e.Path));

    /// <summary>
    /// Le chemin d'archive d'un fichier retrouve dans le jeu : tel quel depuis la racine, depuis
    /// le dossier de l'exe, depuis Data, ou apres un ou deux dossiers d'emballage.
    /// </summary>
    private static readonly HashSet<string> ProxyNames = new(
        ReShadeLocator.ProxyNames.Concat(DllDetector.ProxyNames), StringComparer.OrdinalIgnoreCase);

    public static bool ExistsInGame(GameInfo game, string archivePath, long? size = null)
    {
        var seg = archivePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var bases = new List<string> { game.InstallDir, DllInstaller.TargetDirectory(game) };
        if (NexusModInstaller.DataRoot(game.InstallDir) is { } data) bases.Add(data);
        for (var skip = 0; skip < Math.Min(3, seg.Length); skip++)
        {
            var tail = Path.Combine(seg.Skip(skip).ToArray());
            if (bases.Any(b => File.Exists(Path.Combine(b, tail)) && (size is null || new FileInfo(Path.Combine(b, tail)).Length == size))) return true;
        }
        return false;
    }

    /// <summary>Chemins du fichier principal le plus recent d'un mod.</summary>
    private async Task<List<(string Path, long Size)>> MainFilesAsync(long gameId, long modId, CancellationToken ct)
    {
        if (_contents.TryGetValue((gameId, modId), out var cached)) return cached;

        var result = new List<(string, long)>();
        {
            var main = (await MainFileIdsAsync(gameId, modId, ct)).Take(2).ToList();

            foreach (var fileId in main)
            {
                using var contents = await QueryAsync(
                    "query($f:Int!,$g:Int!){ modFileContents(filter:{ fileId:[{value:$f,op:EQUALS}], gameId:[{value:$g,op:EQUALS}] }, count:500){ nodes { filePath fileSize } } }",
                    new { f = (int)fileId, g = (int)gameId }, ct);
                foreach (var n in contents.RootElement.GetProperty("data").GetProperty("modFileContents").GetProperty("nodes").EnumerateArray())
                    result.Add((n.GetProperty("filePath").GetString() ?? "", long.TryParse(n.GetProperty("fileSize").ToString().Trim('"'), out var s) ? s : 0));
            }
        }
        _contents[(gameId, modId)] = result;
        return result;
    }

    private static async Task<JsonDocument> QueryAsync(string query, object variables, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { query, variables });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await DownloadService.Client.PostAsync(GraphQl, content, ct);
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("errors", out var errors))
            Log.Warn(Src, $"GraphQL: {errors[0].GetProperty("message").GetString()}");
        return doc;
    }
}
