using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>
/// RenoDX DLSS 5, paquet complet : l'addon et toute la pile runtime qui va avec.
///
/// Quatre temps, dans cet ordre et sans raccourci :
///  1. tout telecharger et extraire, sans toucher au jeu, et refuser un paquet auquel il
///     manque Streamline ou le runtime neural ;
///  2. verifier la signature Authenticode de <i>chaque</i> DLL — une seule qui n'est
///     pas signee par NVIDIA et rien n'est ecrit ;
///  3. poser l'ensemble au bon endroit : l'addon a cote de ReShade, Streamline et les DLL
///     NGX la ou le jeu charge Streamline ; originaux sauvegardes, ajouts inscrits ;
///  4. relire chaque fichier pose et le comparer, empreinte a empreinte, a la source.
///
/// Un paquet partiel, ou un nvngx_dlssnr.dll pose loin de sl.interposer.dll, est
/// exactement ce qui produit « NO NR FEATURE MATCHED » en jeu.
/// </summary>
public sealed class Dlss5PackageInstaller
{
    private const string Src = "dlss5";

    /// <summary>Nom de l'installation dans le registre : tout le paquet se retire d'un bloc.</summary>
    public const string Origin = "RenoDX DLSS 5";

    public const string AddonFileName = "renodx-dlss5.addon64";
    public const string NeuralRuntimeFile = "nvngx_dlssnr.dll";

    /// <summary>Fichiers sans lesquels la pile ne demarre pas : absents du paquet, rien n'est ecrit.</summary>
    public static readonly string[] RequiredFiles = { "sl.interposer.dll", "sl.common.dll", NeuralRuntimeFile };

    /// <summary>
    /// Runtime neural de confiance : signe par NVIDIA, ou build repatchee dont
    /// l'empreinte figure dans <see cref="NeuralRuntimePins"/>.
    /// </summary>
    public static bool IsTrustedRuntime(string path)
        => Authenticode.Verify(path).IsNvidia || NeuralRuntimePins.IsKnown(DownloadService.Sha256Cached(path));

    /// <summary>
    /// Dossiers d'ou le jeu charge Streamline. Streamline cherche ses plugins et les DLL NGX a
    /// cote de sl.interposer.dll — a cote de l'executable par defaut, ou dans le chemin que le
    /// jeu lui donne. Chacun de ces dossiers recoit la pile entiere. Un jeu sans Streamline la
    /// recoit a cote de son executable.
    /// </summary>
    public static IReadOnlyList<string> RuntimeDirectories(GameInfo game)
    {
        var dirs = game.StreamlineDirectories
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return dirs.Count > 0 ? dirs : new[] { DllInstaller.TargetDirectory(game) };
    }

    /// <summary>Dossiers relatifs au jeu, pour l'affichage : "bin\x64", ou le nom du dossier racine.</summary>
    public static string Describe(GameInfo game, IEnumerable<string> dirs) =>
        string.Join(", ", dirs.Select(d =>
        {
            var relative = Path.GetRelativePath(game.InstallDir, d);
            return relative == "." ? Path.GetFileName(d.TrimEnd('\\', '/')) : relative;
        }));

    private readonly RhiRepoService _rhi;
    private readonly DownloadService _downloads;
    private readonly BackupService _backups;
    private readonly DeploymentStore _deployments;
    private readonly Func<GpuInfo> _gpu;

    public Dlss5PackageInstaller(RhiRepoService rhi, DownloadService downloads, BackupService backups,
        DeploymentStore deployments, Func<GpuInfo> gpu)
    {
        _rhi = rhi;
        _downloads = downloads;
        _backups = backups;
        _deployments = deployments;
        _gpu = gpu;
    }

    private sealed record PackageFile(string Source, string Name, RhiRepoService.Release From);

    public async Task<InstallResult> InstallAsync(
        GameInfo game, string? addonTag, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!_rhi.IsLoaded) await _rhi.LoadAsync(ct);

        var addon = (addonTag is null ? null : _rhi.ByTag(addonTag))
                    ?? _rhi.Family(RhiRepoService.Dlss5AddonPrefix).FirstOrDefault();
        if (addon is null) return new InstallResult(false, Loc.T("dlss5.err.no_release"));

