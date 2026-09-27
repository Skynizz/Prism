using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Version du moteur, pour ne proposer que ce qui peut marcher : les mods RenoDX generiques
/// visent Unreal 4 et 5 (« any UE4-5 game », wiki RenoDX) et deconseillent Engine.ini sur UE4.
///
/// Indices, du plus sur au moins sur, tous constates sur de vrais jeux :
///  - Unreal : la chaine « ++UE4+Release-4.26 » (UTF-16) du binaire — Stellar Blade la porte ;
///    sinon la version de fichier « UE5-CL-0 » (Silent Hill Townfall) ; sinon les archives :
///    des .upk sans .pak/.utoc = Unreal 3 ;
///  - Unity : la version de UnityPlayer.dll.
/// Un binaire Unreal pese souvent 150 a 350 Mo : la lecture est faite une fois, puis gardee en
/// cache (chemin, taille, date).
/// </summary>
public static partial class EngineProbe
{
    private const string Src = "engine";
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    private static string CacheFile => Path.Combine(AppPaths.Cache, "engine-versions.json");

    /// <summary>Complete Engine/EngineGeneration/EngineVersion a partir de ce que l'inspection a vu.</summary>
    public static void Probe(GameInfo game, bool sawUpk, bool sawPak, string? unityPlayer)
    {
        game.EngineGeneration = null;
        game.EngineVersion = null;

        if (unityPlayer is not null)
        {
            game.Engine ??= "Unity";
            if (game.Engine == "Unity" && UnityVersion(unityPlayer) is { } uv) game.EngineVersion = uv;
            return;
        }

        // Un jeu Unreal 3 est souvent 32 bits (Binaries\Win32) : ses .upk suffisent a le reconnaitre.
        if (game.Engine is null && sawUpk && !sawPak) game.Engine = "Unreal";
        if (!string.Equals(game.Engine, "Unreal", StringComparison.OrdinalIgnoreCase)) return;

        if (sawUpk && !sawPak) { game.EngineGeneration = 3; return; }

        if (game.Executable is { } exe && UnrealMarker(exe) is { } version)
        {
            game.EngineVersion = version;
            game.EngineGeneration = int.TryParse(version.Split('.')[0], out var g) ? g : null;
            return;
        }

        try
        {
            var fv = game.Executable is { } e ? FileVersionInfo.GetVersionInfo(e).FileVersion ?? "" : "";
            if (fv.Contains("UE5", StringComparison.OrdinalIgnoreCase)) game.EngineGeneration = 5;
            else if (fv.Contains("UE4", StringComparison.OrdinalIgnoreCase)) game.EngineGeneration = 4;
        }
        catch { /* pas de ressource de version */ }
    }

    private static string? UnityVersion(string dll)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(dll);
            var text = !string.IsNullOrWhiteSpace(v.ProductVersion) ? v.ProductVersion! : v.FileVersion ?? "";
            var m = UnityVersionRegex().Match(text);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    /// <summary>« 4.26 » depuis « ++UE4+Release-4.26 », ASCII ou UTF-16. Resultat garde en cache.</summary>
    public static string? UnrealMarker(string exe)
    {
        FileInfo info;
        try { info = new FileInfo(exe); if (!info.Exists) return null; }
        catch { return null; }

        LoadCache();
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        if (Cache.TryGetValue(key, out var known)) return known.Length == 0 ? null : known;

        var found = Scan(info.FullName);
        Cache[key] = found ?? "";
        SaveCache();
        Log.Info(Src, $"{info.Name}: Unreal marker {(found ?? "absent")}");
        return found;
    }

    private static readonly byte[] Ascii = Encoding.ASCII.GetBytes("++UE");
    private static readonly byte[] Wide = Encoding.Unicode.GetBytes("++UE");

    private static string? Scan(string path)
    {
        const int Chunk = 8 << 20, Overlap = 256;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            var buffer = new byte[Chunk + Overlap];
            var carried = 0;
            int read;
            while ((read = fs.Read(buffer, carried, Chunk)) > 0)
            {
                var span = buffer.AsSpan(0, carried + read);
                if ((Find(span, Wide, wide: true) ?? Find(span, Ascii, wide: false)) is { } hit) return hit;
                carried = Math.Min(Overlap, span.Length);
                span[^carried..].CopyTo(buffer);
            }
        }
        catch (Exception ex) { Log.Warn(Src, $"Cannot scan {path}: {ex.Message}"); }
        return null;
    }

    private static string? Find(ReadOnlySpan<byte> data, byte[] needle, bool wide)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var i = data[offset..].IndexOf(needle);
            if (i < 0) return null;
            var start = offset + i;
            var len = Math.Min(80, data.Length - start);
            var text = wide
                ? Encoding.Unicode.GetString(data.Slice(start, len & ~1))
                : Encoding.ASCII.GetString(data.Slice(start, len));
            var m = MarkerRegex().Match(text);
            if (m.Success && m.Index == 0) return m.Groups[1].Value;
            offset = start + needle.Length;
        }
        return null;
    }

    private static void LoadCache()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(CacheFile))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CacheFile)) ?? new())
                    Cache[k] = v;
        }
        catch { /* cache facultatif */ }
    }

    private static void SaveCache()
    {
        try { File.WriteAllText(CacheFile, JsonSerializer.Serialize(Cache.ToDictionary(p => p.Key, p => p.Value))); }
        catch { /* cache facultatif */ }
    }

    [GeneratedRegex(@"^\+\+UE[345]\+Release-(\d+\.\d+)")]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"^(\d{1,4}\.\d+)")]
    private static partial Regex UnityVersionRegex();
}
