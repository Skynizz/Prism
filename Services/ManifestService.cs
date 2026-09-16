using System.Text.Json;
using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Catalogue des versions de DLL disponibles. La source est le manifeste du projet
/// DLSS Swapper : chaque entree porte son MD5 et l'etat de la signature NVIDIA, ce
/// qui permet de refuser un fichier altere avant de l'injecter dans un jeu.
/// </summary>
public sealed class ManifestService
{
    private const string ManifestUrl =
        "https://raw.githubusercontent.com/beeradmoore/dlss-swapper-manifest-builder/main/manifest.json";

    /// <summary>Au-dela, on retelecharge le manifeste au demarrage.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    private readonly DownloadService _downloads = new();
    private DllManifest _manifest = new();

    public DateTimeOffset? FetchedAt { get; private set; }
    public bool Loaded { get; private set; }

    /// <summary>Toutes les versions connues pour une famille, de la plus recente a la plus ancienne.</summary>
    public IReadOnlyList<DllRecord> For(DllKind kind)
    {
        var list = kind switch
        {
            DllKind.Dlss => _manifest.Dlss,
            DllKind.DlssG => _manifest.DlssG,
            DllKind.DlssD => _manifest.DlssD,
            DllKind.FsrDx12 => _manifest.FsrDx12,
            DllKind.FsrVk => _manifest.FsrVk,
            DllKind.XeSS => _manifest.XeSS,
            DllKind.XeSSFg => _manifest.XeSSFg,
            DllKind.XeLL => _manifest.XeLL,
            _ => new List<DllRecord>()
        };

        foreach (var r in list) r.Kind = kind;
        return list.OrderByDescending(r => r.VersionNumber).ToList();
    }

    /// <summary>
    /// Version recommandee : la plus recente signee et non marquee "dev". C'est ce que
    /// l'interface propose par defaut, sans jamais figer un numero en dur dans le code.
    /// </summary>
    public DllRecord? Recommended(DllKind kind, bool allowDev = false)
        => For(kind).FirstOrDefault(r => r.IsSignatureValid && (allowDev || !r.IsDevFile));

    public DllRecord? Find(DllKind kind, string version)
        => For(kind).FirstOrDefault(r => r.Version == version);

    public async Task LoadAsync(bool force = false, CancellationToken ct = default)
    {
        var cached = AppPaths.ManifestFile;
        var fresh = File.Exists(cached) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cached) < MaxAge;

        if (!force && fresh && TryLoadLocal(cached)) return;

        try
        {
            var json = await _downloads.GetStringAsync(ManifestUrl, ct);
            // On ne remplace le cache qu'apres un parsing reussi.
            var parsed = JsonSerializer.Deserialize<DllManifest>(json, JsonStore.Options);
            if (parsed is null) throw new InvalidDataException(Loc.T("err.empty_manifest"));

            _manifest = parsed;
            Loaded = true;
            FetchedAt = DateTimeOffset.Now;
            await File.WriteAllTextAsync(cached, json, ct);
            Log.Write($"Manifest fetched: {parsed.Dlss.Count} DLSS, {parsed.DlssG.Count} DLSS-G, {parsed.DlssD.Count} DLSS-D.");
        }
        catch (Exception ex)
        {
            Log.Write($"Cannot fetch the manifest ({ex.Message}) — falling back to the cache.");
            if (!TryLoadLocal(cached))
                Log.Write("No manifest cache available.");
        }
    }

    private bool TryLoadLocal(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var parsed = JsonSerializer.Deserialize<DllManifest>(File.ReadAllText(path), JsonStore.Options);
            if (parsed is null) return false;

            _manifest = parsed;
            Loaded = true;
            FetchedAt = new DateTimeOffset(File.GetLastWriteTime(path));
            return true;
        }
        catch { return false; }
    }
}
