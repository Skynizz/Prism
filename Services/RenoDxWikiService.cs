using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// La page Mods du wiki RenoDX, lue en direct.
///
/// Le wiki est la seule source qui dit, jeu par jeu, <i>ce qu'il faut faire en plus</i>
/// de poser l'addon : quel format de rendu surclasser, s'il faut un Engine.ini,
/// si le HDR du jeu doit etre coupe ou active. Prism le lit a chaque ouverture —
/// jamais une copie figee — et le garde en cache pour fonctionner hors ligne.
///
/// L'index <c>games-index.json</c> de la release snapshot complete la page : il relie
/// chaque mod a l'AppID Steam du jeu, ce qui evite de deviner par le nom.
/// </summary>
public sealed partial class RenoDxWikiService
{
    private const string Src = "renodx";

    public const string WikiRawUrl = "https://raw.githubusercontent.com/wiki/clshortfuse/renodx/Mods.md";
    public const string WikiPageUrl = "https://github.com/clshortfuse/renodx/wiki/Mods";
    public const string GamesIndexUrl = "https://github.com/clshortfuse/renodx/releases/download/snapshot/games-index.json";
    public const string SnapshotBase = "https://github.com/clshortfuse/renodx/releases/download/snapshot/";

    /// <summary>Bloc Engine.ini documente par le wiki pour UE Extended, si la lecture echoue.</summary>
    private static readonly (string Section, string Key, string Value)[] DefaultEngineIni =
    {
        ("SystemSettings", "r.AllowHDR", "1"),
        ("SystemSettings", "r.HDR.EnableHDROutput", "1"),
        ("SystemSettings", "r.HDR.Display.OutputDevice", "3"),
        ("SystemSettings", "r.HDR.Display.ColorGamut", "2"),
        ("SystemSettings", "r.HDR.UI.CompositeMode", "1"),
        ("/Script/Engine.RendererSettings", "r.LUT.UpdateEveryFrame", "1")
    };

    private readonly DownloadService _downloads;
    private List<IndexGame> _index = new();

    public RenoDxWikiService(DownloadService downloads) => _downloads = downloads;

    public IReadOnlyList<HdrWikiEntry> Entries { get; private set; } = Array.Empty<HdrWikiEntry>();

    public string? UeExtendedUrl { get; private set; }
    public string? UnrealLegacyUrl { get; private set; }
    public string? UnityUrl64 { get; private set; }
    public string? UnityUrl32 { get; private set; }

    /// <summary>Cles Engine.ini lues dans le bloc de code du wiki.</summary>
    public IReadOnlyList<(string Section, string Key, string Value)> EngineIniKeys { get; private set; } = DefaultEngineIni;

    public DateTimeOffset? FetchedAt { get; private set; }

    /// <summary>Vrai si la page vient du cache disque faute de reseau.</summary>
    public bool FromCache { get; private set; }

    public bool IsLoaded => Entries.Count > 0;

    // ------------------------------------------------------------ Chargement

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var md = await FetchAsync(WikiRawUrl, "Mods.md", text => Parse(text) >= 50, ct);
        if (md is null) Log.Warn(Src, "Wiki Mods page unavailable, and no cache.");

        var json = await FetchAsync(GamesIndexUrl, "games-index.json", text => ParseIndex(text) > 0, ct);
        if (json is null) Log.Warn(Src, "games-index.json unavailable, and no cache.");

        var ue = await FetchAsync(UeExtendedSourceUrl, "ue-extended-addon.cpp", text => ParseUePresets(text) > 10, ct);
        if (ue is null) Log.Warn(Src, "UE Extended source unavailable, and no cache: built-in presets unknown.");

