using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// Diagnostic du rendu neural : lit ce que le jeu a reellement fait a son dernier lancement.
///
/// L'overlay RenoDX peut afficher « actif » alors que la passe neurale a ete refusee ou n'a
/// rien change a l'image. La seule source fiable est ReShade.log, a cote de l'executable :
/// Prism y cherche des signatures relevees dans de vrais journaux (Diagnostics/signatures.json),
/// en deduit un verdict, et propose le correctif. Le catalogue est embarque, puis relu depuis
/// GitHub : une signature ajoutee au depot profite a tous sans nouvelle version de Prism.
///
/// S'y ajoutent les versions en place — addon, DLSS, Streamline — comparees a la derniere
/// version stable publiee.
/// </summary>
public sealed class DiagnosticService
{
    private const string Src = "diag";
    private const string ResourceName = "Diagnostics.signatures.json";
    public const string RemoteUrl = "https://raw.githubusercontent.com/Skynizz/Prism/main/Diagnostics/signatures.json";

    /// <summary>Un motif distant mal ecrit ne doit jamais figer l'interface.</summary>
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>Au-dela, seule la fin du journal est lue : c'est la que se trouve la derniere session.</summary>
    private const int MaxLogBytes = 8 * 1024 * 1024;

    private sealed record Signature(string Id, Regex Pattern, string Severity, string? Title, DiagnosisFix Fix, bool UnlessPinned);

    private readonly DownloadService _downloads;
    private readonly RhiRepoService _rhi;
    private readonly DeploymentStore _deployments;

    private List<Signature> _signatures;
    private int _version;

    public DiagnosticService(DownloadService downloads, RhiRepoService rhi, DeploymentStore deployments)
    {
        _downloads = downloads;
        _rhi = rhi;
        _deployments = deployments;
        (_signatures, _version) = Parse(ReadEmbedded()) ?? (new List<Signature>(), 0);
    }

    public int CatalogVersion => _version;
    public int SignatureCount => _signatures.Count;

    // ------------------------------------------------------------- Catalogue

    /// <summary>Relit le catalogue sur GitHub ; garde l'embarque si le distant est absent, invalide ou plus ancien.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var cache = Path.Combine(AppPaths.Cache, "diag-signatures.json");
        string? json = null;
        try { json = await _downloads.GetStringAsync(RemoteUrl, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info(Src, $"Remote signatures unavailable: {ex.Message}");
            if (File.Exists(cache)) json = await File.ReadAllTextAsync(cache, ct);
        }

        if (json is null || Parse(json) is not { } remote) return;
        if (remote.Version < _version) return;

        (_signatures, _version) = remote;
        try { await File.WriteAllTextAsync(cache, json, ct); } catch { /* cache facultatif */ }
        Log.Info(Src, $"Diagnostic signatures v{_version}: {_signatures.Count}");
    }

