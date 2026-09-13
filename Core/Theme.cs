using System.Windows;

namespace Prism.Core;

/// <summary>
/// Themes de l'interface.
///
/// Couleurs, typographie, rayons et hauteurs sont tous lus en DynamicResource : changer le
/// dictionnaire de jetons suffit a les mettre a jour. Le theme Studio ajoute une couche de
/// styles (surfaces, gabarits animes) ; un style etant resolu a la creation de l'element,
/// la fenetre est reconstruite apres un changement de theme.
/// </summary>
public static class Theme
{
    public const string Classic = "classic";
    public const string Studio = "studio";

    public static IReadOnlyList<string> All { get; } = new[] { Classic, Studio };

    public static string Current { get; private set; } = Classic;

    public static bool IsStudio => Current == Studio;

    public static string Normalize(string? code) => code == Studio ? Studio : Classic;

    public static void Apply(string? code)
    {
        code = Normalize(code);
        var dicts = Application.Current.Resources.MergedDictionaries;

        // Les jetons restent en tete : tous les styles les lisent.
        var fresh = Load(code == Studio ? "Tokens.Studio.xaml" : "Tokens.xaml");
        var tokens = dicts.FirstOrDefault(IsTokens);
        if (tokens is null) dicts.Insert(0, fresh);
        else dicts[dicts.IndexOf(tokens)] = fresh;

        // La couche Studio passe en dernier, pour l'emporter sur les styles de base.
        foreach (var overlay in dicts.Where(d => Named(d, "Studio.xaml")).ToList()) dicts.Remove(overlay);
        if (code == Studio) dicts.Add(Load("Studio.xaml"));

        Current = code;
    }

    private static ResourceDictionary Load(string file) =>
        new() { Source = new Uri($"pack://application:,,,/Prism;component/Themes/{file}", UriKind.Absolute) };

    private static bool Named(ResourceDictionary d, string file) =>
        d.Source is not null && d.Source.OriginalString.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase);

    private static bool IsTokens(ResourceDictionary d) => Named(d, "Tokens.xaml") || Named(d, "Tokens.Studio.xaml");
}
