using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>Un binaire ReShade trouve a cote de l'executable.</summary>
public sealed record ReShadeLoader(string Path, string Name, Version? Version, bool IsAddon, bool Active);

/// <summary>ReShade tel qu'il est reellement dans un jeu.</summary>
public sealed class ReShadeState
{
    public required IReadOnlyList<ReShadeLoader> Loaders { get; init; }

    /// <summary>Les binaires que le jeu charge vraiment.</summary>
    public IReadOnlyList<ReShadeLoader> Active => Loaders.Where(l => l.Active).ToList();

    public ReShadeLoader? Primary => Active.FirstOrDefault();

    public bool Present => Active.Count > 0;

    /// <summary>Deux ReShade charges ensemble : le jeu plante ou double ses effets.</summary>
    public bool IsDuplicate => Active.Count > 1;

    public bool IsOld => Primary?.Version is { } v && v < ReShadeLocator.Minimum;

    /// <summary>Un seul ReShade, version add-on, 6.8 ou plus : rien a faire.</summary>
    public bool Ready => Active.Count == 1 && Primary!.IsAddon && !IsOld && Primary.Version is not null;

    public string VersionLabel => Primary?.Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    /// <summary>« 6.8.0 · add-on · dxgi.dll », en mots-cles.</summary>
    public string Label
    {
        get
        {
            if (!Present) return Loc.T("common.absent");
            if (IsDuplicate) return Loc.T("reshade.state.duplicate", string.Join(" + ", Active.Select(l => l.Name)));
            var build = Primary!.IsAddon ? "add-on" : Loc.T("reshade.state.standard");
            var label = $"{VersionLabel} · {build} · {Primary.Name}";
            return IsOld ? label + " · " + Loc.T("reshade.state.old") : label;
        }
    }
}

/// <summary>
/// Trouve ReShade dans un jeu, quel que soit le nom sous lequel il se fait charger.
///
/// Les noms viennent de la documentation de ReShade et de RHI : d3d9, d3d10, d3d11, d3d12,
/// dxgi et opengl32, plus les proxys generiques qu'utilisent les chargeurs ASI, et
/// ReShade64.dll quand OptiScaler le charge lui-meme (<c>[Plugins] LoadReshade</c>).
///
/// La version add-on se distingue de la standard sans supposition : la standard contient
/// le message « only limited add-on functionality », que la version add-on n'a pas.
/// </summary>
public static class ReShadeLocator
{
    /// <summary>Version minimale exigee par RenoDX (page Mods du wiki).</summary>
    public static readonly Version Minimum = new(6, 8, 0);

    public static readonly string[] ProxyNames =
    {
        "dxgi.dll", "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "opengl32.dll",
        "dinput8.dll", "winmm.dll", "version.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll",
        "xinput1_3.dll", "xinput1_4.dll"
    };

    public static readonly string[] StandaloneNames = { "ReShade64.dll", "ReShade32.dll" };

    private static readonly byte[] LimitedMarker = Encoding.ASCII.GetBytes("only limited add-on functionality");

    private static readonly ConcurrentDictionary<string, (long Size, DateTime At, bool Addon)> AddonCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Etat neutre, avant tout relevé : evite de balayer le disque depuis une liaison.</summary>
    public static ReShadeState Empty { get; } = new() { Loaders = Array.Empty<ReShadeLoader>() };

    public static ReShadeState Scan(GameInfo game) => Scan(DllInstaller.TargetDirectory(game));

    public static ReShadeState Scan(string dir)
    {
        var loaders = new List<ReShadeLoader>();
        if (!Directory.Exists(dir)) return new ReShadeState { Loaders = loaders };

        var optiLoads = OptiScalerLoadsReShade(dir);

        IEnumerable<string> candidates = ProxyNames.Concat(StandaloneNames).Select(n => Path.Combine(dir, n));
        try { candidates = candidates.Concat(Directory.EnumerateFiles(dir, "*.asi")); }
        catch { /* dossier illisible : les noms fixes suffisent */ }

        foreach (var path in candidates)
        {
            if (!File.Exists(path) || !IsReShade(path)) continue;

            var name = Path.GetFileName(path);
            var standalone = StandaloneNames.Contains(name, StringComparer.OrdinalIgnoreCase);
            // ReShade64.dll seul n'est charge par personne, sauf si OptiScaler s'en charge.
            var active = !standalone || (optiLoads && name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase));

            loaders.Add(new ReShadeLoader(path, name, VersionOf(path), IsAddonBuild(path), active));
        }

        return new ReShadeState { Loaders = loaders };
    }

    public static bool IsReShade(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return $"{info.ProductName} {info.FileDescription}".Contains("ReShade", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static Version? VersionOf(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileMajorPart == 0 && info.FileMinorPart == 0
                ? null
                : new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        }
        catch { return null; }
    }

    /// <summary>Vrai pour la version add-on complete, seule a charger les .addon64.</summary>
    public static bool IsAddonBuild(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return false;
            if (AddonCache.TryGetValue(path, out var hit) && hit.Size == file.Length && hit.At == file.LastWriteTimeUtc)
                return hit.Addon;

            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
            }

            var addon = bytes.AsSpan().IndexOf(LimitedMarker) < 0;
            AddonCache[path] = (file.Length, file.LastWriteTimeUtc, addon);
            return addon;
        }
        catch { return false; }
    }

    /// <summary>OptiScaler present et reglage LoadReshade different de false.</summary>
    public static bool OptiScalerLoadsReShade(string dir)
    {
        var ini = Path.Combine(dir, "OptiScaler.ini");
        if (!File.Exists(ini)) return false;
        try
        {
            var value = IniFile.Read(File.ReadAllLines(ini), "Plugins", "LoadReshade");
            return !string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
