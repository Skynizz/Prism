using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Prism.Core;

/// <summary>Une langue proposee : code, nom dans la langue elle-meme, sens d'ecriture.</summary>
public sealed record Language(string Code, string NativeName, string EnglishName, bool RightToLeft = false);

/// <summary>
/// Traductions de l'interface.
///
/// Les chaines vivent dans <c>Lang/*.json</c>, embarques dans l'executable. Changer de
/// langue publie <c>Item[]</c> : toutes les liaisons <c>{l:T cle}</c> se mettent a
/// jour sans relancer l'application. Une cle absente d'une langue retombe sur
/// l'anglais, puis sur la cle elle-meme — jamais sur une chaine vide.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    /// <summary>Langues proposees, dans l'ordre d'affichage.</summary>
    public static IReadOnlyList<Language> Languages { get; } = new[]
    {
        new Language("en", "English", "English"),
        new Language("fr", "Français", "French"),
        new Language("de", "Deutsch", "German"),
        new Language("es", "Español", "Spanish"),
        new Language("it", "Italiano", "Italian"),
        new Language("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
        new Language("ru", "Русский", "Russian"),
        new Language("uk", "Українська", "Ukrainian"),
        new Language("pl", "Polski", "Polish"),
        new Language("tr", "Türkçe", "Turkish"),
        new Language("ar", "العربية", "Arabic", RightToLeft: true),
        new Language("hi", "हिन्दी", "Hindi"),
        new Language("ja", "日本語", "Japanese"),
        new Language("ko", "한국어", "Korean"),
        new Language("zh-Hans", "简体中文", "Chinese (Simplified)"),
        new Language("zh-Hant", "繁體中文", "Chinese (Traditional)"),
        new Language("id", "Bahasa Indonesia", "Indonesian"),
        new Language("vi", "Tiếng Việt", "Vietnamese")
    };

    private Dictionary<string, string> _fallback = new();
    private Dictionary<string, string> _current = new();

    private Loc()
    {
        _fallback = Load("en");
        _current = _fallback;
        Code = "en";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Publie apres chaque changement de langue.</summary>
    public event Action? LanguageChanged;

    public string Code { get; private set; }

    public Language Current => Languages.FirstOrDefault(l => l.Code == Code) ?? Languages[0];

    public FlowDirection FlowDirection => Current.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public CultureInfo Culture
    {
        get
        {
            try { return CultureInfo.GetCultureInfo(Code); }
            catch { return CultureInfo.InvariantCulture; }
        }
    }

    public string this[string key] =>
        _current.TryGetValue(key, out var value) ? value
        : _fallback.TryGetValue(key, out var fallback) ? fallback
        : key;

    public static string T(string key) => I[key];

    public static string T(string key, params object?[] args)
    {
        try { return string.Format(I.Culture, I[key], args); }
        catch (FormatException) { return I[key]; }
    }

    /// <summary>Toutes les cles connues de la langue de reference.</summary>
    public IReadOnlyCollection<string> Keys => _fallback.Keys;

    /// <summary>Cles traduites dans une langue donnee, pour les controles d'exhaustivite.</summary>
    public static IReadOnlyCollection<string> KeysOf(string code) => Load(code).Keys;

    public void Set(string code)
    {
        if (Languages.All(l => l.Code != code)) code = "en";

        _current = code == "en" ? _fallback : Load(code);
        Code = code;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Code)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
        LanguageChanged?.Invoke();
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>
    /// Langue d'affichage de Windows si elle est proposee, l'anglais sinon. La langue
    /// d'affichage du compte fait foi ; la culture du processus ne sert que de repli.
    /// </summary>
    public static string DetectDefault()
    {
        CultureInfo ui;
        try { ui = CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()); }
        catch { ui = CultureInfo.CurrentUICulture; }
        if (string.IsNullOrEmpty(ui.Name)) ui = CultureInfo.CurrentUICulture;

        var name = ui.Name;

        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith("TW", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith("HK", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith("MO", StringComparison.OrdinalIgnoreCase)
                ? "zh-Hant"
                : "zh-Hans";

        if (name.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR";

        var two = ui.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == two) ? two : "en";
    }

    private static Dictionary<string, string> Load(string code)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Lang.{code}.json");
            if (stream is null) return new Dictionary<string, string>();

            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }
}

/// <summary>
/// <c>{l:T cle}</c> dans le XAML : une liaison vers la traduction, qui suit les
/// changements de langue en direct.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.I, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>
/// Traduit une valeur stable (nom de groupe, valeur d'enumeration) en la prefixant.
/// Multi-liaison avec <c>Loc.I.Code</c> en second, pour se reevaluer au changement
/// de langue : <c>ConverterParameter</c> porte le prefixe de cle.
/// </summary>
public sealed class LocKey : IMultiValueConverter, IValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => Translate(values.Length > 0 ? values[0] : null, parameter);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Translate(value, parameter);

    private static string Translate(object? value, object? parameter)
    {
        var raw = value?.ToString() ?? "";
        var key = $"{parameter}{raw}".ToLowerInvariant();
        var text = Loc.T(key);
        return text == key ? raw : text;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
