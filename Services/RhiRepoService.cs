using System.Text.Json;
using System.Text.RegularExpressions;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Releases de <c>RankFTW/rhi-repo</c> : l'addon RenoDX DLSS 5 et les runtimes NVIDIA
/// publies version par version, chacun dans sa propre release.
///
/// C'est la source que suivent les installeurs DLSS 5 actifs. Prism n'y fait pas
/// confiance aveuglement : toute DLL qui en sort doit porter une signature Authenticode
/// NVIDIA valide avant d'etre posee (voir <see cref="Authenticode"/>).
/// </summary>
public sealed partial class RhiRepoService
{
    private const string Src = "rhi";

    public const string Repo = "RankFTW/rhi-repo";
    public const string ReleasesPage = "https://github.com/RankFTW/rhi-repo/releases";
    private const string ReleasesApi = "https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100";

    public const string Dlss5AddonPrefix = "renodx-dlss5-";

    /// <summary>Pile runtime posee avec l'addon DLSS 5 : DLSS 310.8.0 + Streamline 2.13.</summary>
    public static readonly string[] Dlss5BaseStack =
    {
        "dlss-310.8.0",
        "dlssg-310.8.0",
        "dlssd-310.7.129",
        "streamline-2.13.0.0"
    };

    /// <summary>Runtime neural d'origine, signe NVIDIA : il ne s'initialise que sur RTX 50.</summary>
    public const string NeuralRuntimeOriginal = "dlssnr-310.8.0";

    /// <summary>Build repatchee multi-generations pour RTX 20 a 40, epinglee par empreinte.</summary>
    public const string NeuralRuntimePatched = "dlssnr-310.8.SF-v2";

    /// <summary>
    /// La pile complete pour ce GPU. Sur une RTX 40, l'original signe est reconnu par
    /// RenoDX mais echoue a l'initialisation (0xBAD00001) : c'est la build repatchee
    /// qui fonctionne.
    /// </summary>
    public static IReadOnlyList<string> Dlss5StackFor(GpuInfo gpu)
        => new[] { gpu.SupportsNativeMfg ? NeuralRuntimeOriginal : NeuralRuntimePatched }
            .Concat(Dlss5BaseStack)
            .ToList();

    public sealed record Release(string Tag, string AssetName, string Url, long Size, DateTimeOffset Published)
    {
        /// <summary>Version lisible : « 5.2.1 » pour « renodx-dlss5-5.2.1 ».</summary>
        public string Version => VersionPart().Match(Tag) is { Success: true } m ? m.Value : Tag;
    }

    private readonly DownloadService _downloads;
    private List<Release> _releases = new();

    public RhiRepoService(DownloadService downloads) => _downloads = downloads;

    public IReadOnlyList<Release> Releases => _releases;

    public bool IsLoaded => _releases.Count > 0;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var cache = Path.Combine(AppPaths.Ensure(Path.Combine(AppPaths.ComponentCache, "rhi")), "releases.json");

        try
        {
            var json = await _downloads.GetStringAsync(ReleasesApi, ct);
            var parsed = Parse(json);
            if (parsed.Count > 0)
            {
                _releases = parsed;
                await File.WriteAllTextAsync(cache, json, ct);
                Log.Info(Src, $"rhi-repo: {parsed.Count} releases");
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(Src, $"rhi-repo unavailable: {ex.Message}");
        }

        if (File.Exists(cache))
        {
            _releases = Parse(await File.ReadAllTextAsync(cache, ct));
            Log.Info(Src, $"rhi-repo: {_releases.Count} releases (cache)");
        }
    }

    private static List<Release> Parse(string json)
    {
        var list = new List<Release>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (!r.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) continue;
                if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

                var zip = assets.EnumerateArray().FirstOrDefault(a =>
                    a.TryGetProperty("name", out var n) && n.GetString() is { } s
                    && s.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (zip.ValueKind != JsonValueKind.Object) continue;

                var published = r.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var d)
                    ? d : DateTimeOffset.MinValue;

                list.Add(new Release(
                    tag.GetString()!,
                    zip.GetProperty("name").GetString()!,
                    zip.GetProperty("browser_download_url").GetString()!,
                    zip.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                    published));
            }
        }
        catch (JsonException)
        {
        }
        return list;
    }

    /// <summary>Releases dont le tag commence par le prefixe suivi d'un chiffre, la plus recente d'abord.</summary>
    public IReadOnlyList<Release> Family(string prefix)
        => _releases
            .Where(r => r.Tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && r.Tag.Length > prefix.Length && char.IsDigit(r.Tag[prefix.Length]))
            .OrderByDescending(r => VersionKey(r.Tag, prefix), VersionComparer.Instance)
            .ToList();

    public Release? ByTag(string tag)
        => _releases.FirstOrDefault(r => r.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));

    private static int[] VersionKey(string tag, string prefix)
        => Digits().Matches(tag[prefix.Length..]).Select(m => int.TryParse(m.Value, out var n) ? n : 0).ToArray();

    private sealed class VersionComparer : IComparer<int[]>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(int[]? x, int[]? y)
        {
            x ??= Array.Empty<int>();
            y ??= Array.Empty<int>();
            for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                var a = i < x.Length ? x[i] : 0;
                var b = i < y.Length ? y[i] : 0;
                if (a != b) return a.CompareTo(b);
            }
            return 0;
        }
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\d+(\.\d+)*")]
    private static partial Regex VersionPart();
}
