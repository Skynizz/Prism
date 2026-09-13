using System.Text;
using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Catalogue des mods HDR RenoDX. Tous les addons vivent dans une unique release
/// GitHub taguee "snapshot" ; le nom de l'asset porte le slug du jeu.
/// </summary>
public sealed class RenoDxService
{
    public const string Repo = "clshortfuse/renodx";
    private const string SnapshotTag = "snapshot";

    private readonly GitHubService _github;
    private readonly DownloadService _downloads;
    private readonly DeploymentStore _deployments;

    public RenoDxService(GitHubService github, DownloadService downloads, DeploymentStore deployments)
    {
        _github = github;
        _downloads = downloads;
        _deployments = deployments;
    }

    public List<RenoDxAddon> Addons { get; private set; } = new();
    public DateTimeOffset? PublishedAt { get; private set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var release = await _github.ByTagAsync(Repo, SnapshotTag, ct);
        if (release is null) return;

        PublishedAt = release.PublishedAt;
        Addons = release.Assets
            .Where(a => a.Name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase)
                        || a.Name.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase))
            .Select(a => new RenoDxAddon
            {
                AssetName = a.Name,
                DownloadUrl = a.Url,
                Size = a.Size,
                Slug = SlugOf(a.Name),
                Is32Bit = a.Name.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase)
            })
            .OrderBy(a => a.Slug, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Log.Write($"Catalogue RenoDX : {Addons.Count} addons (snapshot du {PublishedAt:yyyy-MM-dd}).");
    }

    /// <summary>"renodx-cyberpunk2077.addon64" devient "cyberpunk2077".</summary>
    private static string SlugOf(string assetName)
    {
        var s = Path.GetFileNameWithoutExtension(assetName);
        return s.StartsWith("renodx-", StringComparison.OrdinalIgnoreCase) ? s[7..] : s;
    }

    /// <summary>
    /// Propose les addons correspondant a un jeu. On compare des formes normalisees
    /// (minuscules, sans espaces ni ponctuation) car les slugs RenoDX sont contractes.
    /// </summary>
    public List<RenoDxAddon> Match(GameInfo game)
    {
        var key = Normalize(game.Name);
        if (key.Length < 3) return new List<RenoDxAddon>();

        var scored = new List<(RenoDxAddon Addon, int Score)>();
        foreach (var addon in Addons)
        {
            var slug = Normalize(addon.Slug);
            var score = Score(slug, key);
            if (score > 0) scored.Add((addon, score));
        }

        var result = scored
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Addon.Slug.Length)
            .ThenBy(x => x.Addon.Is32Bit)
            .Select(x => x.Addon)
            .Take(8)
            .ToList();

        // RenoDX publie aussi des mods au niveau du moteur. Ils sont proposes apres les
        // mods specifiques : c'est souvent la seule option pour un jeu recent.
        foreach (var g in GenericFor(game))
            if (!result.Contains(g)) result.Add(g);

        return result;
    }

    /// <summary>Mods generiques applicables a un jeu selon son moteur.</summary>
    public IEnumerable<RenoDxAddon> GenericFor(GameInfo game)
    {
        foreach (var addon in Addons.Where(a => !a.Is32Bit))
        {
            var slug = addon.Slug.ToLowerInvariant();
            if (slug == "unrealengine" && game.Engine == "Unreal") yield return addon;
            else if (slug == "generic") yield return addon;
        }
    }

    /// <summary>
    /// Les slugs RenoDX sont des abreviations ("asscreedmirage" pour Assassin's Creed
    /// Mirage, "doomtda" pour DOOM The Dark Ages). Une comparaison par sous-sequence
    /// ordonnee les rattrape la ou un prefixe commun echoue, tout en restant assez
    /// discriminante pour ne pas apparier n'importe quoi.
    /// </summary>
    private static int Score(string slug, string key)
    {
        if (slug.Length == 0) return 0;
        if (slug == key) return 100;
        if (slug.StartsWith(key) || key.StartsWith(slug)) return 85;
        if (slug.Contains(key) || key.Contains(slug)) return 70;

        // Sous-sequence : exige un debut commun pour eviter les rapprochements fortuits.
        var prefix = CommonPrefix(slug, key);
        if (prefix < 3) return 0;

        if (slug.Length >= 6 && IsSubsequence(slug, key)) return 55 + prefix;
        if (key.Length >= 6 && IsSubsequence(key, slug)) return 45 + prefix;
        return 0;
    }

    /// <summary>Vrai si tous les caracteres de <paramref name="needle"/> apparaissent dans l'ordre.</summary>
    private static bool IsSubsequence(string needle, string haystack)
    {
        var i = 0;
        foreach (var c in haystack)
        {
            if (i < needle.Length && needle[i] == c) i++;
            if (i == needle.Length) return true;
        }
        return i == needle.Length;
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// Depose l'addon a cote de l'executable du jeu. ReShade charge automatiquement
    /// les .addon64 places dans ce dossier.
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        GameInfo game, RenoDxAddon addon, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var targetDir = Path.GetDirectoryName(game.Executable) ?? game.InstallDir;
        if (!Directory.Exists(targetDir))
            return new InstallResult(false, Loc.T("err.target_missing"));

        try
        {
            var cached = Path.Combine(AppPaths.ComponentCache, "renodx", addon.AssetName);
            await _downloads.DownloadAsync(addon.DownloadUrl, cached, null, progress, ct);

            var dest = Path.Combine(targetDir, addon.AssetName);
            DllInstaller.ClearReadOnly(dest);
            File.Copy(cached, dest, overwrite: true);
            _deployments.Record(game, dest, "RenoDX", addon.AssetName);

            DllDetector.Inspect(game);
            return new InstallResult(true, Loc.T("renodx.ok", addon.AssetName, targetDir), 1);
        }
        catch (Exception ex)
        {
            Log.Write($"Installation RenoDX echouee : {ex.Message}");
            return new InstallResult(false, Loc.T("err.install_failed", ex.Message));
        }
    }

    public InstallResult Remove(GameInfo game)
    {
        var dir = Path.GetDirectoryName(game.Executable) ?? game.InstallDir;
        var removed = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "renodx*.addon*"))
            {
                DllInstaller.ClearReadOnly(f);
                File.Delete(f);
                removed++;
            }
        }
        catch (Exception ex)
        {
            return new InstallResult(false, Loc.T("err.remove_failed", ex.Message));
        }

        DllDetector.Inspect(game);
        return removed > 0
            ? new InstallResult(true, Loc.T("renodx.removed", removed), removed)
            : new InstallResult(false, Loc.T("renodx.none"));
    }
}
