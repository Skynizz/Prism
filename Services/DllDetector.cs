using System.Diagnostics;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Inspecte l'arborescence d'un jeu pour y trouver les DLL remplacables et les
/// surcouches deja installees.
/// </summary>
public static class DllDetector
{
    /// <summary>
    /// Profondeur maximale de descente. Unreal range Streamline sous
    /// Engine\Plugins\Runtime\Nvidia\StreamlineCore\Binaries\ThirdParty\Win64, soit 8 niveaux :
    /// a 7, ce dossier n'etait jamais vu et la pile DLSS 5 partait a cote de l'executable.
    /// </summary>
    private const int MaxDepth = 10;

    private static readonly Dictionary<string, DllKind> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nvngx_dlss.dll"] = DllKind.Dlss,
        ["nvngx_dlssg.dll"] = DllKind.DlssG,
        ["nvngx_dlssd.dll"] = DllKind.DlssD,
        ["amd_fidelityfx_dx12.dll"] = DllKind.FsrDx12,
        ["amd_fidelityfx_vk.dll"] = DllKind.FsrVk,
        ["libxess.dll"] = DllKind.XeSS,
        ["libxess_fg.dll"] = DllKind.XeSSFg,
        ["libxell.dll"] = DllKind.XeLL
    };

    /// <summary>
    /// Fichiers temoins d'un moteur. On s'en tient a des noms exacts et sans ambiguite :
    /// une heuristique sur des extensions generiques produirait de faux positifs.
    /// </summary>
    private static readonly Dictionary<string, string> EngineMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UnityPlayer.dll"] = "Unity",
        ["REDprelauncher.exe"] = "REDengine",
        ["CrySystem.dll"] = "CryEngine",
        ["re_chunk_000.pak"] = "RE Engine",
        ["dunia.dll"] = "Dunia",
        ["GameAssembly.dll"] = "Unity"
    };

    /// <summary>Noms de proxy utilises par OptiScaler / RTX40MFG / DLSS Enabler.</summary>
    public static readonly string[] ProxyNames =
    {
        "dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll",
        "wininet.dll", "winhttp.dll", "OptiScaler.asi"
    };

    /// <summary>Fichiers dont la presence n'est pas un jeu : on ne descend pas dedans.</summary>
    private static readonly string[] SkipDirs =
    {
        "_CommonRedist", "DirectX", "DotNetFX", "vcredist", "Redist", "__Installer",
        "EasyAntiCheat", "BattlEye", "DXSETUP"
    };

    /// <param name="raise">
    /// Faux pour inspecter depuis un thread de fond : l'appelant declenchera
    /// <see cref="GameInfo.RaiseAll"/> sur le thread d'interface.
    /// </param>
    public static void Inspect(GameInfo game, bool raise = true)
    {
        game.Dlls.Clear();
        game.HasStreamline = false;
        game.HasReShade = false;
        game.HasRenoDx = false;
        game.HasOptiScaler = false;
        game.HasMfgUnlock = false;
        game.HasNeuralRuntime = false;
        game.HasDlss5Bridge = false;
        game.HasDlss5Addon = false;
        game.StreamlineVersion = null;
        game.StreamlineDirectories.Clear();
        game.Api = GameApi.Unknown;
        game.Engine = null;

        var sawD3D12 = false;
        var sawVulkan = false;

        var exeCandidates = new List<(string Path, long Size)>();

        foreach (var file in Walk(game.InstallDir, 0))
        {
            var name = Path.GetFileName(file);

            // Beaucoup de jeux Unreal renomment leur executable : l'arborescence, elle,
            // reste toujours <Projet>\Binaries\Win64.
            if (game.Engine is null && file.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase))
                game.Engine = "Unreal";

            if (Known.TryGetValue(name, out var kind))
            {
                game.Dlls.Add(new InstalledDll
                {
                    Path = file,
                    Kind = kind,
                    FileVersion = ReadVersion(file),
                    Size = SafeSize(file)
                });
                continue;
            }

            if (name.Equals("sl.interposer.dll", StringComparison.OrdinalIgnoreCase))
            {
                game.HasStreamline = true;
                game.StreamlineDirectories.Add(Path.GetDirectoryName(file)!);
                // MFGAdaUnlock exige un paquet Streamline exact : la version compte.
                game.StreamlineVersion = ReadProductVersion(file) ?? ReadVersion(file);
                sawD3D12 = true;
            }
            // D3D12Core.dll accompagne l'Agility SDK : signature fiable d'un rendu DirectX 12.
            else if (name.Equals("D3D12Core.dll", StringComparison.OrdinalIgnoreCase))
                sawD3D12 = true;
            else if (name.Equals("vulkan-1.dll", StringComparison.OrdinalIgnoreCase))
                sawVulkan = true;
            // nvngx_dlssnr.dll n'est pas dans le manifeste hash-verifie : on le
            // constate, on ne le distribue pas.
            else if (name.Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase))
                game.HasNeuralRuntime = true;
            else if (EngineMarkers.TryGetValue(name, out var engine))
                game.Engine = engine;
            else if (IsAddon(name))
            {
                // Les debrideurs MFG se distribuent aussi en addon ReShade. Ils portent le
                // prefixe renodx sans etre des mods HDR : ne pas les compter comme tels.
                var isMfgAddon = name.Contains("MFG", StringComparison.OrdinalIgnoreCase);
                if (name.StartsWith("dlss5-bridge", StringComparison.OrdinalIgnoreCase)) game.HasDlss5Bridge = true;
                else if (name.StartsWith("renodx-dlss5", StringComparison.OrdinalIgnoreCase)
                         || name.Equals(Dlss5Addon.ShortFuse.FileName, StringComparison.OrdinalIgnoreCase)) game.HasDlss5Addon = true;
                else if (isMfgAddon) game.HasMfgUnlock = true;
                else if (name.StartsWith("renodx", StringComparison.OrdinalIgnoreCase)) game.HasRenoDx = true;
            }
            else if (name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("OptiScaler.asi", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("dlss-enabler.dll", StringComparison.OrdinalIgnoreCase))
                game.HasOptiScaler = true;
            else if (name.StartsWith("RTXMFG", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("RTX40MFG", StringComparison.OrdinalIgnoreCase))
                game.HasMfgUnlock = true;
            else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IsLauncherLike(name))
            {
                // "<Jeu>-Win64-Shipping.exe" est la signature d'un build Unreal.
                if (name.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("-WinGDK-Shipping.exe", StringComparison.OrdinalIgnoreCase))
                    game.Engine = "Unreal";

                exeCandidates.Add((file, SafeSize(file)));
            }

            // Un proxy peut heberger ReShade ou OptiScaler : on regarde sa description.
            if (ProxyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                ClassifyProxy(file, game);
        }

        // L'API conditionne les voies proposees : le pont DLSS 5 ne sert qu'en
        // DirectX 11 et Vulkan, OptiScaler prend le relais en DirectX 12.
        game.Api =
            sawD3D12 || game.HasDlssG ? GameApi.DirectX12
            : sawVulkan ? GameApi.Vulkan
            : game.HasDlss ? GameApi.DirectX11
            : GameApi.Unknown;

        // Un build Unreal se reconnait a son « -Shipping.exe » : c'est lui qui charge les DLL, pas
        // le lanceur pose a la racine. Sinon, l'executable du jeu est de loin le plus gros binaire.
        game.Executable ??= exeCandidates
            .Where(e => Path.GetFileName(e.Path).Contains("-Shipping.exe", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Size).FirstOrDefault().Path
            ?? exeCandidates.OrderByDescending(e => e.Size).FirstOrDefault().Path;

        // ReShade compte seulement s'il est charge par le jeu : a cote de l'executable, sous
        // un nom de proxy, ou par OptiScaler. Un ReShade64.dll oublie dans un sous-dossier non.
        game.HasReShade = ReShadeLocator.Scan(DllInstaller.TargetDirectory(game)).Present;
        game.Scanned = true;
        if (raise) game.RaiseAll();
    }

    /// <summary>Un dxgi.dll peut etre ReShade, OptiScaler ou un vrai systeme : la description tranche (ReShade : voir ReShadeLocator).</summary>
    private static void ClassifyProxy(string file, GameInfo game)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            var tag = $"{info.FileDescription} {info.ProductName} {info.CompanyName}";
            if (tag.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) ||
                tag.Contains("DLSS Enabler", StringComparison.OrdinalIgnoreCase)) game.HasOptiScaler = true;
            if (tag.Contains("RTXMFG", StringComparison.OrdinalIgnoreCase) ||
                tag.Contains("MFG Unlock", StringComparison.OrdinalIgnoreCase)) game.HasMfgUnlock = true;
        }
        catch { /* fichier verrouille ou sans metadonnees */ }
    }

    private static bool IsAddon(string name) =>
        name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".addon", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ecarte les binaires annexes qui seraient plus gros que le jeu lui-meme.</summary>
    private static bool IsLauncherLike(string name) =>
        name.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("crashpad", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("crashreport", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("unins", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("redist", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("UnrealCEFSubProcess.exe", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Walk(string dir, int depth)
    {
        if (depth > MaxDepth) yield break;

        string[] files;
        try { files = Directory.GetFiles(dir); }
        catch { yield break; }

        foreach (var f in files) yield return f;

        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch { yield break; }

        foreach (var sub in subs)
        {
            var name = Path.GetFileName(sub);
            if (SkipDirs.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var f in Walk(sub, depth + 1)) yield return f;
        }
    }

    /// <summary>
    /// Version produit, qui porte le numero du paquet la ou la version de fichier
    /// est celle du binaire — c'est le cas des composants Streamline.
    /// </summary>
    public static string? ReadProductVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var v = info.ProductVersion?.Trim();
            if (string.IsNullOrWhiteSpace(v)) return null;

            // La ressource de version Windows separe par des virgules ("2, 7, 1, 0") :
            // on la ramene a la forme pointee attendue par les comparaisons.
            return v.Contains(',') ? v.Replace(" ", "").Replace(',', '.') : v;
        }
        catch { return null; }
    }

    public static string? ReadVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (info.FileMajorPart == 0 && info.FileMinorPart == 0 &&
                info.FileBuildPart == 0 && info.FilePrivatePart == 0)
                return info.FileVersion;
            return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
        }
        catch { return null; }
    }

    private static long SafeSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }
}
