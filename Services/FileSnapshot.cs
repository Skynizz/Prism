using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Photographie des fichiers de mod d'un jeu avant une reparation en plusieurs etapes. Chaque
/// etape est deja une transaction ; la photographie rend l'ensemble atomique : si une etape
/// echoue, <see cref="Restore"/> remet chaque fichier tel qu'il etait et retire ceux apparus depuis.
///
/// Ne sont concernes que les fichiers qu'une installation de mod peut toucher : runtimes NGX,
/// Streamline, compilateur de shaders, addons, DLL proxy, ReShade et OptiScaler. Les fichiers du jeu
/// eux-memes ne sont ni copies ni retires.
/// </summary>
public sealed class FileSnapshot : IDisposable
{
    private const string Src = "snapshot";

    private static readonly string[] Patterns =
    {
        "nvngx_*.dll", "sl.*.dll", "d3dcompiler_47.dll", "*.addon64", "*.addon32",
        "ReShade.ini", "ReShade64.dll", "ReShade32.dll", "OptiScaler.ini", "OptiScaler.dll", "OptiScaler.asi",
        "fakenvapi.dll", "fakenvapi.ini",
        "dxgi.dll", "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dinput8.dll", "opengl32.dll",
        "winmm.dll", "version.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll"
    };

    private readonly string _root;
    private readonly List<string> _dirs;
    private readonly Dictionary<string, string> _copies = new(StringComparer.OrdinalIgnoreCase);

    private FileSnapshot(IEnumerable<string> dirs)
    {
        _root = Path.Combine(AppPaths.Cache, "snapshots", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(_root);
        _dirs = dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var i = 0;
        foreach (var file in Matching())
        {
            var copy = Path.Combine(_root, $"{i++:D4}_{Path.GetFileName(file)}");
            File.Copy(file, copy, overwrite: true);
            _copies[file] = copy;
        }
        Log.Info(Src, $"{_copies.Count} file(s) captured in {_dirs.Count} folder(s)");
    }

    public static FileSnapshot Take(IEnumerable<string> dirs) => new(dirs);

    public int Count => _copies.Count;

    /// <summary>Remet chaque fichier capture et retire ceux qui n'existaient pas. Renvoie les echecs.</summary>
    public List<string> Restore()
    {
        var failed = new List<string>();

        foreach (var file in Matching().Where(f => !_copies.ContainsKey(f)).ToList())
        {
            try { DllInstaller.ClearReadOnly(file); File.Delete(file); }
            catch (Exception ex) { failed.Add(Path.GetFileName(file)); Log.Warn(Src, $"Cannot remove {file}: {ex.Message}"); }
        }

        foreach (var (original, copy) in _copies)
        {
            try
            {
                if (File.Exists(original)) DllInstaller.ClearReadOnly(original);
                File.Copy(copy, original, overwrite: true);
            }
            catch (Exception ex) { failed.Add(Path.GetFileName(original)); Log.Warn(Src, $"Cannot restore {original}: {ex.Message}"); }
        }

        Log.Info(Src, $"Restored {_copies.Count} file(s), {failed.Count} failure(s)");
        return failed;
    }

    private IEnumerable<string> Matching()
        => _dirs.SelectMany(d => Patterns.SelectMany(p =>
            {
                try { return Directory.GetFiles(d, p); }
                catch { return Array.Empty<string>(); }
            }))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* le cache est nettoyable */ }
    }
}
