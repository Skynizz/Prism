using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Prism.Core;

namespace Prism.Services;

/// <summary>Une version de Prism publiee sur GitHub et plus recente que celle installee.</summary>
public sealed record UpdateInfo(
    Version Version, string Tag, string Notes, string HtmlUrl,
    string ZipUrl, string? HashUrl, long Size, bool Prerelease);

/// <summary>
/// Mises a jour de Prism lui-meme, depuis les releases GitHub.
///
/// Une release contient <c>Prism-&lt;version&gt;-win-x64.zip</c> et son empreinte
/// <c>.zip.sha256</c> (voir scripts/release.ps1). Sequence, sans raccourci :
///  1. lire les releases publiees et retenir la plus recente superieure a la version en place ;
///  2. telecharger, puis refuser toute archive dont l'empreinte ne correspond pas ;
///  3. si Prism est signe, exiger la meme signature sur le nouvel executable ;
///  4. remplacer les fichiers : Windows autorise a renommer un executable en cours
///     d'execution, chaque fichier en place devient <c>.old</c> et le nouveau prend sa place ;
///     au moindre echec, tout est remis comme avant ;
///  5. redemarrer ; les <c>.old</c> sont supprimes au lancement suivant.
/// </summary>
public sealed class UpdateService
{
    private const string Src = "update";
    private const string OldSuffix = ".old";

    /// <summary>
    /// Depot qui publie les versions. Code ouvert : le depot du projet. Code ferme : un depot
    /// public dedie aux releases, par exemple « Skynizz/Prism-releases ». Un depot prive n'est
    /// pas lisible par les utilisateurs.
    /// </summary>
    public const string Repo = "Skynizz/Prism";

    private readonly DownloadService _downloads;

    public UpdateService(DownloadService downloads) => _downloads = downloads;

