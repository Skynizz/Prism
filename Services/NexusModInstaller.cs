using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>Ou poser un mod.</summary>
public enum ModLayout
{
    /// <summary>Chaque fichier a sa destination, deduite des regles du jeu et du moteur.</summary>
    Mapped,
    /// <summary>Choix de l'utilisateur : le reste de l'archive dans le dossier du jeu.</summary>
    GameRoot,
    /// <summary>Choix de l'utilisateur : le reste de l'archive a cote de l'executable.</summary>
    ExeDir,
    /// <summary>Une partie des fichiers n'a pas de destination sure : l'utilisateur choisit.</summary>
    Unknown
}

/// <summary>Ce que Prism compte faire d'une archive, avant d'ecrire quoi que ce soit.</summary>
public sealed class ModPlan
{
    public required string Archive { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Domain { get; init; }
    public long? ModId { get; init; }

    /// <summary>Racine de l'archive extraite.</summary>
    public required string Source { get; init; }
    public ModLayout Layout { get; set; }

    /// <summary>Dossier commun des destinations, pour l'affichage.</summary>
    public string? Target { get; set; }

    /// <summary>Chemins relatifs a <see cref="Source"/>, notices de premier niveau exclues.</summary>
    public List<string> Files { get; } = new();

    /// <summary>Destination de chaque fichier, chemin relatif → chemin absolu.</summary>
    public Dictionary<string, string> Map { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Unmapped => Files.Where(f => !Map.ContainsKey(f)).ToList();

    /// <summary>Un addon, un preset ou un shader ReShade : ReShade doit etre en place.</summary>
    public bool NeedsReShade { get; set; }

    /// <summary>Pourquoi l'emplacement est incertain, ou pourquoi l'installation est refusee.</summary>
    public string? Note { get; set; }
    public bool Blocked { get; set; }

    /// <summary>Remarques a montrer apres l'installation (REDmod a deployer...).</summary>
    public List<string> AfterNotes { get; } = new();

    public string Origin => $"Nexus · {Name}";
}

/// <summary>Trace d'un mod Nexus installe, pour retrouver sa page, ses prerequis et ses mises a jour.</summary>
public sealed class NexusInstall
{
    public string GameId { get; set; } = "";
    public string Origin { get; set; } = "";
    public string? Domain { get; set; }
    public long? ModId { get; set; }
    public string? Version { get; set; }
    public string Archive { get; set; } = "";
    public string Target { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
}

/// <summary>
/// Pose un mod recu de Nexus au bon endroit, fichier par fichier. Dans l'ordre :
///  1. les regles du jeu (ModRules/rules.json) : un fichier-repere designe un dossier de mod
///     complet (init.lua d'un mod CET, info.json REDmod, manifest.json SMAPI...) ;
///  2. l'ancrage : le premier dossier du chemin qui existe dans le jeu (archive\, r6\, Data\,
///     meshes\ pour un jeu Bethesda...) cale le fichier sur la racine, quelle que soit la
///     profondeur des dossiers d'emballage ;
///  3. les fichiers isoles, par extension : regles du jeu, puis moteur (plugins Bethesda → Data,
///     paks Unreal → ~mods, mods UE4SS, BepInEx, MelonLoader, addons et presets ReShade → exe) ;
///  4. le dossier de mods du jeu quand il en a un (Mods\, mods\, GameData\...) ;
///  5. ce qui reste est montre a l'utilisateur, qui choisit.
/// L'ecriture passe par <see cref="FileTransaction"/> : originaux sauvegardes, tout ou rien,
/// chaque fichier inscrit sous « Nexus · nom » pour un retrait en un clic.
/// </summary>
public sealed class NexusModInstaller
{
    private const string Src = "nexus";

    private readonly BackupService _backups;
    private readonly DeploymentStore _deployments;

    public NexusModInstaller(BackupService backups, DeploymentStore deployments)
    {
        _backups = backups;
        _deployments = deployments;
    }

    private static string StoreFile => Path.Combine(AppPaths.Root, "nexus-installs.json");

    public static List<NexusInstall> Installed() => JsonStore.Load(StoreFile, () => new List<NexusInstall>());

    private static readonly string[] ProxyDlls =
    { "dxgi.dll", "d3d9.dll", "d3d11.dll", "d3d12.dll", "dinput8.dll", "version.dll", "winmm.dll", "dwmapi.dll", "winhttp.dll", "opengl32.dll" };

    private static readonly string[] UnrealArchives = { ".pak", ".utoc", ".ucas", ".sig" };

    /// <summary>Ce qui accompagne un mod sans servir au jeu : notices, captures.</summary>
    private static readonly string[] Extras =
    { ".txt", ".md", ".pdf", ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".url", ".html", ".htm", ".nfo", ".rtf", ".docx" };

    /// <summary>Dossiers de premier niveau trop courants pour dire quoi que ce soit.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    { "data", "content", "config", "plugins", "x64", "win64", "bin", "docs", "images", "screenshots" };

    // ------------------------------------------------------------------ Analyse

    public async Task<ModPlan> AnalyzeAsync(GameInfo game, string archive, string? domain, string? pageTitle, CancellationToken ct = default)
    {
        var (fileName, modId, version) = NexusService.ParseArchiveName(archive);
        var name = NexusService.ModNameFromTitle(pageTitle) ?? fileName;

        var work = Path.Combine(NexusService.Downloads, "extract", AppPaths.Sanitize(Path.GetFileNameWithoutExtension(archive)));
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        await ArchiveExtractor.ExtractAsync(archive, work, ct);

        var plan = new ModPlan { Archive = archive, Name = name, Version = version, Domain = domain, ModId = modId, Source = work };

        foreach (var f in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(work, f);
            // Notices a la racine de l'archive : jamais utiles au jeu.
            if (!rel.Contains(Path.DirectorySeparatorChar) && !ArchiveExtractor.IsPayload(rel)) continue;
            plan.Files.Add(rel);
        }

        if (plan.Files.Count == 0)
        {
            plan.Blocked = true;
            plan.Note = Loc.T("nexus.err.empty");
            return plan;
        }

        new Mapper(game, plan).Run();
        CheckArchitecture(game, plan);
        plan.Target = CommonDir(plan.Map.Values);
        Log.Info(Src, $"{name}: {plan.Files.Count} file(s), {plan.Map.Count} placed, layout {plan.Layout} → {plan.Target ?? "?"}");
        return plan;
    }

    /// <summary>Choix de l'utilisateur pour ce qui n'avait pas de destination.</summary>
    public static void Assign(ModPlan plan, string baseDir, ModLayout layout)
    {
        var rest = plan.Unmapped;
        var strip = WrapperDepth(rest);
        foreach (var rel in rest)
            plan.Map[rel] = Path.Combine(baseDir, string.Join(Path.DirectorySeparatorChar, Segments(rel).Skip(strip)));
        plan.Layout = layout;
        plan.Target = CommonDir(plan.Map.Values);
    }

    /// <summary>Un addon 32 bits dans un jeu 64 bits (ou l'inverse) ne serait jamais charge.</summary>
    private static void CheckArchitecture(GameInfo game, ModPlan plan)
    {
        var is32 = PeInfo.Is32Bit(game.Executable);
        var wrong = plan.Files.FirstOrDefault(f => f.EndsWith(is32 ? ".addon64" : ".addon32", StringComparison.OrdinalIgnoreCase));
        if (wrong is null) return;
        plan.Blocked = true;
        plan.Note = Loc.T("nexus.err.arch", Path.GetFileName(wrong));
    }

    // ------------------------------------------------------------------ Regles

    /// <summary>Une passe de placement sur une archive, pour un jeu.</summary>
    private sealed class Mapper
    {
        private readonly GameInfo _game;
        private readonly ModPlan _plan;
        private readonly GameModRule? _rule;
        private readonly string _root;
        private readonly string _exeDir;
        private readonly string? _dataRoot;
        private readonly string? _paks;
        private readonly string? _ue4ssMods;
        private readonly bool _bepInEx;
        private readonly bool _melon;
        private readonly string _modName;
        private readonly HashSet<string> _rootDirs;
        private readonly HashSet<string> _exeDirs;

        public Mapper(GameInfo game, ModPlan plan)
        {
            _game = game;
            _plan = plan;
            _rule = ModRules.For(plan.Domain);
            _root = game.InstallDir;
            _exeDir = DllInstaller.TargetDirectory(game);
            _dataRoot = DataRoot(_root);
            _paks = PaksDir(game);
            _ue4ssMods = new[] { Path.Combine(_exeDir, "ue4ss", "Mods"), Path.Combine(_exeDir, "Mods") }
                .FirstOrDefault(d => Directory.Exists(d) && (d.Contains(@"\ue4ss\", StringComparison.OrdinalIgnoreCase)
                                                             || File.Exists(Path.Combine(_exeDir, "UE4SS.dll"))));
            _bepInEx = Directory.Exists(Path.Combine(_root, "BepInEx"));
            _melon = Directory.Exists(Path.Combine(_root, "MelonLoader"));
            _modName = AppPaths.Sanitize(plan.Name);
            _rootDirs = SubDirs(_root);
            _exeDirs = SameDir(_exeDir, _root) ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : SubDirs(_exeDir);
        }

        public void Run()
        {
            if (_rule?.Unsupported is { } key)
            {
                _plan.Blocked = true;
                _plan.Note = Loc.T(key);
                return;
            }

            if (Directory.Exists(Path.Combine(_plan.Source, "fomod")) ||
                _plan.Files.Any(f => Path.GetFileName(f).Equals("ModuleConfig.xml", StringComparison.OrdinalIgnoreCase)))
            {
                _plan.Layout = ModLayout.Unknown;
                _plan.Note = Loc.T("nexus.note.fomod");
                return;
            }

            _plan.NeedsReShade = _plan.Files.Any(f => Path.GetExtension(f).ToLowerInvariant() is ".addon64" or ".addon32" or ".fx" or ".fxh")
                                 || _plan.Files.Any(f => f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && IsReShadePreset(Path.Combine(_plan.Source, f)));

            Markers();
            Anchors();
            Extensions();
            ModRoot();
            ExeSide();
            DropExtras();

            _plan.Layout = _plan.Unmapped.Count == 0 ? ModLayout.Mapped : ModLayout.Unknown;
            if (_plan.Layout == ModLayout.Unknown) _plan.Note = Loc.T("nexus.note.unknown");
        }

        // 1. Fichiers-reperes : un dossier de mod complet.
        private void Markers()
        {
            var markers = new List<(ModMarker Marker, string? Absolute)>();
            if (_rule is not null) markers.AddRange(_rule.Markers.Select(m => (m, (string?)null)));
            // UE4SS : <Mod>\Scripts\main.lua, a poser dans ue4ss\Mods.
            if (_ue4ssMods is not null)
                markers.Add((new ModMarker { File = "main.lua", ParentName = "Scripts", Up = 1, Dest = "{dir}" }, _ue4ssMods));

            var found = new List<(string Dir, ModMarker Marker, string? Absolute)>();
            foreach (var (marker, absolute) in markers)
                foreach (var rel in _plan.Files)
                    if (MarkerDir(rel, marker) is { } dir && Siblings(dir, marker)) found.Add((dir, marker, absolute));

            // Les dossiers les plus hauts d'abord : un sous-dossier deja couvert n'est pas repris.
            foreach (var (dir, marker, absolute) in found.OrderBy(f => Segments(f.Dir).Length).DistinctBy(f => f.Dir))
            {
                var name = dir.Length == 0 ? _modName : Path.GetFileName(dir);
                var baseDir = Resolve(marker.Dest.Replace("{dir}", name).Replace("{mod}", _modName), absolute);
                var hit = false;
                foreach (var rel in _plan.Files.Where(f => !_plan.Map.ContainsKey(f) && Under(f, dir)))
                {
                    _plan.Map[rel] = Path.Combine(baseDir, dir.Length == 0 ? rel : Path.GetRelativePath(dir, rel));
                    hit = true;
                }
                if (hit && marker.Note is { } note && !_plan.AfterNotes.Contains(Loc.T(note))) _plan.AfterNotes.Add(Loc.T(note));
            }
        }

        private static string? MarkerDir(string rel, ModMarker m)
        {
            var seg = Segments(rel);
            var file = seg[^1];
            int dirLen;
            if (m.Dir is not null)
            {
                var i = Array.FindIndex(seg, 0, seg.Length - 1, s => s.Equals(m.Dir, StringComparison.OrdinalIgnoreCase));
                if (i < 0) return null;
                dirLen = i;
            }
            else
            {
                if (m.File is not null && !file.Equals(m.File, StringComparison.OrdinalIgnoreCase)) return null;
                if (m.Ext is not null && !file.EndsWith(m.Ext, StringComparison.OrdinalIgnoreCase)) return null;
                if (m.File is null && m.Ext is null) return null;
                if (m.ParentName is not null && (seg.Length < 2 || !seg[^2].Equals(m.ParentName, StringComparison.OrdinalIgnoreCase))) return null;
                dirLen = seg.Length - 1 - m.Up;
                if (dirLen < 0) return null;
            }
            return string.Join(Path.DirectorySeparatorChar, seg.Take(dirLen));
        }

        private bool Siblings(string dir, ModMarker m)
        {
            if (m.WithSibling is not { Count: > 0 }) return true;
            var full = Path.Combine(_plan.Source, dir);
            return m.WithSibling.Any(s => Directory.Exists(Path.Combine(full, s)) || File.Exists(Path.Combine(full, s)));
        }

        // 2. Ancrage : le premier dossier du chemin qui existe dans le jeu.
        private void Anchors()
        {
            var stops = new HashSet<string>(_rule?.StopFolders ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var dataStops = _dataRoot is null ? null : new HashSet<string>(ModRules.Bethesda.DataStopFolders, StringComparer.OrdinalIgnoreCase);

            foreach (var rel in _plan.Files.Where(f => !_plan.Map.ContainsKey(f)).ToList())
            {
                var seg = Segments(rel);
                // Au plus trois dossiers d'emballage avant l'ancre.
                for (var i = 0; i < Math.Min(seg.Length - 1, 4); i++)
                {
                    var s = seg[i];
                    var tail = string.Join(Path.DirectorySeparatorChar, seg.Skip(i));

                    if (dataStops is not null && dataStops.Contains(s) && !_rootDirs.Contains(s))
                    {
                        _plan.Map[rel] = Path.Combine(_dataRoot!, tail);
                        break;
                    }
                    if (stops.Contains(s) || (_rootDirs.Contains(s) && !Generic.Contains(s)) ||
                        (_rootDirs.Contains(s) && i == 0 && Generic.Contains(s) && AllUnder(s)))
                    {
                        _plan.Map[rel] = Path.Combine(_root, tail);
                        break;
                    }
                    if (_exeDirs.Contains(s) && !Generic.Contains(s))
                    {
                        _plan.Map[rel] = Path.Combine(_exeDir, tail);
                        break;
                    }
                }
            }
        }

        /// <summary>Un dossier courant (bin, Data...) n'ancre que s'il porte toute l'archive.</summary>
        private bool AllUnder(string top)
            => _plan.Files.All(f => Segments(f)[0].Equals(top, StringComparison.OrdinalIgnoreCase) || !f.Contains(Path.DirectorySeparatorChar));

        // 3. Fichiers isoles, par extension.
        private void Extensions()
        {
            foreach (var rel in _plan.Files.Where(f => !_plan.Map.ContainsKey(f)).ToList())
            {
                var file = Path.GetFileName(rel);
                var ext = Path.GetExtension(file).ToLowerInvariant();

                var rule = _rule?.Extensions.FirstOrDefault(r => r.Ext.Contains(ext, StringComparer.OrdinalIgnoreCase));
                string? dest = rule is not null ? Path.Combine(Resolve(rule.Dest.Replace("{mod}", _modName), null), file)
                    : _dataRoot is not null && ModRules.Bethesda.PluginExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) ? Path.Combine(_dataRoot, file)
                    : _paks is not null && UnrealArchives.Contains(ext) ? Path.Combine(_paks, "~mods", file)
                    : ext is ".addon64" or ".addon32" or ".asi" ? Path.Combine(_exeDir, file)
                    : ProxyDlls.Contains(file, StringComparer.OrdinalIgnoreCase) ? Path.Combine(_exeDir, file)
                    : ext is ".fx" or ".fxh" ? Path.Combine(_exeDir, "reshade-shaders", "Shaders", file)
                    : ext == ".ini" && IsReShadePreset(Path.Combine(_plan.Source, rel)) ? Path.Combine(_exeDir, file)
                    : ext == ".dll" && _bepInEx ? Path.Combine(_root, "BepInEx", "plugins", _modName, file)
                    : ext == ".dll" && _melon ? Path.Combine(_root, "Mods", file)
                    : null;
                if (dest is not null) _plan.Map[rel] = dest;
            }
        }

        // 4. Dossier de mods du jeu : chaque dossier de l'archive y devient un mod.
        private void ModRoot()
        {
            if (_rule?.ModRoot is not { } modRoot) return;
            var rest = _plan.Unmapped.Where(f => !IsExtra(f)).ToList();
            if (rest.Count == 0) return;

            var baseDir = Path.Combine(_root, modRoot);
            var strip = WrapperDepth(rest);
            // Un seul dossier restant apres emballage : c'est le dossier du mod.
            foreach (var rel in rest)
            {
                var seg = Segments(rel).Skip(strip).ToArray();
                _plan.Map[rel] = seg.Length == 1
                    ? Path.Combine(baseDir, _modName, seg[0])
                    : Path.Combine(baseDir, string.Join(Path.DirectorySeparatorChar, seg));
            }
        }

        // 5. Ce qui se charge a cote de l'executable : DLL, configs, dossiers ReShade.
        private void ExeSide()
        {
            var rest = _plan.Unmapped.Where(f => !IsExtra(f)).ToList();
            if (rest.Count == 0) return;
            // Un .exe n'y va qu'accompagne de ses DLL : un chargeur (skse64_loader.exe), pas un installeur.
            var withDll = rest.Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            var exeSide = rest.All(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext is ".dll" or ".ini" or ".toml" or ".json" or ".asi" or ".bin"
                       || (ext == ".exe" && withDll)
                       || f.StartsWith("reshade-shaders", StringComparison.OrdinalIgnoreCase);
            });
            if (!exeSide) return;
            var strip = WrapperDepth(rest);
            foreach (var rel in rest)
                _plan.Map[rel] = Path.Combine(_exeDir, string.Join(Path.DirectorySeparatorChar, Segments(rel).Skip(strip)));
        }

        // Captures et notices laissees de cote quand le reste a trouve sa place.
        private void DropExtras()
        {
            if (_plan.Map.Count == 0) return;
            foreach (var rel in _plan.Unmapped.Where(IsExtra)) _plan.Files.Remove(rel);
        }

        private string Resolve(string dest, string? absolute)
            => absolute is not null ? Path.Combine(absolute, dest) : Path.Combine(_root, dest.Replace('/', Path.DirectorySeparatorChar));
    }

    // ---------------------------------------------------------------- Outils

    private static bool IsExtra(string rel) => Extras.Contains(Path.GetExtension(rel).ToLowerInvariant());

    private static string[] Segments(string rel) => rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

    private static bool Under(string rel, string dir)
        => dir.Length == 0 || rel.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Nombre de dossiers d'emballage communs a tous les fichiers (« Mod v1.2\Mod\ »).</summary>
    private static int WrapperDepth(List<string> files)
    {
        var depth = 0;
        while (true)
        {
            var parts = files.Select(Segments).ToList();
            if (parts.Any(p => p.Length <= depth + 1)) return depth;
            var first = parts[0][depth];
            if (!parts.All(p => p[depth].Equals(first, StringComparison.OrdinalIgnoreCase))) return depth;
            depth++;
        }
    }

    private static HashSet<string> SubDirs(string dir)
    {
        try { return new HashSet<string>(Directory.GetDirectories(dir).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase); }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Dossier Data d'un jeu Bethesda : il porte les .esm du jeu.</summary>
    public static string? DataRoot(string root)
    {
        foreach (var name in ModRules.Bethesda.DataFolders)
        {
            var dir = Path.Combine(root, name);
            try { if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.esm").Any()) return dir; }
            catch { /* illisible */ }
        }
        return null;
    }

    /// <summary>Preset ReShade : un .ini qui declare ses techniques.</summary>
    private static bool IsReShadePreset(string path)
    {
        try { return File.ReadLines(path).Take(200).Any(l => l.StartsWith("Techniques=", StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    /// <summary>&lt;Projet&gt;\Content\Paks du jeu, celui qui porte le plus gros .pak.</summary>
    public static string? PaksDir(GameInfo game)
    {
        try
        {
            return Directory.EnumerateDirectories(game.InstallDir)
                .Where(d => !Path.GetFileName(d).Equals("Engine", StringComparison.OrdinalIgnoreCase))
                .Select(d => Path.Combine(d, "Content", "Paks"))
                .Where(Directory.Exists)
                .OrderByDescending(d => Directory.EnumerateFiles(d, "*.pak").Select(f => new FileInfo(f).Length).DefaultIfEmpty(0).Max())
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static bool SameDir(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string? CommonDir(IEnumerable<string> paths)
    {
        string? common = null;
        foreach (var p in paths)
        {
            var dir = Path.GetDirectoryName(p) ?? p;
            if (common is null) { common = dir; continue; }
            while (common.Length > 0 && !(dir + '\\').StartsWith(common.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase))
                common = Path.GetDirectoryName(common) ?? "";
        }
        return string.IsNullOrEmpty(common) ? null : common;
    }

    // ------------------------------------------------------------- Installation

    public InstallResult Install(GameInfo game, ModPlan plan)
    {
        if (plan.Blocked) return new InstallResult(false, plan.Note ?? Loc.T("nexus.err.empty"));
        if (plan.Unmapped.Count > 0) return new InstallResult(false, Loc.T("nexus.note.unknown"));

        // Deja installe sous ce nom : l'ancienne version part dans la meme transaction.
        var previous = _deployments.For(game.Id)
            .Where(e => string.Equals(e.Origin, plan.Origin, StringComparison.Ordinal))
            .Select(e => e.Path).ToList();

        var tx = new FileTransaction(game, _backups, _deployments, plan.Origin);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(game.InstallDir);
        foreach (var (rel, destRaw) in plan.Map)
        {
            var dest = Path.GetFullPath(destRaw);
            // Garde-fou : rien ne sort du dossier du jeu.
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            tx.Copy(Path.Combine(plan.Source, rel), dest, plan.Name, plan.Version ?? "");
            targets.Add(dest);
        }
        foreach (var old in previous.Where(p => !targets.Contains(p))) tx.Delete(old);

        var result = tx.Commit();
        if (!result.Success) return result;

        var list = Installed();
        list.RemoveAll(i => i.GameId == game.Id && i.Origin == plan.Origin);
        list.Add(new NexusInstall
        {
            GameId = game.Id, Origin = plan.Origin, Domain = plan.Domain, ModId = plan.ModId, Version = plan.Version,
            Archive = Path.GetFileName(plan.Archive), Target = plan.Target ?? "", InstalledAt = DateTimeOffset.Now
        });
        JsonStore.Save(StoreFile, list);

        Log.Info(Src, $"{plan.Origin}: {targets.Count} file(s) → {plan.Target}");
        var msg = Loc.T("nexus.ok", plan.Name, targets.Count, plan.Target ?? game.InstallDir);
        if (plan.AfterNotes.Count > 0) msg += " " + string.Join(" ", plan.AfterNotes);
        return new InstallResult(true, msg, targets.Count);
    }
}
