using System.Text;

namespace Prism.Services;

/// <summary>
/// Noeud d'un fichier KeyValues Valve (libraryfolders.vdf, appmanifest_*.acf).
/// Les cles sont insensibles a la casse, comme chez Valve.
/// </summary>
public sealed class VdfNode
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
    public VdfNode? Child(string key) => Children.TryGetValue(key, out var c) ? c : null;
}

/// <summary>Parseur minimal du format KeyValues texte de Valve.</summary>
public static class VdfParser
{
    public static VdfNode? ParseFile(string path)
    {
        try { return Parse(File.ReadAllText(path, Encoding.UTF8)); }
        catch { return null; }
    }

    public static VdfNode Parse(string text)
    {
        var i = 0;
        var root = new VdfNode();
        ParseBody(text, ref i, root);
        return root;
    }

    private static void ParseBody(string s, ref int i, VdfNode node)
    {
        while (i < s.Length)
        {
            SkipTrivia(s, ref i);
            if (i >= s.Length) return;

            if (s[i] == '}') { i++; return; }

            var key = ReadToken(s, ref i);
            if (key is null) return;

            SkipTrivia(s, ref i);
            if (i >= s.Length) return;

            if (s[i] == '{')
            {
                i++;
                var child = new VdfNode();
                ParseBody(s, ref i, child);
                node.Children[key] = child;
            }
            else
            {
                var value = ReadToken(s, ref i);
                if (value is not null) node.Values[key] = value;
            }
        }
    }

    /// <summary>Avance au-dela des blancs et des commentaires de ligne.</summary>
    private static void SkipTrivia(string s, ref int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }

            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }

            return;
        }
    }

    private static string? ReadToken(string s, ref int i)
    {
        SkipTrivia(s, ref i);
        if (i >= s.Length) return null;

        if (s[i] == '"')
        {
            i++;
            var sb = new StringBuilder();

            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    sb.Append(Unescape(s[i]));
                }
                else
                {
                    sb.Append(s[i]);
                }
                i++;
            }

            i++; // guillemet fermant
            return sb.ToString();
        }

        var start = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}') i++;
        return i > start ? s[start..i] : null;
    }

    private static char Unescape(char c) => c switch
    {
        'n' => '\n',
        't' => '\t',
        'r' => '\r',
        _ => c
    };
}
