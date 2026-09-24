using System.Diagnostics;
using System.Text.Json;
using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Donne a tout jeu — Epic, GOG, Xbox, Ubisoft, ajoute a la main, sorti hier — la meme identite
/// qu'un jeu Steam : son nom officiel et son AppID. Les catalogues s'appuient dessus (le plan
/// RenoDX HDR cherche d'abord par AppID), et « Dawnwalker » devient « The Blood of Dawnwalker ».
///
/// Sources, de la plus sure a la moins sure :
///  1. le manifeste Steam, pour un jeu Steam ;
///  2. les fichiers de l'editeur dans le dossier : <c>steam_appid.txt</c>, <c>goggame-*.info</c>
///     (JSON : name, gameId, playTasks) ;
///  3. la recherche du magasin Steam (store.steampowered.com/api/storesearch), retenue seulement
///     sans ambiguite — « Engine » y renvoie Wallpaper Engine, SpaceEngine... ;
///  4. le choix de l'utilisateur, qui prime sur tout et n'est jamais recalcule.
/// Seul le nom cherche part sur le reseau, et uniquement si l'option est active.
/// </summary>
public sealed class GameIdentityService
{
    private const string Src = "identity";
    private const string SearchApi = "https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=";
    private const string DetailsApi = "https://store.steampowered.com/api/appdetails?filters=basic&appids=";

    /// <summary>
    /// Titres de magasin qui ne sont pas le jeu lui-meme. Mots entiers seulement : « Backpack Hero »,
    /// « Content Warning » ou « Demon's Souls » sont des jeux.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex NotTheGame = new(
        @"\b(soundtrack|ost|dlc|demo|playtest|artbook|season pass|edition content|upgrade pack|expansion pass)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly DownloadService _downloads;
    private readonly SettingsStore _settings;

    public GameIdentityService(DownloadService downloads, SettingsStore settings)
    {
        _downloads = downloads;
        _settings = settings;
    }

    private Dictionary<string, GameIdentity> Cache => _settings.Current.Identities;

    // --------------------------------------------------------------- Appliquer

    /// <summary>Identites deja connues : appliquees tout de suite, sans reseau.</summary>
    public void ApplyKnown(IEnumerable<GameInfo> games)
    {
        foreach (var game in games)
        {
            if (game.Platform == GamePlatform.Steam) { game.IdentitySource = IdentitySource.Steam; continue; }
            if (Cache.TryGetValue(game.Id, out var id)) Apply(game, id);
        }
    }

    /// <summary>Jeux sans identite fiable : fichiers locaux d'abord, magasin ensuite.</summary>
    public async Task<List<GameInfo>> ResolvePendingAsync(IEnumerable<GameInfo> games, CancellationToken ct = default)
    {
        var changed = new List<GameInfo>();
        foreach (var game in games.Where(g => g.Platform != GamePlatform.Steam && !Cache.ContainsKey(g.Id)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var found = FromLocalFiles(game);
            if (found is not null && found.SteamAppId is { } appId && string.IsNullOrWhiteSpace(found.Name))
                found.Name = await NameForAppIdAsync(appId, ct) ?? "";

            if ((found is null || string.IsNullOrWhiteSpace(found.Name)) && _settings.Current.IdentifyOnline)
                found = await FromStoreAsync(game, ct);

            if (found is null || string.IsNullOrWhiteSpace(found.Name)) continue;
            Cache[game.Id] = found;
            Apply(game, found);
            changed.Add(game);
            Log.Info(Src, $"{game.Id}: {found.Name} ({found.SteamAppId?.ToString() ?? "no AppID"}, {found.Source})");
        }

        if (changed.Count > 0) _settings.Save();
        return changed;
    }

    /// <summary>Choix de l'utilisateur : un resultat du magasin, ou un nom libre sans AppID.</summary>
    public void SetByUser(GameInfo game, string name, long? appId)
    {
        var identity = new GameIdentity { Name = name.Trim(), SteamAppId = appId, Source = IdentitySource.User };
        Cache[game.Id] = identity;
        _settings.Save();
        Apply(game, identity);
    }

    /// <summary>Oublie l'identite choisie : le jeu reprend son nom detecte au prochain scan.</summary>
    public void Forget(GameInfo game)
    {
        if (Cache.Remove(game.Id)) _settings.Save();
    }

    private static void Apply(GameInfo game, GameIdentity id)
    {
        if (!string.IsNullOrWhiteSpace(id.Name)) game.Name = id.Name;
        game.SteamAppId = id.SteamAppId ?? game.SteamAppId;
        game.IdentitySource = id.Source;
    }

    // ------------------------------------------------------------ Fichiers locaux

    /// <summary>steam_appid.txt et goggame-*.info, a la racine du jeu ou a cote de l'executable.</summary>
    public static GameIdentity? FromLocalFiles(GameInfo game)
    {
        var dirs = new[] { game.InstallDir, Path.GetDirectoryName(game.Executable ?? "") }
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            foreach (var info in SafeFiles(dir!, "goggame-*.info"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(info));
                    if (doc.RootElement.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                        return new GameIdentity { Name = name, Source = IdentitySource.LocalFile };
                }
                catch (Exception ex) when (ex is JsonException or IOException) { /* fichier illisible : on passe */ }
            }

            var appIdFile = Path.Combine(dir!, "steam_appid.txt");
            if (File.Exists(appIdFile) && long.TryParse(SafeRead(appIdFile).Trim(), out var appId) && appId > 0)
                return new GameIdentity { SteamAppId = appId, Source = IdentitySource.LocalFile };
        }
        return null;
    }

