using System.Reflection;
using System.Text.Json;
using Prism.Core;

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

public sealed class GameModRule
{
    public string? ModRoot { get; set; }
    public List<string> StopFolders { get; set; } = new();
    public List<ModMarker> Markers { get; set; } = new();
    public List<ExtensionRule> Extensions { get; set; } = new();
    /// <summary>Jeu dont les mods passent par un gestionnaire propre : cle du message.</summary>
    public string? Unsupported { get; set; }
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

    static ModRules()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is not null) Load(new StreamReader(stream).ReadToEnd());
        }
        catch (Exception ex) { Log.Warn(Src, $"Embedded rules unreadable: {ex.Message}"); }
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
        if (root.TryGetProperty("bethesda", out var b))
            Bethesda = b.Deserialize<BethesdaRule>(Options) ?? new BethesdaRule();
        _version = root.TryGetProperty("version", out var v) ? v.GetInt32() : 0;
        Log.Info(Src, $"Mod rules v{_version}: {Games.Count} game(s)");
    }
}
