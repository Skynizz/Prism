using System.Text.Json;

namespace Prism.Services;

public sealed record GhAsset(string Name, string Url, long Size);
public sealed record GhRelease(string Tag, DateTimeOffset? PublishedAt, string HtmlUrl, IReadOnlyList<GhAsset> Assets);

/// <summary>
/// Metadonnees de provenance d'un depot. Affichees telles quelles dans le
/// gestionnaire de composants : l'utilisateur doit pouvoir juger une source sans
/// quitter l'application.
/// </summary>
public sealed record GhRepo(string FullName, int Stars, string? License, DateTimeOffset? PushedAt, bool Archived);

/// <summary>
/// Lecture des releases GitHub. Les reponses sont mises en cache sur disque pour
/// ne pas epuiser le quota anonyme de l'API (60 requetes par heure et par IP).
/// </summary>
public sealed class GitHubService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(3);
    private readonly DownloadService _downloads;

    // ComponentCatalog interroge toutes les sources en parallele : le cache memoire
    // doit supporter des ecritures concurrentes.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GhRelease> _memory =
        new(StringComparer.OrdinalIgnoreCase);

    public GitHubService(DownloadService downloads) => _downloads = downloads;

    /// <summary>Derniere release publiee (hors prereleases).</summary>
    public Task<GhRelease?> LatestAsync(string repo, CancellationToken ct = default)
        => FetchAsync(repo, $"https://api.github.com/repos/{repo}/releases/latest", $"{repo}_latest", ct);

    /// <summary>Release identifiee par son tag — RenoDX publie tout sous le tag "snapshot".</summary>
    public Task<GhRelease?> ByTagAsync(string repo, string tag, CancellationToken ct = default)
        => FetchAsync(repo, $"https://api.github.com/repos/{repo}/releases/tags/{tag}", $"{repo}_{tag}", ct);

    /// <summary>Etoiles, licence et date du dernier commit, pour juger de la fraicheur d'une source.</summary>
    public async Task<GhRepo?> RepoAsync(string repo, CancellationToken ct = default)
    {
        var json = await CachedJsonAsync($"https://api.github.com/repos/{repo}", $"{repo}_meta", ct);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("full_name", out var fn)) return null;

            string? license = null;
            if (r.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.Object &&
                lic.TryGetProperty("spdx_id", out var spdx))
                license = spdx.GetString();

            return new GhRepo(
                fn.GetString() ?? repo,
                r.TryGetProperty("stargazers_count", out var s) && s.TryGetInt32(out var sv) ? sv : 0,
                license,
                r.TryGetProperty("pushed_at", out var p) && p.TryGetDateTimeOffset(out var pv) ? pv : null,
                r.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex)
        {
            Log.Warn("github", $"Metadonnees illisibles pour {repo} : {ex.Message}");
            return null;
        }
    }

    /// <summary>Recupere un JSON en passant par le cache disque, avec repli sur un cache perime.</summary>
    private async Task<string?> CachedJsonAsync(string url, string cacheKey, CancellationToken ct)
    {
        var cacheFile = Path.Combine(AppPaths.Cache, "gh_" + AppPaths.Sanitize(cacheKey) + ".json");

        if (File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < CacheTtl)
        {
            try { return await File.ReadAllTextAsync(cacheFile, ct); } catch { }
        }

        try
        {
            var json = await _downloads.GetStringAsync(url, ct);
            await File.WriteAllTextAsync(cacheFile, json, ct);
            return json;
        }
        catch (Exception ex)
        {
            Log.Warn("github", $"API indisponible ({ex.Message}) — repli sur le cache.");
            if (!File.Exists(cacheFile)) return null;
            try { return await File.ReadAllTextAsync(cacheFile, ct); } catch { return null; }
        }
    }

    private async Task<GhRelease?> FetchAsync(string repo, string url, string cacheKey, CancellationToken ct)
    {
        if (_memory.TryGetValue(cacheKey, out var hit)) return hit;

        var cacheFile = Path.Combine(AppPaths.Cache, "gh_" + AppPaths.Sanitize(cacheKey) + ".json");
        string? json = null;

        var cacheFresh = File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < CacheTtl;
        if (cacheFresh)
        {
            try { json = await File.ReadAllTextAsync(cacheFile, ct); } catch { }
        }

        if (json is null)
        {
            try
            {
                json = await _downloads.GetStringAsync(url, ct);
                await File.WriteAllTextAsync(cacheFile, json, ct);
            }
            catch (Exception ex)
            {
                Log.Write($"API GitHub indisponible pour {repo} ({ex.Message}) — tentative de repli sur le cache.");
                // Le cache perime vaut mieux que rien : on affiche une version, meme datee.
                if (File.Exists(cacheFile))
                {
                    try { json = await File.ReadAllTextAsync(cacheFile, ct); } catch { return null; }
                }
                else return null;
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;

            var assets = new List<GhAsset>();
            if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var dl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                    if (name is not null && dl is not null) assets.Add(new GhAsset(name, dl, size));
                }
            }

            var release = new GhRelease(
                r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "?" : "?",
                r.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var pv) ? pv : null,
                r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                assets);

            _memory[cacheKey] = release;
            return release;
        }
        catch (Exception ex)
        {
            Log.Write($"Release GitHub illisible pour {repo} : {ex.Message}");
            return null;
        }
    }
}