    // ---------------------------------------------------------------- Magasin

    /// <summary>Recherche sur le magasin Steam ; resultats bruts, pour le choix manuel.</summary>
    public async Task<List<IdentityCandidate>> SearchAsync(string term, CancellationToken ct = default)
    {
        var list = new List<IdentityCandidate>();
        if (string.IsNullOrWhiteSpace(term)) return list;
        try
        {
            var json = await _downloads.GetStringAsync(SearchApi + Uri.EscapeDataString(term.Trim()), ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) && t.GetString() != "app") continue;
                if (item.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name
                    && item.TryGetProperty("id", out var i) && i.TryGetInt64(out var id))
                    list.Add(new IdentityCandidate(name, id));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(Src, $"Store search failed for '{term}': {ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// Retenu seulement sans ambiguite : titre identique une fois normalise, ou titre qui finit
    /// par le nom cherche (« dawnwalker » -&gt; « the blood of dawnwalker ») pour un nom d'au moins
    /// 8 lettres. Entre plusieurs, le plus court : l'edition de base plutot que ses contenus.
    /// </summary>
    private async Task<GameIdentity?> FromStoreAsync(GameInfo game, CancellationToken ct)
    {
        foreach (var term in SearchTerms(game))
        {
            var key = RenoDxWikiService.Normalize(term);
            if (key.Length < 4) continue;

            var results = (await SearchAsync(term, ct))
                .Where(c => !NotTheGame.IsMatch(c.Name))
                .ToList();

            var exact = results.FirstOrDefault(c => RenoDxWikiService.Normalize(c.Name) == key);
            var suffix = key.Length >= 8
                ? results.Where(c => RenoDxWikiService.Normalize(c.Name).EndsWith(key, StringComparison.Ordinal))
                    .OrderBy(c => c.Name.Length).FirstOrDefault()
                : null;

            if ((exact ?? suffix) is { } pick)
                return new GameIdentity { Name = pick.Name, SteamAppId = pick.AppId, Source = IdentitySource.StoreSearch };
        }
        return null;
    }

    /// <summary>Nom du produit declare par l'exe, dossier du jeu, nom de l'exe.</summary>
    public static IEnumerable<string> SearchTerms(GameInfo game)
    {
        var terms = new List<string>();
        try
        {
            if (game.Executable is { } exe && File.Exists(exe)
                && FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim() is { Length: > 0 } product
                && !product.Contains("Unreal", StringComparison.OrdinalIgnoreCase)
                && !product.Contains("Unity", StringComparison.OrdinalIgnoreCase))
                terms.Add(product);
        }
        catch { /* pas de ressource de version */ }

        terms.Add(game.Name);
        terms.Add(Path.GetFileName(game.InstallDir.TrimEnd('\\', '/')));
        if (game.Executable is { } e)
            terms.Add(Path.GetFileNameWithoutExtension(e).Replace("-Win64-Shipping", "", StringComparison.OrdinalIgnoreCase));

        return terms.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string?> NameForAppIdAsync(long appId, CancellationToken ct)
    {
        if (!_settings.Current.IdentifyOnline) return null;
        try
        {
            var json = await _downloads.GetStringAsync(DetailsApi + appId, ct);
            using var doc = JsonDocument.Parse(json);
            // Une edition peut repondre sous un autre identifiant : on prend la premiere reponse.
            foreach (var entry in doc.RootElement.EnumerateObject())
                if (entry.Value.TryGetProperty("data", out var data) && data.TryGetProperty("name", out var n))
                    return n.GetString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(Src, $"App details failed for {appId}: {ex.Message}");
        }
        return null;
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return ""; }
    }
}