        Log.Info(Src, $"RenoDX wiki: {Entries.Count} rows, {_index.Count} indexed games, "
                      + $"{UePresets.Count} UE Extended built-in presets" + (FromCache ? " (cache)" : ""));
    }

    // ------------------------------------------------- Prereglages de UE Extended

    /// <summary>
    /// Code source de UE Extended. Le mod embarque une table GAME_SETTINGS : pour chaque jeu, reconnu
    /// par le nom de son executable ou son ProductName, ses propres valeurs par defaut (Set_Path,
    /// surclassements Upgrade_*). Une valeur presente dans ReShade.ini les remplace
    /// (renodx::utils::settings::LoadSetting apres les defauts) : Prism ne doit donc pas y ecrire.
    /// </summary>
    public const string UeExtendedSourceUrl =
        "https://raw.githubusercontent.com/marat569/renodx/main/src/games/ue-extended/addon.cpp";

    /// <summary>Executables et noms de produit que UE Extended configure lui-meme.</summary>
    public IReadOnlySet<string> UePresets { get; private set; } = new HashSet<string>();

    /// <summary>Entrees de GAME_SETTINGS : une chaine seule sur sa ligne, suivie de « GameSettings{ ».</summary>
    internal int ParseUePresets(string source)
    {
        var start = source.IndexOf("GAME_SETTINGS = {", StringComparison.Ordinal);
        if (start < 0) return 0;
        var lines = source[start..].Replace("\r", "").Split('\n');
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < lines.Length; i++)
        {
            if (lines[i] == "};") break;
            var m = UePresetKey().Match(lines[i]);
            if (m.Success && lines[i + 1].Contains("GameSettings{", StringComparison.Ordinal))
                keys.Add(m.Groups[1].Value);
        }
        if (keys.Count > 10) UePresets = keys;
        return keys.Count;
    }

    /// <summary>Vrai si UE Extended a son propre prereglage pour ce jeu (exe, ou ProductName de l'exe).</summary>
    public bool HasUePreset(GameInfo game)
    {
        if (game.Executable is not { } exe) return false;
        if (UePresets.Contains(Path.GetFileName(exe))) return true;
        try
        {
            var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim();
            return !string.IsNullOrEmpty(product) && UePresets.Contains(product);
        }
        catch { return false; }
    }

    /// <summary>
    /// Telecharge, valide, puis met en cache. Un corps invalide (page d'erreur, format
    /// change) n'ecrase jamais un cache sain.
    /// </summary>
    private async Task<string?> FetchAsync(string url, string cacheName, Func<string, bool> accept, CancellationToken ct)
    {
        var cache = Path.Combine(AppPaths.Ensure(Path.Combine(AppPaths.ComponentCache, "renodx-wiki")), cacheName);

        try
        {
            var text = await _downloads.GetStringAsync(url, ct);
            if (accept(text))
            {
                await File.WriteAllTextAsync(cache, text, ct);
                FetchedAt = DateTimeOffset.Now;
                return text;
            }
            Log.Warn(Src, $"{cacheName}: unexpected content, cache kept.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(Src, $"{cacheName}: {ex.Message}");
        }

        if (!File.Exists(cache)) return null;

        var cached = await File.ReadAllTextAsync(cache, ct);
        if (!accept(cached)) return null;

        FromCache = true;
        FetchedAt = File.GetLastWriteTime(cache);
        return cached;
    }

    // --------------------------------------------------------------- Lecture

    private enum Section { None, Main, UeExtended, Legacy, Unity, Other }

    /// <summary>Analyse la page ; renvoie le nombre de lignes retenues.</summary>
    internal int Parse(string markdown)
    {
        var entries = new List<HdrWikiEntry>();
        var section = Section.None;
        string? ueUrl = null, legacyUrl = null, unity64 = null, unity32 = null;
        var engineIni = new List<(string, string, string)>();
        var inIniBlock = false;
        var iniSection = "";

        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith("# List", StringComparison.Ordinal)) { section = Section.Main; continue; }
            if (line.StartsWith("## Multi-Game Mods", StringComparison.Ordinal)) { section = Section.None; continue; }
            // Titre renomme en septembre 2026 : « ### UE Extended » est devenu
            // « ### Unreal Engine Extended », lien vers marat569.github.io. Les deux sont acceptes.
            if (line.StartsWith("### ", StringComparison.Ordinal)
                && (line.Contains("UE Extended", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Unreal Engine Extended", StringComparison.OrdinalIgnoreCase)))
            {
                section = Section.UeExtended;
                ueUrl = AddonUrl(line, ".addon64");
                continue;
            }
            if (line.Contains("<summary>Legacy Unreal Mod", StringComparison.Ordinal)) { section = Section.Legacy; continue; }
            if (section == Section.Legacy && line.StartsWith("### Unreal Engine", StringComparison.Ordinal))
            {
                legacyUrl = AddonUrl(line, ".addon64");
                continue;
            }
            if (line.StartsWith("### Unity Engine", StringComparison.Ordinal)) { section = Section.Unity; continue; }
            if (section == Section.Unity && unity64 is null && line.StartsWith("64-bit", StringComparison.Ordinal))
            {
                unity64 = AddonUrl(line, ".addon64");
                unity32 = AddonUrl(line, ".addon32");
                continue;
            }
            if (line.StartsWith("# ", StringComparison.Ordinal) && !line.StartsWith("# List", StringComparison.Ordinal))
            {
                section = Section.Other;
                continue;
            }

            // Le bloc ```ini de la section UE Extended : c'est lui qui fait foi.
            if (section == Section.UeExtended && line.StartsWith("```", StringComparison.Ordinal))
            {
                inIniBlock = !inIniBlock && line.Contains("ini", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inIniBlock)
            {
                if (line.StartsWith('[') && line.EndsWith(']')) iniSection = line[1..^1];
                else if (line.Length > 0 && line[0] is not '#' and not ';' && line.Contains('='))
                {
                    var eq = line.IndexOf('=');
                    engineIni.Add((iniSection, line[..eq].Trim(), line[(eq + 1)..].Trim()));
                }
                continue;
            }

            if (!line.StartsWith('|') || line.StartsWith("| Name", StringComparison.Ordinal)
                || line.StartsWith("| :", StringComparison.Ordinal)) continue;

            var entry = section switch
            {
                Section.Main => ParseMainRow(line),
                Section.UeExtended => ParseEngineRow(line, HdrModKind.UeExtended),
                Section.Legacy => ParseEngineRow(line, HdrModKind.UnrealLegacy),
                Section.Unity => ParseEngineRow(line, HdrModKind.Unity),
                _ => null
            };
            if (entry is not null) entries.Add(entry);
        }

        if (entries.Count < 50) return entries.Count;

        Entries = entries;
        UeExtendedUrl = ueUrl;
        UnrealLegacyUrl = legacyUrl;
        UnityUrl64 = unity64;
        UnityUrl32 = unity32;
        if (engineIni.Count > 0) EngineIniKeys = engineIni;
        return entries.Count;
    }

    private static HdrWikiEntry? ParseMainRow(string line)
    {
        var cells = Cells(line);
        if (cells.Length < 4) return null;

        var (name, thread) = NameCell(cells[0]);
        if (name.Length == 0) return null;

        string? url64 = null, url32 = null, nexus = null;
        foreach (Match m in LinkTarget().Matches(cells[2]))
        {
            var url = m.Groups[1].Value;
            if (url.Contains("img.shields.io", StringComparison.OrdinalIgnoreCase)) continue;
            if (url.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase)) url64 ??= url;
            else if (url.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase)) url32 ??= url;
            else if (url.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase)) nexus ??= url;
        }

        return new HdrWikiEntry
        {
            Name = name,
            Kind = HdrModKind.Game,
            Maintainer = Clean(cells[1]),
            Status = StatusOf(cells[3]),
            Note = HoverNote(cells[3]),
            Url64 = url64,
            Url32 = url32,
            NexusUrl = nexus,
            ThreadUrl = thread,
            Key = Normalize(name)
        };
    }

    private static HdrWikiEntry? ParseEngineRow(string line, HdrModKind kind)
    {
        var cells = Cells(line);
        if (cells.Length < 2) return null;

        var (name, thread) = NameCell(cells[0]);
        if (name.Length == 0) return null;

        var note = cells.Length > 2 ? NotesCell(cells[2]) : null;

        return new HdrWikiEntry
        {
            Name = name,
            Kind = kind,
            Status = StatusOf(cells[1]),
            Note = string.IsNullOrWhiteSpace(note) ? HoverNote(cells[1]) : note,
            ThreadUrl = thread,
            Key = Normalize(name)
        };
    }

    private static string[] Cells(string line) => line.Trim().Trim('|').Split('|');

    private static (string Name, string? Url) NameCell(string cell)
    {
        var link = NameLink().Match(cell);
        return link.Success
            ? (Clean(link.Groups[1].Value), link.Groups[2].Value)
            : (Clean(cell), null);
    }

    private static HdrModStatus StatusOf(string cell)
        => cell.Contains(":white_check_mark:", StringComparison.Ordinal) ? HdrModStatus.Working
         : cell.Contains(":construction:", StringComparison.Ordinal) ? HdrModStatus.InProgress
         : HdrModStatus.Unknown;

    private static string? HoverNote(string cell)
    {
        var m = HoverNoteRegex().Match(cell);
        return m.Success && m.Groups[1].Value.Trim().Length > 0 ? m.Groups[1].Value.Trim() : null;
    }

    private static string NotesCell(string cell)
    {
        var s = BreakTag().Replace(cell, "\n");
        s = HtmlTag().Replace(s, "");
        s = s.Replace("`", "");
        return string.Join("\n", s.Split('\n').Select(p => Spaces().Replace(p, " ").Trim()).Where(p => p.Length > 0));
    }

    private static string Clean(string s) => Spaces().Replace(HtmlTag().Replace(s, ""), " ").Trim();

    private static string? AddonUrl(string line, string extension)
    {
        foreach (Match m in LinkTarget().Matches(line))
            if (m.Groups[1].Value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return m.Groups[1].Value;
        return null;
    }

    // ----------------------------------------------------------------- Index

    private sealed record IndexArtifact(string Name, bool Is32Bit, bool Plain);

    private sealed record IndexGame(string Title, long? SteamAppId, string? GameExe,
        IReadOnlyList<string> Aliases, IReadOnlyList<IndexArtifact> Artifacts);

    internal int ParseIndex(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
                return 0;

            var list = new List<IndexGame>();
            foreach (var g in games.EnumerateArray())
            {
                var title = Str(g, "title") ?? "";
                long? appId = Long(g, "steam_appid");
                string? exe = null;
                if (g.TryGetProperty("deploy", out var deploy) && deploy.ValueKind == JsonValueKind.Object)
                {
                    appId ??= Long(deploy, "steam_appid");
                    exe = Str(deploy, "game_exe");
                }

                var aliases = new List<string>();
                if (g.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Array)
                    aliases.AddRange(al.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!));

                var artifacts = new List<IndexArtifact>();
                if (g.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mods.EnumerateArray())
                    {
                        var plain = !m.TryGetProperty("variant", out var v) || v.ValueKind == JsonValueKind.Null;
                        if (!m.TryGetProperty("artifacts", out var arts) || arts.ValueKind != JsonValueKind.Array) continue;
                        foreach (var a in arts.EnumerateArray())
                            if (Str(a, "name") is { } n)
                                artifacts.Add(new IndexArtifact(n, Str(a, "arch") == "x86", plain));
                    }
                }

                list.Add(new IndexGame(title, appId, exe, aliases, artifacts));
            }

            _index = list;
            return list.Count;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    // -------------------------------------------------------------- Appariement

    /// <summary>
    /// Le mod qui convient a ce jeu, et tout ce qu'il exige. Ordre de preference :
    /// mod dedie (AppID Steam, puis nom), puis mod generique du moteur — en suivant
    /// toujours la ligne du wiki quand le jeu y figure.
    /// </summary>
    public HdrPlan? PlanFor(GameInfo game)
    {
        if (!IsLoaded && _index.Count == 0) return null;

        var is32 = PeInfo.Is32Bit(game.Executable);
        var keys = CandidateKeys(game);
        // L'AppID vaut aussi pour un jeu hors Steam dont l'identite a ete retrouvee.
        var appId = game.SteamAppId
                    ?? (game.Id.StartsWith("steam:", StringComparison.Ordinal)
                        && long.TryParse(game.Id.AsSpan(6), out var id) ? id : (long?)null);

        // 1. Mod dedie.
        var indexed = (appId is not null ? _index.FirstOrDefault(g => g.SteamAppId == appId) : null)
                      ?? _index.FirstOrDefault(g => keys.Contains(Normalize(g.Title))
                                                    || g.Aliases.Any(a => keys.Contains(Normalize(a)))
                                                    || (g.GameExe is { } e && keys.Contains(Normalize(Path.GetFileNameWithoutExtension(e)))));

        var gameRows = Entries.Where(e => e.Kind == HdrModKind.Game).ToList();
        var row = indexed is not null
            ? gameRows.FirstOrDefault(r => r.Key == Normalize(indexed.Title))
              ?? gameRows.FirstOrDefault(r => keys.Contains(r.Key))
              ?? gameRows.FirstOrDefault(r => indexed.Artifacts.Any(a => FileOf(r.Url64) == a.Name || FileOf(r.Url32) == a.Name)
                                              && keys.Any(k => Similar(k, r.Key)))
            : gameRows.FirstOrDefault(r => keys.Contains(r.Key))
              ?? gameRows.FirstOrDefault(r => keys.Any(k => Similar(k, r.Key)));

        var reason = indexed is not null && appId is not null && indexed.SteamAppId == appId
            ? Loc.T("hdr.reason.steam")
            : Loc.T("hdr.reason.name");

        // Une ligne de la liste principale du wiki : mod dedie, valide par le wiki.
        if (row is not null) return GamePlan(game, row, indexed, is32, reason);

        // Le jeu figure dans une table moteur : c'est la marche a suivre documentee, avec
        // ses reglages. Elle prime sur une build dediee que le wiki ne liste pas encore —
        // c'est le cas de Hollow Cocoon, dont la note Unity exige un surclassement.
        if (Entries.Any(e => e.Kind is HdrModKind.UeExtended or HdrModKind.UnrealLegacy && keys.Contains(e.Key)))
            return UnrealPlan(game, keys, is32);

        if (Entries.Any(e => e.Kind == HdrModKind.Unity && keys.Contains(e.Key)))
            return UnityPlan(game, keys, is32);

        // Build dediee connue de l'index, absente du wiki.
        if (indexed is not null) return GamePlan(game, null, indexed, is32, reason);

        // 2. Mod generique du moteur.
        if (string.Equals(game.Engine, "Unreal", StringComparison.OrdinalIgnoreCase))
            return UnrealPlan(game, keys, is32);

        if (string.Equals(game.Engine, "Unity", StringComparison.OrdinalIgnoreCase))
            return UnityPlan(game, keys, is32);

        return null;
    }

    private HdrPlan GamePlan(GameInfo game, HdrWikiEntry? row, IndexGame? indexed, bool is32, string reason)
    {
        string? url = is32 ? row?.Url32 : row?.Url64;
        var fallback = false;

        // Un mod liste uniquement sur Nexus ou Discord peut exister en build snapshot :
        // on l'installe, et on le dit.
        if (url is null && indexed is not null)
        {
            var artifact = indexed.Artifacts
                .OrderByDescending(a => a.Plain)
                .FirstOrDefault(a => a.Is32Bit == is32);
            if (artifact is not null)
            {
                url = SnapshotBase + artifact.Name;
                fallback = row is not null;
            }
        }

        var plan = new HdrPlan
        {
            Kind = HdrModKind.Game,
            Entry = row ?? (indexed is null ? null : new HdrWikiEntry
            {
                Name = indexed.Title,
                Kind = HdrModKind.Game,
                Key = Normalize(indexed.Title)
            }),
            MatchReason = reason,
            AddonUrl = url,
            AddonFileName = FileOf(url),
            Is32Bit = is32,
            ExternalUrl = url is null ? row?.NexusUrl ?? row?.ThreadUrl ?? WikiPageUrl : null
        };

        if (url is null)
        {
            var other = is32 ? row?.Url64 : row?.Url32;
            plan.BlockedReason = other is not null
                ? Loc.T(is32 ? "hdr.block.no32" : "hdr.block.no64")
                : Loc.T("hdr.block.external");
        }

        AddCommonSteps(plan, game);
        if (fallback) plan.Steps.Add(Manual(Loc.T("hdr.step.snapshot_fallback")));
        AddNoteSteps(plan, row?.Note, HdrModKind.Game);
        AddClosingSteps(plan);
        return plan;
    }

    private HdrPlan UnrealPlan(GameInfo game, HashSet<string> keys, bool is32)
    {
        var ueRow = Entries.FirstOrDefault(e => e.Kind == HdrModKind.UeExtended && keys.Contains(e.Key));
        var legacyRow = Entries.FirstOrDefault(e => e.Kind == HdrModKind.UnrealLegacy && keys.Contains(e.Key));

        // UE Extended est le mod recommande. L'ancien mod n'est retenu que pour un jeu
        // valide avec lui et absent de la table UE Extended.
        // Un prereglage integre a UE Extended prouve que ses auteurs le prennent en charge : il
        // l'emporte sur une ancienne ligne du tableau Legacy (Psychonauts 2).
        var useLegacy = ueRow is null && legacyRow is not null && UnrealLegacyUrl is not null && !HasUePreset(game);
        var kind = useLegacy ? HdrModKind.UnrealLegacy : HdrModKind.UeExtended;
        var row = useLegacy ? legacyRow : ueRow;
        var url = useLegacy ? UnrealLegacyUrl : UeExtendedUrl;

        // UE Extended reconnait lui-meme ce jeu : ses valeurs par defaut, tenues a jour par ses
        // auteurs, s'appliquent seules. Une cle ecrite dans ReShade.ini les remplacerait.
        var preset = kind == HdrModKind.UeExtended && HasUePreset(game);

        var plan = new HdrPlan
        {
            Kind = kind,
            Entry = row,
            MatchReason = Loc.T(useLegacy ? "hdr.reason.ue_legacy"
                              : preset ? "hdr.reason.ue_preset"
                              : row is not null ? "hdr.reason.ue_listed" : "hdr.reason.ue_generic"),
            AddonUrl = url,
            AddonFileName = FileOf(url),
            ExternalUrl = url is null ? WikiPageUrl : null
        };

        if (is32) plan.BlockedReason = Loc.T("hdr.block.no32");
        else if (url is null) plan.BlockedReason = Loc.T("hdr.block.external");

        AddCommonSteps(plan, game);
        if (preset) plan.Steps.Add(Manual(Loc.T("hdr.step.ue_preset")));
        AddNoteSteps(plan, row?.Note, kind, skipReShadeKeys: preset);
        // Jeu absent du wiki et du mod : l'ordre que le wiki donne pour les jeux non listes.
        if (kind == HdrModKind.UeExtended && row is null && !preset)
            plan.Steps.Add(Manual(Loc.T("hdr.step.ue_order")));
        if (kind == HdrModKind.UeExtended) plan.Steps.Add(Manual(Loc.T("hdr.step.ue5_sliders")));
        AddClosingSteps(plan);
        return plan;
    }

    private HdrPlan UnityPlan(GameInfo game, HashSet<string> keys, bool is32)
    {
        var row = Entries.FirstOrDefault(e => e.Kind == HdrModKind.Unity && keys.Contains(e.Key));
        var url = is32 ? UnityUrl32 : UnityUrl64;

        var plan = new HdrPlan
        {
            Kind = HdrModKind.Unity,
            Entry = row,
            MatchReason = Loc.T(row is not null ? "hdr.reason.unity_listed" : "hdr.reason.unity_generic"),
            AddonUrl = url,
            AddonFileName = FileOf(url),
            Is32Bit = is32,
            ExternalUrl = url is null ? WikiPageUrl : null
        };

        if (url is null) plan.BlockedReason = Loc.T("hdr.block.external");

        AddCommonSteps(plan, game);
        AddNoteSteps(plan, row?.Note, HdrModKind.Unity);
        plan.Steps.Add(Manual(Loc.T("hdr.step.unity_fullscreen")));
        plan.Steps.Add(Manual(Loc.T("hdr.step.unity_brightness")));
        AddClosingSteps(plan);
        return plan;
    }

    private static void AddCommonSteps(HdrPlan plan, GameInfo game)
    {
        plan.Steps.Add(new HdrStep { Kind = HdrStepKind.Requirement, Text = Loc.T("hdr.step.reshade") });

        if (plan.AddonFileName is not null)
            plan.Steps.Add(new HdrStep { Kind = HdrStepKind.Addon, Text = Loc.T("hdr.step.addon", plan.AddonFileName) });

        if (plan.Status == HdrModStatus.InProgress)
            plan.Steps.Add(Manual(Loc.T("hdr.step.wip")));
    }

    private void AddNoteSteps(HdrPlan plan, string? note, HdrModKind kind, bool skipReShadeKeys = false)
    {
        foreach (var step in HdrNotes.Parse(note, kind, EngineIniKeys))
        {
            if (skipReShadeKeys && step.Kind == HdrStepKind.ReShadeKey) continue;
            // Une meme cle ne s'ecrit qu'une fois : la derniere mention l'emporte.
            if (step.Key is not null) plan.Steps.RemoveAll(s => s.Key == step.Key);
            plan.Steps.Add(step);
        }
    }

    private static void AddClosingSteps(HdrPlan plan)
    {
        plan.Steps.Add(Manual(Loc.T("hdr.step.no_autohdr")));
        plan.Steps.Add(Manual(Loc.T("hdr.step.restart")));
    }

    private static HdrStep Manual(string text) => new() { Kind = HdrStepKind.Manual, Text = text };

    // ------------------------------------------------------------- Normalisation

    private static HashSet<string> CandidateKeys(GameInfo game)
    {
        var names = new List<string> { game.Name };
        if (!string.IsNullOrWhiteSpace(game.InstallDir)) names.Add(Path.GetFileName(game.InstallDir.TrimEnd('\\', '/')));
        if (!string.IsNullOrWhiteSpace(game.Executable)) names.Add(Path.GetFileNameWithoutExtension(game.Executable));

        return names.Select(Normalize).Where(k => k.Length >= 3).ToHashSet();
    }

    /// <summary>
    /// Lettres et chiffres, en minuscules, sans accents, sans marques ni parentheses :
    /// « Assassin’s Creed® Origins » et « Atlas Fallen (DX12) » deviennent
    /// « assassinscreedorigins » et « atlasfallen ».
    /// </summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        var text = NameLink().Replace(s, "$1");
        text = Parenthesis().Replace(text, "");
        text = text.Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Rapprochement prudent : un prefixe commun long, et des longueurs voisines.</summary>
    private static bool Similar(string a, string b)
    {
        if (a.Length < 8 || b.Length < 8) return false;
        if (!(a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal))) return false;
        return Math.Min(a.Length, b.Length) / (double)Math.Max(a.Length, b.Length) >= 0.8;
    }

    private static string? FileOf(string? url)
        => string.IsNullOrWhiteSpace(url) ? null : Path.GetFileName(new Uri(url).AbsolutePath);

    [GeneratedRegex(@"\]\((https?://[^)\s]+)\)")]
    private static partial Regex LinkTarget();

    [GeneratedRegex(@"^\s*""([^""]+)"",\s*$")]
    private static partial Regex UePresetKey();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]*)\)")]
    private static partial Regex NameLink();

    [GeneratedRegex(@"\(# ""([^""]*)""\)")]
    private static partial Regex HoverNoteRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Parenthesis();
}