    /// <summary>Version de l'executable en cours, fixee par &lt;Version&gt; du projet.</summary>
    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0));

    public static string CurrentLabel => Current.ToString(3);

    /// <summary>Derniere erreur de verification, pour l'affichage ; null si tout va bien.</summary>
    public string? LastError { get; private set; }

    // ------------------------------------------------------------ Verification

    public async Task<UpdateInfo?> CheckAsync(bool includePrerelease, CancellationToken ct = default)
    {
        LastError = null;
        string json;
        try
        {
            json = await _downloads.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=20", ct);
        }
        catch (HttpRequestException ex)
        {
            // 404 : depot prive ou encore sans release.
            LastError = Loc.T("upd.err.offline");
            Log.Warn(Src, $"Releases de {Repo} illisibles : {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = Loc.T("upd.err.offline");
            Log.Warn(Src, $"Verification des mises a jour impossible : {ex.Message}");
            return null;
        }

        UpdateInfo? best = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (Bool(r, "draft")) continue;
                var prerelease = Bool(r, "prerelease");
                if (prerelease && !includePrerelease) continue;

                var tag = Str(r, "tag_name");
                if (ParseTag(tag) is not { } version || version <= Current) continue;
                if (best is not null && version <= best.Version) continue;

                string? zip = null, hash = null;
                long size = 0;
                if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = Str(a, "name");
                        var url = Str(a, "browser_download_url");
                        if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            zip = url;
                            size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                        }
                        else if (name.EndsWith("-win-x64.zip.sha256", StringComparison.OrdinalIgnoreCase))
                            hash = url;
                    }
                }
                if (zip is null) continue;

                best = new UpdateInfo(version, tag, Str(r, "body").Trim(), Str(r, "html_url"), zip, hash, size, prerelease);
            }
        }
        catch (Exception ex)
        {
            LastError = Loc.T("upd.err.offline");
            Log.Warn(Src, $"Reponse des releases illisible : {ex.Message}");
            return null;
        }

        Log.Info(Src, best is null
            ? $"Prism {CurrentLabel} est a jour"
            : $"Mise a jour disponible : {best.Tag}{(best.Prerelease ? " (test)" : "")}");
        return best;
    }

    // ----------------------------------------------------------- Telechargement

    /// <summary>Telecharge et verifie la mise a jour ; renvoie le dossier pret a installer.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(AppPaths.Cache, "updates", AppPaths.Sanitize(info.Tag));
        var zip = Path.Combine(dir, $"Prism-{info.Version.ToString(3)}-win-x64.zip");

        // Sans empreinte publiee, rien ne prouve que l'archive est celle qui a ete publiee.
        if (info.HashUrl is null) throw new InvalidDataException(Loc.T("upd.err.no_hash"));
        var published = Regex.Match(await _downloads.GetStringAsync(info.HashUrl, ct), "[0-9a-fA-F]{64}").Value;
        if (published.Length == 0) throw new InvalidDataException(Loc.T("upd.err.no_hash"));

        await _downloads.DownloadAsync(info.ZipUrl, zip, null, progress, ct);

        var actual = await DownloadService.Sha256Async(zip, ct);
        if (!actual.Equals(published, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(zip); } catch { }
            Log.Error(Src, $"Empreinte refusee pour {info.Tag} : attendu {published}, obtenu {actual}");
            throw new InvalidDataException(Loc.T("upd.err.hash"));
        }

        var staging = Path.Combine(dir, "files");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        await ArchiveExtractor.ExtractAsync(zip, staging, ct);

        var exe = Directory.EnumerateFiles(staging, "Prism.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidDataException(Loc.T("upd.err.package"));

        // Une version signee n'accepte qu'une mise a jour signee par le meme editeur.
        var mine = Authenticode.Verify(Environment.ProcessPath ?? "");
        if (mine.Valid)
        {
            var theirs = Authenticode.Verify(exe);
            if (!theirs.Valid || !string.Equals(theirs.Signer, mine.Signer, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error(Src, $"Signature refusee pour {info.Tag} : {theirs.Signer ?? "aucune"} (attendu {mine.Signer})");
                throw new InvalidDataException(Loc.T("upd.err.signature"));
            }
        }
        else
        {
            Log.Warn(Src, "Prism n'est pas signe : la signature de la mise a jour n'est pas exigee.");
        }

        Log.Info(Src, $"{info.Tag} telechargee et verifiee (SHA-256 {actual[..12]}…)");
        return Path.GetDirectoryName(exe)!;
    }

    // --------------------------------------------------------------- Installation

    /// <summary>Remplace les fichiers de Prism par ceux du dossier verifie, ou ne change rien.</summary>
    public static void Apply(string staging)
    {
        var appDir = AppContext.BaseDirectory;
        if (!CanWrite(appDir)) throw new UnauthorizedAccessException(Loc.T("upd.err.readonly", appDir));

        var done = new List<(string Dest, bool HadOld)>();
        try
        {
            foreach (var source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(appDir, Path.GetRelativePath(staging, source));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                var old = dest + OldSuffix;
                if (File.Exists(old)) File.Delete(old);

                var hadOld = File.Exists(dest);
                if (hadOld) File.Move(dest, old);
                done.Add((dest, hadOld));

                File.Copy(source, dest, overwrite: true);
            }
            Log.Info(Src, $"{done.Count} fichier(s) remplace(s) dans {appDir}");
        }
        catch (Exception ex)
        {
            Log.Error(Src, $"Installation interrompue ({ex.Message}) : retour a la version en place");
            for (var i = done.Count - 1; i >= 0; i--)
            {
                var (dest, hadOld) = done[i];
                try
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    if (hadOld) File.Move(dest + OldSuffix, dest);
                }
                catch (Exception rollback) { Log.Error(Src, $"Retour impossible pour {dest} : {rollback.Message}"); }
            }
            throw;
        }
    }

    /// <summary>Relance la nouvelle version et ferme celle-ci.</summary>
    public static void Restart()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Prism.exe");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        System.Windows.Application.Current?.Shutdown();
    }

    /// <summary>Supprime les fichiers laisses par la version precedente.</summary>
    public static void CleanupPreviousVersion()
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(AppContext.BaseDirectory, "*" + OldSuffix, SearchOption.AllDirectories))
            {
                try { File.Delete(old); }
                catch { /* encore verrouille : ce sera pour le prochain lancement */ }
            }
        }
        catch { /* dossier illisible : sans consequence */ }
    }

    // ------------------------------------------------------------- Utilitaires

    /// <summary>« v1.2.0 », « 1.2.0-beta.1 » → 1.2.0.</summary>
    public static Version? ParseTag(string tag)
    {
        var m = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".prism-write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
