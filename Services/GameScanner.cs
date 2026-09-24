using System.Text.Json;
using Microsoft.Win32;
using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>Decouvre les jeux installes sur toutes les plateformes connues.</summary>
public sealed class GameScanner
{
    /// <summary>Dossiers ajoutes manuellement par l'utilisateur, scannes a un niveau.</summary>
    public List<string> ExtraFolders { get; set; } = new();

    /// <summary>Jeux ajoutes par leur executable : ceux qu'aucun scanner ne trouve.</summary>
    public List<ManualGame> ManualGames { get; set; } = new();

    /// <summary>Executable choisi a la main, par identifiant de jeu : il prime sur la detection.</summary>
    public Dictionary<string, string> ExeOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task<List<GameInfo>> ScanAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var found = new List<GameInfo>();

        await Task.Run(() =>
        {
            Run(progress, "Steam", () => found.AddRange(ScanSteam()), ct);
            Run(progress, "Epic Games", () => found.AddRange(ScanEpic()), ct);
            Run(progress, "GOG", () => found.AddRange(ScanGog()), ct);
            Run(progress, "Xbox / Microsoft Store", () => found.AddRange(ScanXbox()), ct);
            Run(progress, "EA App", () => found.AddRange(ScanEa()), ct);
            Run(progress, "Ubisoft Connect", () => found.AddRange(ScanUbisoft()), ct);
            Run(progress, "Battle.net", () => found.AddRange(ScanBattleNet()), ct);
            Run(progress, Loc.T("scan.manual"), () => found.AddRange(ScanExtraFolders()), ct);
        }, ct);

        // Un meme jeu peut etre vu par deux scanners (Xbox + dossier manuel par exemple).
        var games = found
            .Where(g => Directory.Exists(g.InstallDir))
            .GroupBy(g => g.InstallDir.TrimEnd('\\', '/').ToLowerInvariant())
            .Select(grp => grp.First())
            .ToList();

        // Un jeu ajoute par son exe mais aussi vu par une plateforme garde l'identifiant de la
        // plateforme — son historique y est attache — et prend l'executable choisi.
        foreach (var m in ManualGames.Select(FromExecutable).OfType<GameInfo>())
        {
            var same = games.FirstOrDefault(g => SameDir(g.InstallDir, m.InstallDir));
            if (same is null) games.Add(m);
            else if (!ExeOverrides.ContainsKey(same.Id)) same.Executable = m.Executable;
        }
        games = games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        foreach (var g in games)
            if (ExeOverrides.TryGetValue(g.Id, out var exe) && File.Exists(exe))
                g.Executable = exe;