    private static string ReadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return "";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static (List<Signature> Signatures, int Version)? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out var n) ? n : 0;
            if (!root.TryGetProperty("signatures", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;

            var list = new List<Signature>();
            foreach (var s in arr.EnumerateArray())
            {
                var id = Str(s, "id");
                var pattern = Str(s, "pattern");
                var severity = Str(s, "severity")?.ToLowerInvariant();
                if (id is null || pattern is null || severity is not ("failed" or "degraded" or "working" or "ignore")) continue;

                Regex regex;
                try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget); }
                catch (ArgumentException) { Log.Warn(Src, $"Invalid signature pattern skipped: {id}"); continue; }

                var fix = Str(s, "fix")?.ToLowerInvariant() switch
                {
                    "reinstall" => DiagnosisFix.Reinstall,
                    "reshade" => DiagnosisFix.ReShade,
                    "shortfuse" => DiagnosisFix.ShortFuse,
                    _ => DiagnosisFix.None
                };
                var pinned = s.TryGetProperty("unlessPinned", out var u) && u.ValueKind == JsonValueKind.True;
                list.Add(new Signature(id, regex, severity, Str(s, "title"), fix, pinned));
            }
            return list.Count > 0 ? (list, version) : null;
        }
        catch (JsonException) { return null; }

        static string? Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    // ------------------------------------------------------------- Diagnostic

    public Diagnosis Diagnose(GameInfo game)
    {
        var dir = DllInstaller.TargetDirectory(game);

        // Les conflits valent pour tout jeu, addon DLSS 5 ou pas : ils passent en tete.
        var conflicts = ConflictService.Evaluate(game)
            .Select(c => new DiagnosisFinding { Id = c.Id, State = c.State, Title = c.Title, Evidence = c.Evidence, Fix = c.Fix })
            .ToList();
        var conflictVerdict = conflicts.Any(c => c.State == UiStatus.Error) ? DiagnosisVerdict.Failed : DiagnosisVerdict.Degraded;

        var kind = Dlss5Addon.All.FirstOrDefault(a => File.Exists(Path.Combine(dir, a.FileName)));
        if (kind is null)
            return conflicts.Count == 0
                ? new Diagnosis { Verdict = DiagnosisVerdict.NotApplicable, Summary = Loc.T("diag.no_addon") }
                : new Diagnosis { Verdict = conflictVerdict, Summary = Loc.T("diag.conflicts", conflicts.Count), Findings = conflicts };

        var versions = conflicts.Concat(VersionFindings(game, dir, kind)).ToList();
        var logPath = Path.Combine(dir, "ReShade.log");

        if (!File.Exists(logPath))
            return new Diagnosis
            {
                Verdict = conflicts.Count > 0 ? conflictVerdict : DiagnosisVerdict.Unknown,
                Summary = Loc.T("diag.no_log"), Findings = versions
            };

        var logTime = File.GetLastWriteTime(logPath);

        // Un journal plus ancien que l'addon decrit une autre installation : rien a en conclure.
        if (logTime < File.GetLastWriteTime(Path.Combine(dir, kind.FileName)))
            return new Diagnosis
            {
                Verdict = conflicts.Count > 0 ? conflictVerdict : DiagnosisVerdict.Unknown,
                Summary = Loc.T("diag.stale"),
                LogPath = logPath, LogTime = logTime, Findings = versions
            };

        var findings = new List<DiagnosisFinding>(conflicts);
        var evaluated = 0;

        foreach (var line in ReadLines(logPath))
        {
            foreach (var sig in _signatures)
            {
                Match m;
                try { m = sig.Pattern.Match(line); }
                catch (RegexMatchTimeoutException) { continue; }
                if (!m.Success) continue;

                if (sig.Severity == "ignore") break;
                if (sig.Severity == "working") { evaluated++; break; }

                // Build epinglee par Prism : l'addon la signale comme « non testee », a tort.
                if (sig.UnlessPinned && m.Groups["sha"] is { Success: true } sha && NeuralRuntimePins.IsKnown(sha.Value.ToUpperInvariant()))
                    break;

                if (findings.All(f => f.Id != sig.Id))
                    findings.Add(new DiagnosisFinding
                    {
                        Id = sig.Id,
                        State = sig.Severity == "failed" ? UiStatus.Error : UiStatus.Warning,
                        Title = sig.Title is { } key ? Loc.T(key) : sig.Id,
                        Evidence = Shorten(line),
                        Fix = sig.Fix
                    });
                break;
            }
        }

        var verdict = findings.Any(f => f.State == UiStatus.Error) ? DiagnosisVerdict.Failed
            : findings.Count > 0 ? DiagnosisVerdict.Degraded
            : evaluated > 0 ? DiagnosisVerdict.Working
            : DiagnosisVerdict.Unknown;

        var summary = verdict switch
        {
            DiagnosisVerdict.Working => Loc.T("diag.ok", evaluated),
            DiagnosisVerdict.Unknown => Loc.T("diag.no_eval"),
            _ => Loc.T("diag.issues", findings.Count)
        };

        Log.Info(Src, $"{game.Name}: {verdict} ({findings.Count} finding(s), {evaluated} NR evaluation(s), {kind.Label})");

        return new Diagnosis
        {
            Verdict = verdict,
            Summary = summary,
            LogPath = logPath,
            LogTime = logTime,
            Findings = findings.Concat(versions.Skip(conflicts.Count)).ToList()
        };
    }

    /// <summary>Addon, DLSS et Streamline en place, compares a la derniere version stable publiee.</summary>
    private List<DiagnosisFinding> VersionFindings(GameInfo game, string dir, Dlss5Addon kind)
    {
        var list = new List<DiagnosisFinding>();

        void Older(string id, string label, string? installed, string? latest)
        {
            if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest)) return;
            if (RhiRepoService.CompareVersions(installed, latest) >= 0) return;
            Add(id, label, installed, latest);
        }

        void Add(string id, string label, string installed, string latest)
        {
            list.Add(new DiagnosisFinding
            {
                Id = id,
                State = UiStatus.Detected,
                Title = Loc.T("diag.f.outdated", label, installed, latest),
                Fix = DiagnosisFix.Reinstall
            });
        }

        // Version de l'addon telle que Prism l'a inscrite ; un addon pose a la main n'en a pas.
        var addonPath = Path.Combine(dir, kind.FileName);
        var entry = _deployments.All.FirstOrDefault(e => string.Equals(e.Path, addonPath, StringComparison.OrdinalIgnoreCase));
        var latestAddon = _rhi.Family(kind.TagPrefix).FirstOrDefault();
        if (entry?.Version is { } recorded && recorded.Contains('.'))
            Older("outdated-addon", kind.Label, recorded, latestAddon?.Version);
        else if (latestAddon is not null && AddonBuildDate(addonPath) is { } built
                 && latestAddon.Published.UtcDateTime.Date > built.AddDays(1))
            // Version inconnue (pose a la main, ou inscrite « 5 » par une ancienne version de Prism) :
            // seule la date de build du fichier est sure, comparee a la publication de la derniere release.
            Add("outdated-addon", kind.Label, "build " + built.ToString("yyyy-MM-dd"), latestAddon.Version);

        var (dlss, streamline) = Dlss5PackageInstaller.StackVersions();
        var runtimeDirs = Dlss5PackageInstaller.RuntimeDirectories(game);

        var sr = runtimeDirs.Select(d => Path.Combine(d, "nvngx_dlss.dll")).FirstOrDefault(File.Exists);
        if (sr is not null) Older("outdated-dlss", "DLSS SR", DllDetector.ReadVersion(sr), dlss);

        var sl = runtimeDirs.Select(d => Path.Combine(d, "sl.interposer.dll")).FirstOrDefault(File.Exists);
        if (sl is not null) Older("outdated-streamline", "Streamline", DllDetector.ReadProductVersion(sl), streamline);

        return list;
    }

    /// <summary>
    /// Date de build d'un addon RenoDX : sa FileVersion vaut « 0.2026.0828.2110 » (annee, mois-jour,
    /// heure). Tout autre format renvoie null : aucune date n'est devinee.
    /// </summary>
    private static DateTime? AddonBuildDate(string path)
    {
        try
        {
            var parts = (System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion ?? "").Split('.');
            if (parts.Length < 3 || !int.TryParse(parts[1], out var year) || year is < 2020 or > 2100) return null;
            return DateTime.TryParseExact(parts[1] + parts[2].PadLeft(4, '0'), "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Lignes du journal ; le jeu peut l'avoir encore ouvert, d'ou le partage en ecriture.</summary>
    private static IEnumerable<string> ReadLines(string path)
    {
        string text;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxLogBytes) fs.Seek(-MaxLogBytes, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log.Warn(Src, $"Cannot read {path}: {ex.Message}");
            return Array.Empty<string>();
        }
        return text.Split('\n');
    }

    /// <summary>« 00:56:51:215 [14564] | ERROR | [DLSS 5 ...] DLSS5 Generic: message » → « message ».</summary>
    private static string Shorten(string line)
    {
        var s = line.Trim();
        var bar = s.LastIndexOf(" | ", StringComparison.Ordinal);
        if (bar >= 0) s = s[(bar + 3)..];
        var colon = s.IndexOf("Generic: ", StringComparison.Ordinal);
        if (colon >= 0) s = s[(colon + 9)..];
        if (s.StartsWith('[') && s.IndexOf("] ", StringComparison.Ordinal) is var close and > 0) s = s[(close + 2)..];
        return s.Length > 180 ? s[..177] + "…" : s;
    }
}
