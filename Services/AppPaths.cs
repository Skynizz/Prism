namespace Prism.Services;

/// <summary>Emplacements sur disque utilises par Prism. Tout vit sous %LOCALAPPDATA%\Prism.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prism");

    public static string Cache => Ensure(Path.Combine(Root, "cache"));
    public static string DllCache => Ensure(Path.Combine(Cache, "dll"));
    public static string ComponentCache => Ensure(Path.Combine(Cache, "components"));
    public static string Backups => Ensure(Path.Combine(Root, "backups"));
    public static string Tools => Ensure(Path.Combine(Root, "tools"));
    public static string Logs => Ensure(Path.Combine(Root, "logs"));

    public static string ManifestFile => Path.Combine(Cache, "dll-manifest.json");
    public static string SettingsFile => Path.Combine(Ensure(Root), "settings.json");
    public static string ProfilesFile => Path.Combine(Ensure(Root), "profiles.json");
    public static string BackupIndexFile => Path.Combine(Ensure(Root), "backups.json");
    public static string DeploymentsFile => Path.Combine(Ensure(Root), "deployments.json");

    public static string BackupDirFor(string gameId)
        => Ensure(Path.Combine(Backups, Sanitize(gameId)));

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Rend une chaine utilisable comme nom de dossier.</summary>
    public static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buf = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
            buf[i] = Array.IndexOf(invalid, s[i]) >= 0 ? '_' : s[i];
        return new string(buf);
    }
}
