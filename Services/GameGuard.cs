using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Garde-fou avant toute ecriture dans un jeu.
///
/// Un jeu lance tient ses DLL ouvertes : la copie echoue au milieu (« fichier utilise par
/// un autre processus ») et laisse une pile a moitie posee. On refuse donc d'ecrire tant
/// qu'un processus du dossier du jeu tourne — le jeu, mais aussi son launcher ou son
/// rapporteur de plantage — ou qu'un des fichiers vises est verrouille.
/// </summary>
public static class GameGuard
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Null si l'ecriture peut avoir lieu ; sinon l'echec a afficher.</summary>
    public static InstallResult? Check(GameInfo game, IEnumerable<string>? paths = null)
    {
        if (RunningProcess(game) is { } running)
        {
            Log.Warn("guard", $"{game.Name}: write refused, {running} is running");
            return new InstallResult(false, Loc.T("guard.running", running));
        }

        foreach (var path in paths ?? Enumerable.Empty<string>())
        {
            if (!IsLocked(path)) continue;
            Log.Warn("guard", $"{game.Name}: write refused, {path} is locked");
            return new InstallResult(false, Loc.T("guard.locked", Path.GetFileName(path)));
        }

        return null;
    }

    /// <summary>
    /// Nom du jeu s'il tourne, ou null : son executable, ou tout programme lance depuis le
    /// dossier de l'executable — la ou le jeu charge ses DLL. Un rapporteur de plantage
    /// reste ouvert dans un sous-dossier ne bloque pas : s'il tenait un fichier vise, la
    /// verification des verrous le signalerait.
    /// </summary>
    public static string? RunningProcess(GameInfo game)
    {
        string exeDir;
        try { exeDir = Path.GetFullPath(DllInstaller.TargetDirectory(game)).TrimEnd('\\', '/'); }
        catch { return null; }

        var self = Environment.ProcessId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == self || process.Id <= 4) continue;
                var path = ImagePath(process.Id);
                if (path is null) continue;

                var sameExe = game.Executable is { } exe && path.Equals(exe, StringComparison.OrdinalIgnoreCase);
                var besideExe = string.Equals(Path.GetDirectoryName(path), exeDir, StringComparison.OrdinalIgnoreCase);
                if (sameExe || besideExe) return Path.GetFileName(path);
            }
        }
        return null;
    }

    /// <summary>
    /// Vrai si un autre programme tient le fichier ouvert. Une DLL chargee par un processus
    /// refuse l'ouverture exclusive ; un simple attribut lecture seule, lui, n'est pas un verrou.
    /// </summary>
    public static bool IsLocked(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    /// <summary>Violation de partage ou de verrou Windows (codes 32 et 33).</summary>
    public static bool IsSharingViolation(Exception ex)
        => ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>
    /// Chemin de l'image d'un processus. QueryFullProcessImageName fonctionne avec un acces
    /// limite, la ou Process.MainModule echoue sur les processus 32 bits ou proteges.
    /// </summary>
    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
