using System.Diagnostics;
using System.Text.RegularExpressions;
using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Recupere et installe ReShade. On vise systematiquement la variante "Addon",
/// seule capable de charger les .addon64 dont RenoDX depend.
/// </summary>
public sealed class ReShadeService
{
    private const string HomePage = "https://reshade.me/";
    private const string DownloadBase = "https://reshade.me/downloads/";

    private readonly DownloadService _downloads;

    public ReShadeService(DownloadService downloads) => _downloads = downloads;

    public string? LatestVersion { get; private set; }
    public string? LatestUrl { get; private set; }

    /// <summary>Lit la page d'accueil pour en extraire le numero de version courant.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var html = await _downloads.GetStringAsync(HomePage, ct);
            // Le lien de telechargement porte le numero : ReShade_Setup_6.8.0_Addon.exe
            var m = Regex.Matches(html, @"ReShade_Setup_([0-9]+\.[0-9]+\.[0-9]+)_Addon\.exe", RegexOptions.IgnoreCase)
                         .OfType<Match>()
                         .OrderByDescending(x => Version.TryParse(x.Groups[1].Value, out var v) ? v : new Version(0, 0))
                         .FirstOrDefault();

            if (m is null)
            {
                Log.Write("Version de ReShade introuvable sur la page d'accueil.");
                return;
            }

            LatestVersion = m.Groups[1].Value;
            LatestUrl = DownloadBase + m.Value;
            Log.Write($"ReShade disponible : {LatestVersion}");
        }
        catch (Exception ex)
        {
            Log.Write($"Verification de ReShade impossible : {ex.Message}");
        }
    }

    public async Task<string?> EnsureSetupAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (LatestUrl is null) await LoadAsync(ct);
        if (LatestUrl is null) return null;

        var dest = Path.Combine(AppPaths.ComponentCache, "reshade", $"ReShade_Setup_{LatestVersion}_Addon.exe");
        try
        {
            await _downloads.DownloadAsync(LatestUrl, dest, null, progress, ct);
            return dest;
        }
        catch (Exception ex)
        {
            Log.Write($"Telechargement de ReShade echoue : {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Lance l'installeur en mode non interactif sur l'executable du jeu.
    /// ReShade accepte la forme : ReShade_Setup.exe &lt;jeu.exe&gt; --api &lt;api&gt;.
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        GameInfo game, string api = "dxgi", IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(game.Executable) || !File.Exists(game.Executable))
            return new InstallResult(false, Loc.T("reshade.err.no_exe"));

        var setup = await EnsureSetupAsync(progress, ct);
        if (setup is null) return new InstallResult(false, Loc.T("reshade.err.no_setup"));

        try
        {
            var psi = new ProcessStartInfo(setup)
            {
                Arguments = $"\"{game.Executable}\" --api {api}",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(setup)!
            };

            using var proc = Process.Start(psi);
            if (proc is null) return new InstallResult(false, Loc.T("reshade.err.launch"));

            await proc.WaitForExitAsync(ct);
            DllDetector.Inspect(game);

            return game.HasReShade
                ? new InstallResult(true, Loc.T("reshade.ok", LatestVersion ?? "", api), 1)
                : new InstallResult(false, Loc.T("reshade.err.nothing"));
        }
        catch (Exception ex)
        {
            Log.Write($"Installation de ReShade echouee : {ex.Message}");
            return new InstallResult(false, Loc.T("err.install_failed", ex.Message));
        }
    }

    /// <summary>Version de ReShade presente dans le jeu, lue depuis les metadonnees du binaire.</summary>
    public static string? InstalledVersion(GameInfo game)
    {
        var dir = Path.GetDirectoryName(game.Executable) ?? game.InstallDir;
        if (!Directory.Exists(dir)) return null;

        foreach (var candidate in new[] { "ReShade64.dll", "ReShade32.dll" }
                     .Concat(DllDetector.ProxyNames))
        {
            var path = Path.Combine(dir, candidate);
            if (!File.Exists(path)) continue;

            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if ((info.ProductName ?? info.FileDescription ?? "").Contains("ReShade", StringComparison.OrdinalIgnoreCase))
                    return info.ProductVersion ?? info.FileVersion;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Retire les fichiers deposes par ReShade, en laissant les presets de l'utilisateur.</summary>
    public static InstallResult Uninstall(GameInfo game)
    {
        var dir = Path.GetDirectoryName(game.Executable) ?? game.InstallDir;
        var removed = 0;

        // ReShadePreset.ini et reshade-shaders contiennent le travail de l'utilisateur :
        // on ne retire que le moteur et sa configuration technique.
        foreach (var name in new[] { "ReShade64.dll", "ReShade32.dll", "ReShade.ini" }
                     .Concat(DllDetector.ProxyNames))
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;

            // Un dxgi.dll systeme ne doit surtout pas etre supprime : on verifie l'origine.
            try
            {
                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    var tag = $"{info.ProductName} {info.FileDescription}";
                    if (!tag.Contains("ReShade", StringComparison.OrdinalIgnoreCase)) continue;
                }

                DllInstaller.ClearReadOnly(path);
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) { Log.Write($"Suppression de {path} impossible : {ex.Message}"); }
        }

        DllDetector.Inspect(game);
        return removed > 0
            ? new InstallResult(true, Loc.T("reshade.ok.removed", removed), removed)
            : new InstallResult(false, Loc.T("reshade.none"));
    }
}
