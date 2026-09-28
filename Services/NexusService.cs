using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>Un lien « Mod Manager Download » : nxm://cyberpunk2077/mods/107/files/123?key=...</summary>
public sealed record NxmLink(string Domain, long ModId, long FileId);

/// <summary>
/// Nexus Mods cote navigation : la page du jeu, les liens nxm, le nom d'un mod.
///
/// Le domaine du jeu (« cyberpunk2077 ») vient de l'API GraphQL v2 publique, sans cle :
/// <c>games(filter: { name: { value, op: WILDCARD } }) { nodes { name domainName } }</c>.
/// Nexus ne connait pas l'AppID Steam : le rapprochement se fait sur le nom normalise,
/// et seul un nom identique est retenu. Le resultat est garde en cache.
///
/// Aucun telechargement ne passe par l'API : c'est l'utilisateur qui clique sur le site,
/// un fichier a la fois, comme l'exige la politique d'usage de Nexus.
/// </summary>
public sealed partial class NexusService
{
    private const string Src = "nexus";
    public const string Site = "https://www.nexusmods.com";
    private const string GraphQl = "https://api.nexusmods.com/v2/graphql";

    private readonly Dictionary<string, string> _domains;

    public NexusService()
    {
        _domains = JsonStore.Load(CacheFile, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private static string CacheFile => Path.Combine(AppPaths.Cache, "nexus-domains.json");

    /// <summary>Dossier des archives recues de Nexus.</summary>
    public static string Downloads => AppPaths.Ensure(Path.Combine(AppPaths.Cache, "nexus"));

    public static string GamesPage => $"{Site}/games";
    public static string GamePage(string domain) => $"{Site}/games/{domain}/mods";

    /// <summary>La page de telechargement manuel d'un fichier : celle qu'ouvre « Manual download ».</summary>
    public static string FilePage(NxmLink link) => $"{Site}/{link.Domain}/mods/{link.ModId}?tab=files&file_id={link.FileId}";

    /// <summary>Domaine deja connu pour ce jeu, sans requete.</summary>
    public string? CachedDomain(GameInfo game) => _domains.TryGetValue(game.Id, out var d) && d.Length > 0 ? d : null;

    public async Task<string?> DomainForAsync(GameInfo game, CancellationToken ct = default)
    {
        if (_domains.TryGetValue(game.Id, out var known)) return known.Length == 0 ? null : known;

        var wanted = RenoDxWikiService.Normalize(game.Name);
        if (wanted.Length == 0) return null;

        string? found = null;
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                query = "query($n:String!){ games(filter:{name:{value:$n,op:WILDCARD}}) { nodes { name domainName } } }",
                variables = new { n = SearchText(game.Name) }
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await DownloadService.Client.PostAsync(GraphQl, content, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var nodes = doc.RootElement.GetProperty("data").GetProperty("games").GetProperty("nodes");
            foreach (var n in nodes.EnumerateArray())
            {
                if (RenoDxWikiService.Normalize(n.GetProperty("name").GetString()) != wanted) continue;
                found = n.GetProperty("domainName").GetString();
                break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hors ligne : on retentera, rien n'est mis en cache.
            Log.Warn(Src, $"Game lookup failed for {game.Name}: {ex.Message}");
            return null;
        }

        _domains[game.Id] = found ?? "";
        JsonStore.Save(CacheFile, _domains);
        Log.Info(Src, $"{game.Name}: {(found ?? "not on Nexus")}");
        return found;
    }

    /// <summary>Le nom sans symboles de marque ni ponctuation, que la recherche Nexus accepte.</summary>
    private static string SearchText(string name)
        => Spaces().Replace(NonWord().Replace(name, " "), " ").Trim();

    /// <summary>Sections du site qui ne sont pas des jeux.</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    { "games", "search", "users", "news", "about", "premium", "mods", "collections", "account", "api", "media", "images", "videos" };

    public static NxmLink? ParseNxm(string? uri)
    {
        if (uri is null) return null;
        var m = NxmRegex().Match(uri);
        return m.Success ? new NxmLink(m.Groups[1].Value.ToLowerInvariant(), long.Parse(m.Groups[2].Value), long.Parse(m.Groups[3].Value)) : null;
    }

    /// <summary>Domaine du jeu d'une page Nexus : /games/{domaine}/... ou /{domaine}/mods/...</summary>
    public static string? DomainOf(string? url)
    {
        if (url is null) return null;
        var m = PageRegex().Match(url);
        if (!m.Success) return null;
        var domain = m.Groups[1].Value.ToLowerInvariant();
        return NotGames.Contains(domain) ? null : domain;
    }

    /// <summary>Fichier vise par une page de telechargement (…?tab=files&amp;file_id=123).</summary>
    public static long? FileIdOf(string? url)
    {
        if (url is null) return null;
        var m = FileIdRegex().Match(url);
        return m.Success ? long.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>Numero du mod d'une page Nexus, s'il y en a un.</summary>
    public static long? ModIdOf(string? url)
    {
        if (url is null) return null;
        var m = ModRegex().Match(url);
        return m.Success ? long.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// Nom du fichier, numero du mod et version d'apres le nom d'archive que donne Nexus. Deux formes :
    ///  - ancienne : « Cyber Engine Tweaks-107-1-32-3-1712345678.zip » ;
    ///  - actuelle : « ArchiveXL 4198 1.27.3 2026-09-07T10-15Z aI50IA5YP.zip ».
    /// Le nom est celui du fichier sur Nexus : un mod peut en avoir plusieurs (principal, options).
    /// </summary>
    public static (string Name, long? ModId, string? Version) ParseArchiveName(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var m = CurrentArchiveRegex().Match(stem);
        if (m.Success) return (Clean(m.Groups[1].Value), long.Parse(m.Groups[2].Value), m.Groups[3].Value);
        m = ArchiveRegex().Match(stem);
        if (!m.Success) return (Clean(stem), null, null);
        var version = m.Groups[3].Success ? m.Groups[3].Value.Replace('-', '.') : "";
        return (Clean(m.Groups[1].Value), long.Parse(m.Groups[2].Value), version.Length == 0 ? null : version);

        // Un fichier envoye sur Nexus sous le nom « input_loader_v0.2.3.zip » garde son extension dans le nom.
        static string Clean(string name)
        {
            name = name.Trim();
            foreach (var ext in new[] { ".zip", ".7z", ".rar" })
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length].Trim();
            return name;
        }
    }

    /// <summary>Nom du mod d'apres le titre de la page : « Cyber Engine Tweaks at Cyberpunk 2077 Nexus - Mods and community ».</summary>
    public static string? ModNameFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var at = title.IndexOf(" at ", StringComparison.Ordinal);
        return at > 0 ? title[..at].Trim() : null;
    }

    [GeneratedRegex(@"^nxm://([a-z0-9]+)/mods/(\d+)/files/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NxmRegex();

    [GeneratedRegex(@"nexusmods\.com/(?:games/)?([a-z0-9]+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PageRegex();

    [GeneratedRegex(@"nexusmods\.com/(?:games/)?[a-z0-9]+/mods/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ModRegex();

    [GeneratedRegex(@"^(.+?)-(\d+)-(?:(.*)-)?(\d{10})$")]
    private static partial Regex ArchiveRegex();

    [GeneratedRegex(@"^(.+) (\d+) (\S+) \d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z \w+$")]
    private static partial Regex CurrentArchiveRegex();

    [GeneratedRegex(@"[?&]file_id=(\d+)")]
    private static partial Regex FileIdRegex();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
