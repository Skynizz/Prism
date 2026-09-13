using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Prism.Models;
using Prism.Services;

namespace Prism.Core;

/// <summary>true devient Visible, false Collapsed. Le parametre "invert" inverse la regle.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object? parameter, CultureInfo c)
    {
        var b = value is bool v && v;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Affiche l'element seulement si la valeur est renseignee.</summary>
public sealed class HasValueToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object? parameter, CultureInfo c)
    {
        var has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i > 0,
            System.Collections.ICollection col => col.Count > 0,
            _ => true
        };
        if (parameter as string == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class InverseBool : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is bool b && !b;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is bool b && !b;
}

public sealed class ByteSize : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is long bytes ? SystemInfo.FormatBytes(bytes) : "";

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Compare la valeur liee au parametre : sert aux boutons segmentes.</summary>
public sealed class EqualityToBool : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => value is bool b && b && p is not null ? p : Binding.DoNothing;
}

/// <summary>
/// Compare deux valeurs liees. Necessaire la ou la valeur de reference est elle-meme
/// une liaison : ConverterParameter n'accepte pas de binding.
/// </summary>
public sealed class MultiEquality : IMultiValueConverter
{
    public object Convert(object?[] values, Type t, object? p, CultureInfo c)
        => values.Length >= 2
           && string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object[] ConvertBack(object? value, Type[] t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public sealed class EqualityToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>
/// Resout une cle de pinceau depuis les ressources de l'application, pour que les
/// convertisseurs restent alignes sur les jetons de design.
/// </summary>
internal static class BrushLookup
{
    public static Brush Get(string key)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        return Brushes.Gray;
    }
}

/// <summary>Couleur du point d'etat. Volontairement discrete : le point porte le sens.</summary>
public sealed class StatusToBrush : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        // Un booleen se lit comme un etat binaire : en place, ou pas encore.
        var status = value switch
        {
            UiStatus s => s,
            bool b => b ? UiStatus.Ready : UiStatus.Idle,
            _ => UiStatus.Idle
        };

        return BrushLookup.Get(status switch
        {
            UiStatus.Active or UiStatus.Injected or UiStatus.Ready => "BrOk",
            UiStatus.Detected => "BrInfo",
            UiStatus.Warning => "BrWarn",
            UiStatus.Error => "BrErr",
            _ => "BrIdle"
        });
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Infobulle d'un multiplicateur : courte, technique, sans emphase.</summary>
public sealed class MultiplierTip : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not int m || m < 2) return Loc.T("tip.mult.off");
        return Loc.T("tip.mult.on", m - 1, m);
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class LevelToBrush : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var level = value as LogLevel? ?? LogLevel.Info;
        return BrushLookup.Get(level switch
        {
            LogLevel.Error => "BrErr",
            LogLevel.Warn => "BrWarn",
            LogLevel.Trace => "BrFg3",
            _ => "BrInfo"
        });
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