        var stack = RhiRepoService.Dlss5StackFor(_gpu());
        var missing = stack.Where(t => _rhi.ByTag(t) is null).ToList();
        if (missing.Count > 0)
            return new InstallResult(false, Loc.T("dlss5.err.missing", string.Join(", ", missing)));

        // L'addon va a cote de ReShade, donc de l'executable ; la pile, la ou Streamline est charge.
        var exeDir = DllInstaller.TargetDirectory(game);
        if (!Directory.Exists(exeDir)) return new InstallResult(false, Loc.T("err.target_missing"));
        if (!game.HasReShade) return new InstallResult(false, Loc.T("hdr.err.reshade"));

        var runtimeDirs = RuntimeDirectories(game);

        // Un seul addon neural par dossier. Un autre, pose a la main, n'est pas le notre.
        var others = Directory.EnumerateFiles(exeDir, "renodx-dlss5*.addon64")
            .Where(p => !Path.GetFileName(p).Equals(AddonFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var foreign = others.Where(p => !_deployments.WasDeployed(p)).Select(Path.GetFileName).ToList();
        if (foreign.Count > 0)
            return new InstallResult(false, Loc.T("dlss5.err.foreign", string.Join(", ", foreign)));

        try
        {
            // 1. Telechargement et extraction.
            var releases = new List<RhiRepoService.Release> { addon };
            releases.AddRange(stack.Select(t => _rhi.ByTag(t)!));

            var files = new List<PackageFile>();
            for (var i = 0; i < releases.Count; i++)
            {
                var r = releases[i];
                var root = Path.Combine(AppPaths.ComponentCache, "rhi", AppPaths.Sanitize(r.Tag));
                var zip = Path.Combine(root, r.AssetName);
                var index = i;
                var part = new Progress<double>(p => progress?.Report((index + p / 100.0) / releases.Count * 90));

                await _downloads.DownloadAsync(r.Url, zip, null, part, ct);

                var extract = Path.Combine(root, "files");
                if (!Directory.Exists(extract) || !Directory.EnumerateFiles(extract, "*", SearchOption.AllDirectories).Any())
                    await ArchiveExtractor.ExtractAsync(zip, extract, ct);

                foreach (var f in Directory.EnumerateFiles(extract, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(f);
                    var ext = Path.GetExtension(name);
                    if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                        && !ext.Equals(".addon64", StringComparison.OrdinalIgnoreCase)) continue;

                    // L'addon garde un nom stable : c'est lui qu'on inscrit en chargement precoce.
                    if (r == addon && ext.Equals(".addon64", StringComparison.OrdinalIgnoreCase)) name = AddonFileName;

                    // Le premier composant qui fournit un nom l'emporte : addon, runtime neural, puis la pile.
                    if (files.All(x => !x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        files.Add(new PackageFile(f, name, r));
                }
            }

            // Streamline et le runtime neural sont indispensables : sans eux, pas de DLSS 5.
            var absent = RequiredFiles
                .Where(req => files.All(f => !f.Name.Equals(req, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (absent.Count > 0)
            {
                Log.Error(Src, $"Paquet incomplet, rien n'est ecrit : {string.Join(", ", absent)}");
                return new InstallResult(false, Loc.T("dlss5.err.pack_incomplete", string.Join(", ", absent)));
            }

            // 2. Signatures : tout ou rien.
            foreach (var f in files.Where(f => f.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                // Le runtime neural des RTX 20 a 40 est repatche : signature rompue par
                // construction. Il ne passe que si son empreinte est epinglee.
                if (f.Name.Equals(NeuralRuntimeFile, StringComparison.OrdinalIgnoreCase))
                {
                    if (IsTrustedRuntime(f.Source)) continue;
                    Log.Error(Src, $"Runtime neural refuse ({f.From.Tag}) : empreinte inconnue");
                    return new InstallResult(false, Loc.T("dlss5.err.pin", f.Name));
                }

                var sig = Authenticode.Verify(f.Source);
                if (!sig.IsNvidia)
                {
                    var who = sig.Valid ? sig.Signer ?? "?" : Loc.T("sig.invalid");
                    Log.Error(Src, $"Signature refusee pour {f.Name} ({f.From.Tag}) : {who}");
                    return new InstallResult(false, Loc.T("dlss5.err.signature", f.Name, who));
                }
            }

            // 3. Pose.
            foreach (var old in others) RemoveDeployed(old);

            var written = 0;
            var placed = new List<(string Source, string Dest)>();

            foreach (var f in files)
            {
                var isAddon = f.Name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase);
                foreach (var dir in isAddon ? new[] { exeDir } : runtimeDirs)
                {
                    var dest = Path.Combine(dir, f.Name);
                    var existed = File.Exists(dest);
                    var ours = _deployments.WasDeployed(dest);

                    if (existed && !ours) _backups.Capture(game, dest, Origin);

                    DllInstaller.ClearReadOnly(dest);
                    File.Copy(f.Source, dest, overwrite: true);

                    // Un ajout, ou une nouvelle version d'un ajout : le registre suit l'empreinte reelle.
                    if (!existed || ours)
                        _deployments.Record(game, dest, ComponentOf(f.From.Tag), f.From.Version,
                            default, "", DownloadService.Sha256Cached(dest), Origin);

                    placed.Add((f.Source, dest));
                    written++;
                }
            }

            // Copies de la pile que Prism avait posees hors des dossiers de Streamline : jamais
            // chargees, elles ne font que tromper le diagnostic. Seules les notres sont retirees.
            var strays = files
                .Where(f => f.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.Combine(exeDir, f.Name))
                .Where(p => !runtimeDirs.Contains(Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase)
                            && File.Exists(p) && _deployments.WasDeployed(p))
                .ToList();
            foreach (var stray in strays)
            {
                RemoveDeployed(stray);
                Log.Info(Src, $"Copie hors Streamline retiree : {stray}");
            }

            // Un compilateur de shaders de Windows 8.1 dans le dossier du jeu fait echouer
            // l'addon (« unrecognized compiler target 'cs_5_1' »).
            if (ShaderCompiler.IsOutdated(exeDir) && ShaderCompiler.Upgrade(game, exeDir, _backups, Origin)) written++;

            var early = ReShadeConfig.EnableEarlyLoading(exeDir, AddonFileName);

            // 4. Chaque fichier pose est relu : present, et identique a la source.
            var bad = placed
                .Where(p => !File.Exists(p.Dest)
                            || !string.Equals(DownloadService.Sha256Cached(p.Dest),
                                DownloadService.Sha256Cached(p.Source), StringComparison.OrdinalIgnoreCase))
                .Select(p => Path.GetRelativePath(game.InstallDir, p.Dest))
                .ToList();

            DllDetector.Inspect(game);
            progress?.Report(100);

            if (bad.Count > 0)
            {
                Log.Error(Src, $"Verification apres pose echouee : {string.Join(", ", bad)}");
                return new InstallResult(false, Loc.T("dlss5.err.verify", string.Join(", ", bad)), written);
            }

            var where = Describe(game, runtimeDirs);
            var msg = Loc.T("dlss5.ok.installed", addon.Version, written) + " " + Loc.T("dlss5.ok.location", where);
            if (!early.Success) msg += " " + early.Message;

            Log.Info(Src, $"RenoDX DLSS 5 {addon.Version} : {written} fichier(s) poses et verifies, pile dans {where}");
            return new InstallResult(true, msg, written);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(Src, $"Installation DLSS 5 echouee : {ex.Message}");
            return new InstallResult(false, Loc.T("err.install_failed", ex.Message));
        }
    }

    private void RemoveDeployed(string path)
    {
        var entry = _deployments.All.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) _deployments.Remove(entry);
    }

    private static string ComponentOf(string tag) => tag switch
    {
        _ when tag.StartsWith(RhiRepoService.Dlss5AddonPrefix, StringComparison.OrdinalIgnoreCase) => "RenoDX DLSS 5",
        _ when tag.StartsWith("dlssnr-", StringComparison.OrdinalIgnoreCase) => "DLSS-NR",
        _ when tag.StartsWith("dlssg-", StringComparison.OrdinalIgnoreCase) => "DLSS-G",
        _ when tag.StartsWith("dlssd-", StringComparison.OrdinalIgnoreCase) => "DLSS-RR",
        _ when tag.StartsWith("dlss-", StringComparison.OrdinalIgnoreCase) => "DLSS SR",
        _ when tag.StartsWith("streamline-", StringComparison.OrdinalIgnoreCase) => "Streamline",
        _ => tag
    };
}
