using System.Reflection;

namespace Prism.Services;

/// <summary>
/// Emplacements sur disque utilises par Prism. Tout vit sous %LOCALAPPDATA%\Prism — et sous
/// %LOCALAPPDATA%\Prism-dev pour une build de test, qui ne doit jamais toucher aux reglages,
/// aux sauvegardes de jeux ni au registre des modifications de la version stable installee a cote.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Suffixe du canal : vide en version stable, « -dev » quand le numero de version porte une
    /// etiquette de preversion (« 1.2.0-dev »). PRISM_DATA_SUFFIX le force, pour un essai ponctuel.
    /// </summary>
    public static string Channel { get; } = ResolveChannel();

    /// <summary>Vrai pour une build de test, qui vit dans son propre dossier de donnees.</summary>
    public static bool IsDev => Channel.Length > 0;

    private static string ResolveChannel()
    {
        var forced = Environment.GetEnvironmentVariable("PRISM_DATA_SUFFIX");
        if (!string.IsNullOrWhiteSpace(forced)) return "-" + Sanitize(forced.Trim().TrimStart('-'));

        // « 1.2.0+sha » : stable. « 1.2.0-dev.3+sha » : version de test.
        var info = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "";

        var label = info.Split('+')[0];
        var dash = label.IndexOf('-');
        return dash < 0 ? "" : "-" + Sanitize(label[(dash + 1)..].Split('.')[0]);
    }

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prism" + Channel);

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