/// <summary>
/// Traduit les notes du wiki en actions. Ce que Prism sait appliquer devient une
/// cle ReShade.ini ou un Engine.ini ; le reste est affiche tel quel, a faire soi-meme.
///
/// Les cles viennent du code des mods RenoDX (section <c>[renodx]</c>) :
/// <c>Upgrade_&lt;FORMAT&gt;</c> 0 aucun, 1 taille de sortie, 2 ratio de sortie, 3 toute taille ;
/// <c>Upgrade_CopyDestinations</c>, <c>Use_Swapchain_Proxy</c>, <c>Swapchain_Encoding</c>
/// (1 = gamma), <c>Set_Path</c> pour UE Extended (0 = HDR natif, 1 = conversion SDR).
/// </summary>
public static partial class HdrNotes
{
    public static IEnumerable<HdrStep> Parse(
        string? note, HdrModKind kind, IReadOnlyList<(string Section, string Key, string Value)> engineIni)
    {
        if (string.IsNullOrWhiteSpace(note)) yield break;

        var fragments = note
            .Split('\n')
            .SelectMany(p => Sentence().Split(p))
            .SelectMany(p => p.Split(", "))
            .Select(p => p.Trim().TrimEnd('.').Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var engineIniDone = false;

        foreach (var f in fragments)
        {
            var lower = f.ToLowerInvariant();
            var uncertain = f.Contains('?');
            var conditional = Conditional().IsMatch(lower);

            // Surclassement de format de rendu.
            var formats = Format().Matches(f).Select(m => FixFormat(m.Value)).Distinct().ToList();
            if (formats.Count > 0 && (lower.Contains("upgrade") || Size().IsMatch(f)))
            {
                if (uncertain || conditional)
                {
                    yield return Wiki(f);
                    continue;
                }

                var size = SizeValue(f);
                foreach (var fmt in formats)
                    yield return Key($"Upgrade_{fmt}", size.ToString(CultureInfo.InvariantCulture),
                        Loc.T("hdr.step.upgrade", fmt, Loc.T($"hdr.size.{size}")));
                continue;
            }

            if (CopyDestinations().Match(f) is { Success: true } copy)
            {
                var on = copy.Groups[1].Value.Equals("on", StringComparison.OrdinalIgnoreCase);
                yield return Key("Upgrade_CopyDestinations", on ? "1" : "0",
                    Loc.T(on ? "hdr.step.copy_dest_on" : "hdr.step.copy_dest_off"));
                continue;
            }

            if (lower.Contains("swapchain proxy") && lower.Contains("enable") && !uncertain)
            {
                yield return Key("Use_Swapchain_Proxy", "1", Loc.T("hdr.step.proxy"));
                continue;
            }

            if (Encoding().Match(f) is { Success: true } enc)
            {
                var gamma = enc.Groups[1].Value.Equals("gamma", StringComparison.OrdinalIgnoreCase);
                yield return Key("Swapchain_Encoding", gamma ? "1" : "0",
                    Loc.T(gamma ? "hdr.step.encoding_gamma" : "hdr.step.encoding_linear"));
                continue;
            }

            if (kind == HdrModKind.UeExtended && UpgradePath().Match(f) is { Success: true } path)
            {
                var on = path.Groups[1].Value.Equals("on", StringComparison.OrdinalIgnoreCase);
                yield return Key("Set_Path", on ? "1" : "0", Loc.T(on ? "hdr.step.path_on" : "hdr.step.path_off"));
                if (f.Length > path.Length + 2) yield return Wiki(f);
                continue;
            }

            // « Native HDR » = utiliser le HDR du jeu. Mais « Native HDR is broken » dit l'inverse :
            // une phrase negative ne doit jamais basculer le chemin (Deep Rock Galactic: Rogue Core).
            if (kind == HdrModKind.UeExtended && lower.StartsWith("native hdr", StringComparison.Ordinal)
                && !NegativeNote().IsMatch(lower))
            {
                yield return Key("Set_Path", "0", Loc.T("hdr.step.path_off"));
                yield return new HdrStep { Kind = HdrStepKind.Manual, Text = Loc.T("hdr.step.enable_ingame_hdr") };
                if (lower.Length > "native hdr".Length + 1) yield return Wiki(f);
                continue;
            }

            if (kind == HdrModKind.UeExtended && lower.Contains("r.hdr.enablehdroutput=1") && lower.Contains("only"))
            {
                engineIniDone = true;
                yield return new HdrStep
                {
                    Kind = HdrStepKind.EngineIni,
                    Key = "Engine.ini",
                    Text = Loc.T("hdr.step.engine_ini_one"),
                    IniLines = new[] { ("SystemSettings", "r.HDR.EnableHDROutput", "1") }
                };
                continue;
            }

            if (kind == HdrModKind.UeExtended && lower == "engine.ini")
            {
                // Le wiki demande Upgrade Path: Off avant d'ecrire l'Engine.ini.
                yield return Key("Set_Path", "0", Loc.T("hdr.step.path_off"));
                if (!engineIniDone)
                    yield return new HdrStep
                    {
                        Kind = HdrStepKind.EngineIni,
                        Key = "Engine.ini",
                        Text = Loc.T("hdr.step.engine_ini"),
                        IniLines = engineIni
                    };
                continue;
            }

            if (kind == HdrModKind.Unity && lower.Contains("compatibility offsets") && lower.Contains("+1"))
            {
                yield return Key("Scaling_Offset", "1", Loc.T("hdr.step.offsets"));
                yield return Key("Tonemap_Offset", "1", Loc.T("hdr.step.offsets"));
                continue;
            }

            if (lower.Contains("disable in-game hdr"))
            {
                yield return new HdrStep { Kind = HdrStepKind.Manual, Text = Loc.T("hdr.step.disable_ingame_hdr") };
                if (lower.Length > "disable in-game hdr".Length + 2) yield return Wiki(f);
                continue;
            }

            // Informations deja traitees ailleurs : architecture et API.
            if (Informational().IsMatch(lower)) continue;

            yield return Wiki(f);
        }
    }

    private static HdrStep Key(string key, string value, string text)
        => new() { Kind = HdrStepKind.ReShadeKey, Key = key, Value = value, Text = text };

    private static HdrStep Wiki(string text)
        => new() { Kind = HdrStepKind.Manual, Text = Loc.T("hdr.step.wiki_note"), WikiText = text };

    /// <summary>1 taille de sortie, 2 ratio de sortie, 3 toute taille. Taille de sortie par defaut.</summary>
    private static int SizeValue(string f)
    {
        var m = Size().Match(f);
        if (!m.Success) return 1;
        return m.Value.ToLowerInvariant() switch
        {
            var s when s.Contains("ratio") => 2,
            var s when s.Contains("any") => 3,
            _ => 1
        };
    }

    /// <summary>Le wiki contient au moins une coquille : R8G8R8A8 pour R8G8B8A8.</summary>
    private static string FixFormat(string f) => f.ToUpperInvariant().Replace("R8G8R8A8", "R8G8B8A8");

    [GeneratedRegex(@"(?<=\.)\s+(?=[A-Z])")]
    private static partial Regex Sentence();

    [GeneratedRegex(@"\b(R8G8[BR]8A8_(?:TYPELESS|UNORM_SRGB|UNORM)|B8G8R8A8_(?:TYPELESS|UNORM)|R10G10B10A2_(?:TYPELESS|UNORM)|R11G11B10_FLOAT|R16G16B16A16_TYPELESS)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Format();

    [GeneratedRegex(@"output\s*size|output\s*ratio|any\s*size", RegexOptions.IgnoreCase)]
    private static partial Regex Size();

    [GeneratedRegex(@"\b(if|when|optional|for render resolution|for any other)\b")]
    private static partial Regex Conditional();

    [GeneratedRegex(@"upgrade\s+copy\s+destinations\s*:?\s*(on|off)", RegexOptions.IgnoreCase)]
    private static partial Regex CopyDestinations();

    [GeneratedRegex(@"encoding\s*:\s*(gamma|linear)", RegexOptions.IgnoreCase)]
    private static partial Regex Encoding();

    [GeneratedRegex(@"upgrade\s+path\s*:\s*(on|off)", RegexOptions.IgnoreCase)]
    private static partial Regex UpgradePath();

    [GeneratedRegex(@"\b(broken|not|doesn't|does not|don't|no longer|issues?|buggy|crash\w*|avoid)\b")]
    private static partial Regex NegativeNote();

    [GeneratedRegex(@"^(32-bit|64-bit|dx11|dx12|works out of the box)$")]
    private static partial Regex Informational();
}
