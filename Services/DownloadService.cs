using System.Net.Http;
using System.Security.Cryptography;
using Prism.Core;

namespace Prism.Services;

/// <summary>Telechargements avec progression, reprise par cache et verification d'empreinte.</summary>
public sealed class DownloadService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        // GitHub refuse les requetes sans User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Prism/1.0 (+game-mod-manager)");
        return c;
    }

    public static HttpClient Client => Http;

    /// <summary>
    /// Telecharge <paramref name="url"/> vers <paramref name="destination"/>.
    /// Si le fichier existe deja et que son empreinte correspond, rien n'est retelecharge.
    /// </summary>
    public async Task<string> DownloadAsync(
        string url,
        string destination,
        string? expectedMd5 = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (File.Exists(destination))
        {
            if (expectedMd5 is null || await Md5Async(destination, ct) == Normalize(expectedMd5))
            {
                progress?.Report(100);
                return destination;
            }
            File.Delete(destination);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tmp = destination + ".part";

        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1L;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                var buffer = new byte[1 << 16];
                long read = 0;
                int n;
                var lastReport = -1.0;

                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total <= 0) continue;

                    var pct = read * 100.0 / total;
                    // Un rapport par pourcent suffit : inutile de saturer le thread UI.
                    if (pct - lastReport >= 1.0)
                    {
                        lastReport = pct;
                        progress?.Report(pct);
                    }
                }
            }
        }

        if (expectedMd5 is not null)
        {
            var actual = await Md5Async(tmp, ct);
            if (actual != Normalize(expectedMd5))
            {
                File.Delete(tmp);
                throw new InvalidDataException(
                    Loc.T("err.md5", Path.GetFileName(destination), Normalize(expectedMd5), actual));
            }
        }

        File.Move(tmp, destination, overwrite: true);
        progress?.Report(100);
        return destination;
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
        => await Http.GetStringAsync(url, ct);

    public static async Task<string> Md5Async(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        using var md5 = MD5.Create();
        var hash = await md5.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash);
    }

    /// <summary>Variante synchrone, pour les verifications d'integrite hors chemin asynchrone.</summary>
    public static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, DateTime At, string Hash)>
        HashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Empreinte avec memorisation, invalidee par la taille et la date d'ecriture.
    ///
    /// La couverture d'un paquet est recalculee a chaque rafraichissement de la fiche
    /// d'un titre ; sans cache, cela relirait plusieurs centaines de megaoctets a
    /// chaque fois — <c>nvngx_dlssnr.dll</c> pese a lui seul 165 Mo.
    /// </summary>
    public static string Sha256Cached(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "";

            if (HashCache.TryGetValue(path, out var hit)
                && hit.Size == info.Length && hit.At == info.LastWriteTimeUtc)
                return hit.Hash;

            var hash = Sha256(path);
            HashCache[path] = (info.Length, info.LastWriteTimeUtc, hash);
            return hash;
        }
        catch { return ""; }
    }

    private static string Normalize(string hash) => hash.Replace("-", "").Trim().ToUpperInvariant();
}
