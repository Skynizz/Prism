using System.Reflection;
using System.Text.Json;
using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>Un fichier (ou dossier) qui designe un dossier de mod complet : init.lua d'un mod CET, manifest.json SMAPI...</summary>
public sealed class ModMarker
{
    /// <summary>Nom exact du fichier.</summary>
    public string? File { get; set; }
    /// <summary>Extension du fichier (Kenshi : .mod).</summary>
    public string? Ext { get; set; }
    /// <summary>Nom d'un sous-dossier (Witcher 3 : content).</summary>
    public string? Dir { get; set; }
    /// <summary>Le fichier doit etre dans un dossier de ce nom (RimWorld : About\About.xml).</summary>
    public string? ParentName { get; set; }
    /// <summary>Niveaux a remonter depuis le dossier du fichier pour trouver le dossier du mod.</summary>
    public int Up { get; set; }
    /// <summary>Le dossier du mod doit aussi contenir l'un de ces elements (REDmod : archives, scripts...).</summary>
    public List<string>? WithSibling { get; set; }
    public string Dest { get; set; } = "";
    /// <summary>Cle de traduction affichee apres l'installation.</summary>
    public string? Note { get; set; }
}

public sealed class ExtensionRule
{
    public List<string> Ext { get; set; } = new();
    public string Dest { get; set; } = "";
}

/// <summary>Dossier qui accueille un dossier par mod, et le fichier qui prouve qu'un mod s'y trouve.</summary>
public sealed class ModContainer
{
    public string Path { get; set; } = "";
    /// <summary>Extensions (« .dll ») ou noms exacts (« init.lua »).</summary>
    public List<string> Keys { get; set; } = new();

    public bool IsKey(string fileName)
        => Keys.Any(k => k.StartsWith('.') ? fileName.EndsWith(k, StringComparison.OrdinalIgnoreCase)
                                          : fileName.Equals(k, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Chargeur de mods du jeu (CET, RED4ext, REDmod...) : ce qui le prouve et ce qui l'exige.</summary>
public sealed class FrameworkRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Mod Nexus qui le fournit.</summary>
    public long? Nexus { get; set; }
    /// <summary>DLC Steam qui le fournit (REDmod : 2060310).</summary>
    public long? SteamApp { get; set; }
    /// <summary>Fichiers relatifs au jeu qui prouvent qu'il est installe.</summary>
    public List<string> Files { get; set; } = new();
    /// <summary>Il devient indispensable des qu'un fichier de ce nom, ou de cette extension, existe dans ce dossier.</summary>
    public string? NeededDir { get; set; }
    public string? NeededFile { get; set; }
    public List<string>? NeededExt { get; set; }
    /// <summary>Utile sans etre exige (REDmod : case Mods du launcher).</summary>
    public bool Optional { get; set; }
    public string? Note { get; set; }
}

/// <summary>Journal d'un chargeur, relu apres un lancement.</summary>
public sealed class LogRule
{
    public string Dir { get; set; } = "";
    public string Pattern { get; set; } = "*.log";
    public string Error { get; set; } = "";
}

public sealed class GameModRule
{
    public List<FrameworkRule> Frameworks { get; set; } = new();
    public List<LogRule> Logs { get; set; } = new();
    /// <summary>Executable qui identifie le jeu sans passer par Nexus (restes de mods, noms reserves).</summary>
    public string? Exe { get; set; }
    public List<ModContainer> ModContainers { get; set; } = new();
    public string? ModRoot { get; set; }
    public List<string> StopFolders { get; set; } = new();
    public List<ModMarker> Markers { get; set; } = new();
    public List<ExtensionRule> Extensions { get; set; } = new();
    /// <summary>Jeu dont les mods passent par un gestionnaire propre : cle du message.</summary>
    public string? Unsupported { get; set; }
}

/// <summary>Noms de DLL proxy qu'un chargeur de mods du jeu occupe : OptiScaler et ReShade doivent les laisser libres.</summary>
public sealed class ProxyReservation
{
    public string Exe { get; set; } = "";
    public List<string> Names { get; set; } = new();
    public string? Why { get; set; }
}

public sealed class BethesdaRule
{
    public List<string> DataFolders { get; set; } = new();
    public List<string> PluginExtensions { get; set; } = new();
    public List<string> DataStopFolders { get; set; } = new();
}

/// <summary>
/// Regles de placement des mods (ModRules/rules.json), embarquees puis relues depuis GitHub
/// si la version y est plus recente : un jeu s'ajoute sans nouvelle version de Prism.
/// </summary>
public static class ModRules
{
    private const string Src = "modrules";
    private const string ResourceName = "ModRules.rules.json";
    public const string RemoteUrl = "https://raw.githubusercontent.com/Skynizz/Prism/main/ModRules/rules.json";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static int _version;
    public static Dictionary<string, GameModRule> Games { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public static BethesdaRule Bethesda { get; private set; } = new();
    public static List<ProxyReservation> Reservations { get; private set; } = new();

    /// <summary>
    /// Noms de proxy reserves dans ce dossier : ceux des chargeurs de mods du jeu dont
    /// l'executable s'y trouve (Cyberpunk : winmm.dll pour RED4ext, version.dll pour CET).
    /// </summary>
    public static HashSet<string> ReservedProxies(string dir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Reservations)
            if (File.Exists(Path.Combine(dir, r.Exe)))
                foreach (var n in r.Names) set.Add(n);
        return set;
    }

    static ModRules()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is not null) Load(new StreamReader(stream).ReadToEnd());
        }
        catch (Exception ex) { Log.Warn(Src, $"Embedded rules unreadable: {ex.Message}"); }
    }

    /// <summary>Regle du jeu reconnue a son executable.</summary>
    public static GameModRule? ForGame(GameInfo game)
    {
        var exe = System.IO.Path.GetFileName(game.Executable ?? "");
        return exe.Length == 0 ? null
            : Games.Values.FirstOrDefault(r => r.Exe is not null && r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
    }

    public static GameModRule? For(string? domain)
        => domain is not null && Games.TryGetValue(domain, out var r) ? r : null;

    /// <summary>Relit les regles publiees ; garde les embarquees si GitHub est injoignable ou plus ancien.</summary>
    public static async Task RefreshAsync(DownloadService downloads, CancellationToken ct = default)
    {
        try
        {
            var json = await downloads.GetStringAsync(RemoteUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetInt32() : 0;
            if (version > _version) Load(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info(Src, $"Remote rules unavailable: {ex.Message}");
        }
    }

    private static void Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var games = new Dictionary<string, GameModRule>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("games", out var g))
            foreach (var p in g.EnumerateObject())
                games[p.Name] = p.Value.Deserialize<GameModRule>(Options) ?? new GameModRule();

        Games = games;
        Reservations = root.TryGetProperty("proxyReservations", out var pr)
            ? pr.Deserialize<List<ProxyReservation>>(Options) ?? new List<ProxyReservation>()
            : new List<ProxyReservation>();
        if (root.TryGetProperty("bethesda", out var b))
            Bethesda = b.Deserialize<BethesdaRule>(Options) ?? new BethesdaRule();
        _version = root.TryGetProperty("version", out var v) ? v.GetInt32() : 0;
        Log.Info(Src, $"Mod rules v{_version}: {Games.Count} game(s)");
    }
}
