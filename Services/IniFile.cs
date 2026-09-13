namespace Prism.Services;

/// <summary>
/// Edition ligne a ligne d'un fichier INI, sans rien reformater : commentaires,
/// ordre et lignes vides de l'utilisateur restent en place.
/// </summary>
public static class IniFile
{
    public static string? Read(IReadOnlyList<string> lines, string section, string key)
    {
        var (start, end) = Bounds(lines, section);
        if (start < 0) return null;

        for (var i = start + 1; i < end; i++)
            if (KeyOf(lines[i]) is { } k && k.Equals(key, StringComparison.OrdinalIgnoreCase))
                return ValueOf(lines[i]);

        return null;
    }

    public static void Set(List<string> lines, string section, string key, string value)
    {
        var (start, end) = Bounds(lines, section);

        if (start < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return;
        }

        for (var i = start + 1; i < end; i++)
        {
            if (KeyOf(lines[i]) is { } k && k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return;
            }
        }

        // Au-dessus des lignes vides qui ferment la section.
        var insert = end;
        while (insert > start + 1 && string.IsNullOrWhiteSpace(lines[insert - 1])) insert--;
        lines.Insert(insert, $"{key}={value}");
    }

    /// <summary>Retire une cle ; la section disparait si elle ne contient plus rien.</summary>
    public static bool Remove(List<string> lines, string section, string key)
    {
        var (start, end) = Bounds(lines, section);
        if (start < 0) return false;

        var removed = false;
        for (var i = end - 1; i > start; i--)
        {
            if (KeyOf(lines[i]) is { } k && k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(i);
                removed = true;
            }
        }

        (start, end) = Bounds(lines, section);
        if (start >= 0 && Enumerable.Range(start + 1, end - start - 1).All(i => string.IsNullOrWhiteSpace(lines[i])))
        {
            lines.RemoveRange(start, end - start);
            while (start > 0 && start <= lines.Count && string.IsNullOrWhiteSpace(lines[start - 1])
                   && (start == lines.Count || string.IsNullOrWhiteSpace(lines[start - 1])))
            {
                if (start - 1 < lines.Count && start - 2 >= 0 && string.IsNullOrWhiteSpace(lines[start - 2]))
                    lines.RemoveAt(start - 1);
                else break;
                start--;
            }
        }

        return removed;
    }

    private static (int Start, int End) Bounds(IReadOnlyList<string> lines, string section)
    {
        var header = $"[{section}]";
        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
            {
                start = i;
                break;
            }
        }
        if (start < 0) return (-1, -1);

        var end = lines.Count;
        for (var i = start + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('['))
            {
                end = i;
                break;
            }
        }
        return (start, end);
    }

    private static string? KeyOf(string line)
    {
        var t = line.Trim();
        if (t.Length == 0 || t[0] is ';' or '#') return null;
        var eq = t.IndexOf('=');
        return eq > 0 ? t[..eq].Trim() : null;
    }

    private static string ValueOf(string line)
    {
        var t = line.Trim();
        return t[(t.IndexOf('=') + 1)..].Trim();
    }
}