        return games;
    }

    /// <summary>
    /// Un jeu decrit par son executable. Pour un build Unreal (<c>&lt;Jeu&gt;\&lt;Projet&gt;\Binaries\Win64</c>),
    /// le dossier du jeu est la racine, pour que toute l'arborescence soit inspectee.
    /// </summary>
    public static GameInfo? FromExecutable(ManualGame manual)
    {
        var exe = manual.Executable;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;

        var dir = Path.GetDirectoryName(exe)!;
        const string marker = @"\Binaries\Win64";
        var at = dir.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var root = at > 0 ? Path.GetDirectoryName(dir[..at]) ?? dir : dir;

        return new GameInfo
        {
            Id = ManualId(exe),
            Name = string.IsNullOrWhiteSpace(manual.Name) ? Path.GetFileName(root) : manual.Name,
            InstallDir = root,
            Platform = GamePlatform.Manual,
            Executable = exe
        };
    }

    public static string ManualId(string exe) => "exe:" + exe.Trim().ToLowerInvariant();

    private static bool SameDir(string a, string b)
        => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Nom lisible d'un executable : le produit declare, sinon le dossier du jeu.</summary>
    public static string NameFor(string exe)
    {
        try
        {
            var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim();
            if (!string.IsNullOrWhiteSpace(product) && !product.Contains("Unreal", StringComparison.OrdinalIgnoreCase))
                return product;
        }
        catch { /* pas de ressource de version */ }
        return FromExecutable(new ManualGame { Executable = exe })?.Name ?? Path.GetFileNameWithoutExtension(exe);
    }

    private static void Run(IProgress<string>? progress, string label, Action action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(Loc.T("scan.progress", label));
        try { action(); }
        catch (Exception ex) { Log.Write($"Scanner {label} failed: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- Steam

    public static IEnumerable<GameInfo> ScanSteam()
    {
        var steamPath = RegString(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                        ?? RegString(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (steamPath is null || !Directory.Exists(steamPath)) yield break;

        var libraries = new List<string> { steamPath };

        // libraryfolders.vdf liste les disques secondaires.
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        var root = VdfParser.ParseFile(vdf);
        var folders = root?.Child("libraryfolders");
        if (folders is not null)
        {
            foreach (var entry in folders.Children.Values)
            {
                var p = entry.Get("path");
                if (!string.IsNullOrWhiteSpace(p)) libraries.Add(p!);
            }
        }

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;

            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var node = VdfParser.ParseFile(acf)?.Child("AppState");
                var appid = node?.Get("appid");
                var name = node?.Get("name");
                var dir = node?.Get("installdir");
                if (appid is null || name is null || dir is null) continue;

                // Les redistribuables Steam ne sont pas des jeux.
                if (appid is "228980" or "1070560" or "1391110") continue;

                var full = Path.Combine(apps, "common", dir);
                if (!Directory.Exists(full)) continue;

                yield return new GameInfo
                {
                    Id = $"steam:{appid}",
                    Name = name,
                    InstallDir = full,
                    Platform = GamePlatform.Steam,
                    SteamAppId = long.TryParse(appid, out var id) ? id : null,
                    IdentitySource = IdentitySource.Steam
                };
            }
        }
    }

    // ----------------------------------------------------------------- Epic

    public static IEnumerable<GameInfo> ScanEpic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;

        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            GameInfo? game = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var name = Str(r, "DisplayName");
                var loc = Str(r, "InstallLocation");
                var key = Str(r, "AppName") ?? Str(r, "InstallationGuid");
                if (name is null || loc is null) continue;

                game = new GameInfo
                {
                    Id = $"epic:{key ?? name}",
                    Name = name,
                    InstallDir = loc,
                    Platform = GamePlatform.Epic,
                    Executable = Str(r, "LaunchExecutable") is { } exe ? Path.Combine(loc, exe) : null
                };
            }
            catch (Exception ex) { Log.Write($"Epic manifest unreadable ({Path.GetFileName(file)}): {ex.Message}"); }

            if (game is not null) yield return game;
        }
    }

    // ------------------------------------------------------------------ GOG

    public static IEnumerable<GameInfo> ScanGog()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var games = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (games is null) continue;

            foreach (var id in games.GetSubKeyNames())
            {
                using var k = games.OpenSubKey(id);
                var name = k?.GetValue("gameName") as string;
                var path = k?.GetValue("path") as string;
                if (name is null || path is null) continue;

                yield return new GameInfo
                {
                    Id = $"gog:{id}",
                    Name = name,
                    InstallDir = path,
                    Platform = GamePlatform.Gog,
                    Executable = k?.GetValue("exe") as string
                };
            }
        }
    }

    // ----------------------------------------------------------------- Xbox

    public static IEnumerable<GameInfo> ScanXbox()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(root)) continue;

            foreach (var folder in SafeDirs(root))
            {
                // Le binaire vit sous <Jeu>\Content, le dossier parent porte le nom du jeu.
                var content = Path.Combine(folder, "Content");
                var install = Directory.Exists(content) ? content : folder;
                var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(name)) continue;

                yield return new GameInfo
                {
                    Id = $"xbox:{name}",
                    Name = name,
                    InstallDir = install,
                    Platform = GamePlatform.Xbox
                };
            }
        }
    }

    // --------------------------------------------------------------- EA App

    public static IEnumerable<GameInfo> ScanEa()
    {
        var roots = new List<string>();
        foreach (var pf in ProgramFilesRoots())
        {
            roots.Add(Path.Combine(pf, "EA Games"));
            roots.Add(Path.Combine(pf, "Origin Games"));
        }

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var folder in SafeDirs(root))
            {
                var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
                yield return new GameInfo
                {
                    Id = $"ea:{name}",
                    Name = name,
                    InstallDir = folder,
                    Platform = GamePlatform.EaApp
                };
            }
        }
    }

    // ------------------------------------------------------ Ubisoft Connect

    public static IEnumerable<GameInfo> ScanUbisoft()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var installs = baseKey.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
            if (installs is null) continue;

            foreach (var id in installs.GetSubKeyNames())
            {
                using var k = installs.OpenSubKey(id);
                var dir = k?.GetValue("InstallDir") as string;
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

                // Ubisoft ne stocke pas le titre : le nom du dossier est ce qu'on a de mieux.
                yield return new GameInfo
                {
                    Id = $"ubisoft:{id}",
                    Name = Path.GetFileName(dir!.TrimEnd('\\', '/')),
                    InstallDir = dir!,
                    Platform = GamePlatform.Ubisoft
                };
            }
        }
    }

    // ----------------------------------------------------------- Battle.net

    public static IEnumerable<GameInfo> ScanBattleNet()
    {
        foreach (var pf in ProgramFilesRoots())
        {
            foreach (var candidate in new[] { Path.Combine(pf, "Battle.net Games"), Path.Combine(pf, "Battle.net") })
            {
                if (!Directory.Exists(candidate)) continue;
                foreach (var folder in SafeDirs(candidate))
                {
                    var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
                    if (name.Equals("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;
                    yield return new GameInfo
                    {
                        Id = $"bnet:{name}",
                        Name = name,
                        InstallDir = folder,
                        Platform = GamePlatform.BattleNet
                    };
                }
            }
        }
    }

    // ----------------------------------------------------- Dossiers manuels

    private IEnumerable<GameInfo> ScanExtraFolders()
    {
        foreach (var root in ExtraFolders.Where(Directory.Exists))
        {
            // Le dossier choisi peut etre le jeu lui-meme, pas une bibliotheque : sans ce test,
            // ses sous-dossiers (« Engine », « Binaries »...) passaient pour autant de jeux.
            if (IsGameFolder(root))
            {
                yield return new GameInfo
                {
                    Id = $"manual:{Path.GetFileName(root.TrimEnd('\\', '/'))}",
                    Name = Path.GetFileName(root.TrimEnd('\\', '/')),
                    InstallDir = root,
                    Platform = GamePlatform.Manual
                };
                continue;
            }

            foreach (var folder in SafeDirs(root))
            {
                var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
                yield return new GameInfo
                {
                    Id = $"manual:{name}",
                    Name = name,
                    InstallDir = folder,
                    Platform = GamePlatform.Manual
                };
            }
        }
    }

    /// <summary>
    /// Un dossier de jeu, pas une bibliotheque : un executable a sa racine, « Binaries\Win64 »
    /// directement dessous, ou la racine d'un build Unreal — un dossier « Engine » a cote du projet
    /// (The Blood of Dawnwalker : Engine\ et Dawnwalker\Binaries\Win64\, aucun exe a la racine).
    /// Une bibliotheque n'a jamais de dossier « Engine » a elle.
    /// </summary>
    public static bool IsGameFolder(string dir)
    {
        try
        {
            if (Directory.EnumerateFiles(dir, "*.exe").Any()) return true;
            if (Directory.Exists(Path.Combine(dir, "Binaries", "Win64"))) return true;
            return Directory.Exists(Path.Combine(dir, "Engine"))
                   && SafeDirs(dir).Any(d => Directory.Exists(Path.Combine(d, "Binaries", "Win64"))
                                             || Directory.Exists(Path.Combine(d, "x64")));
        }
        catch { return false; }
    }

    // ----------------------------------------------------------- Utilitaires

    private static IEnumerable<string> ProgramFilesRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    }

    /// <summary>Enumere les sous-dossiers sans exploser sur un dossier protege.</summary>
    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static string? Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? RegString(RegistryHive hive, string subkey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var k = baseKey.OpenSubKey(subkey);
            return k?.GetValue(name) as string;
        }
        catch { return null; }
    }
}
