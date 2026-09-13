using System.Diagnostics;
using System.IO.Compression;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Extraction d'archives. Le .zip passe par la bibliotheque standard ; le .7z
/// (format des releases OptiScaler) exige 7zr.exe, qu'on recupere a la demande
/// dans le dossier outils de Prism.
/// </summary>
public static class ArchiveExtractor
{
    private const string SevenZrUrl = "https://www.7-zip.org/a/7zr.exe";

    public static async Task<string> ExtractAsync(string archive, string targetDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        var ext = Path.GetExtension(archive).ToLowerInvariant();

        switch (ext)
        {
            case ".zip":
                ExtractZip(archive, targetDir);
                return targetDir;

            case ".7z":
                await ExtractSevenZipAsync(archive, targetDir, ct);
                return targetDir;

            default:
                throw new NotSupportedException(Loc.T("err.archive_format", ext));
        }
    }

    private static void ExtractZip(string archive, string targetDir)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // dossier

            var dest = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            // Garde-fou zip-slip : une entree ne doit jamais sortir du dossier cible.
            if (!dest.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
            {
                Log.Write($"Entree d'archive ignoree (chemin hors cible) : {entry.FullName}");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    private static async Task ExtractSevenZipAsync(string archive, string targetDir, CancellationToken ct)
    {
        var exe = await EnsureSevenZrAsync(ct);

        var psi = new ProcessStartInfo(exe)
        {
            // x = extraction avec arborescence, -y = repondre oui, -o = destination
            Arguments = $"x \"{archive}\" -o\"{targetDir}\" -y",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException(Loc.T("err.7zr_launch"));
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(Loc.T("err.7zr_failed", proc.ExitCode, stderr + stdout));
    }

    /// <summary>Recupere 7zr.exe (environ 600 Ko) la premiere fois qu'une archive .7z se presente.</summary>
    public static async Task<string> EnsureSevenZrAsync(CancellationToken ct = default)
    {
        var exe = Path.Combine(AppPaths.Tools, "7zr.exe");
        if (File.Exists(exe) && new FileInfo(exe).Length > 100_000) return exe;

        // Une installation 7-Zip deja presente evite le telechargement.
        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        Log.Write("Telechargement de 7zr.exe pour l'extraction des archives .7z");
        await new DownloadService().DownloadAsync(SevenZrUrl, exe, null, null, ct);
        return exe;
    }

    /// <summary>
    /// Extensions qui n'ont rien a faire dans un dossier de jeu. Les archives
    /// d'OptiScaler et consorts embarquent des notices, des scripts d'installation
    /// et des marqueurs sans extension du genre « !! EXTRACT ALL FILES ... !! » :
    /// les copier ne sert a rien et brouille la lecture de ce que Prism a ajoute.
    /// </summary>
    private static readonly string[] NotPayload =
    {
        ".bat", ".cmd", ".sh", ".ps1", ".txt", ".md", ".pdf", ".html", ".htm", ".url", ".nfo", ".log"
    };

    /// <summary>Vrai si le fichier a une raison d'etre pose a cote du jeu.</summary>
    public static bool IsPayload(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var ext = Path.GetExtension(name);

        // Un fichier sans extension n'est jamais charge par un moteur : c'est une notice.
        if (string.IsNullOrEmpty(ext)) return false;

        return !NotPayload.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Retrouve un fichier par nom dans une arborescence extraite.</summary>
    public static string? FindFile(string root, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }
}
