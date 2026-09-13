using System.Diagnostics;
using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Chemin DLSS 5 — Neural Rendering.
///
/// Le choix de la voie depend de l'API du titre, et c'est la distinction qui compte :
///
///  - <b>DirectX 12</b> : OptiScaler execute nvngx_dlssnr.dll comme une passe
///    supplementaire apres l'upscaler. C'est la voie de reference ; ReShade n'est
///    meme plus necessaire.
///  - <b>DirectX 11 et Vulkan</b> : OptiScaler ne s'applique pas. Le pont ReShade
///    reflete l'appel DLSS natif, ou substitue un flux optique quand le titre n'a
///    pas DLSS du tout.
///
/// Dans tous les cas il faut <b>nvngx_dlssnr.dll</b>. Ce runtime n'existe dans aucun
/// catalogue verifiable : il est livre par un pilote RTX 50, ou par un addon neural
/// distribue hors depot public — et sur RTX 20/30/40 il faut meme une variante
/// repatchee. Prism le detecte et sait le copier depuis le pilote local, mais ne le
/// telecharge jamais aupres d'un tiers : aucune empreinte de reference ne permettrait
/// de l'authentifier.
/// </summary>
public sealed class Dlss5Service
{
    private const string Src = "dlss5";

    public const string BridgeRepo = "NIGos/dlss5-bridge";
    public const string OptiNrRepo = "Dagherbou/OptiScaler_DLSSNR";
    public const string MultipassRepo = "wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass";
    public const string OneClickRepo = "faisalkindi/DLSS5oneclick";

    private const string BridgeAsset = "dlss5-bridge.addon64";
    private const string NeuralRuntime = "nvngx_dlssnr.dll";

    private readonly GitHubService _github;
    private readonly DownloadService _downloads;
    private readonly BackupService _backups;
    private readonly DeploymentStore _deployments;
    private readonly RhiRepoService _rhi;
    private readonly Dlss5PackageInstaller _package;

    public Dlss5Service(GitHubService github, DownloadService downloads, BackupService backups,
        DeploymentStore deployments, RhiRepoService rhi, Dlss5PackageInstaller package)
    {
        _github = github;
        _downloads = downloads;
        _backups = backups;
        _deployments = deployments;
        _rhi = rhi;
        _package = package;
    }

    public RhiRepoService Rhi => _rhi;

    public string? BridgeVersion { get; private set; }
    public string? OptiNrVersion { get; private set; }
    public string? MultipassVersion { get; private set; }
    public string? OneClickVersion { get; private set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        BridgeVersion = (await _github.LatestAsync(BridgeRepo, ct))?.Tag;
        OptiNrVersion = (await _github.LatestAsync(OptiNrRepo, ct))?.Tag;
        MultipassVersion = (await _github.LatestAsync(MultipassRepo, ct))?.Tag;
        OneClickVersion = (await _github.LatestAsync(OneClickRepo, ct))?.Tag;
        await _rhi.LoadAsync(ct);
    }

    // ----------------------------------------------------------------- Voies

    /// <summary>
    /// Voies proposees, filtrees par l'API du titre. Une voie hors API reste visible,
    /// avec la raison : c'est ce qui evite de la chercher ailleurs.
    /// </summary>
    public List<Dlss5Option> Options(GameInfo game, GpuInfo gpu)
    {
        var api = game.Api;
        var dx12 = api is GameApi.DirectX12 or GameApi.Unknown;
        var legacyApi = api is GameApi.DirectX11 or GameApi.Vulkan;

        string? Gate(bool ok, string needs) =>
            ok ? null : Loc.T("dlss5.gate", needs, api.Label());

        var options = new List<Dlss5Option>();

        // RenoDX DLSS 5, paquet complet depuis rhi-repo. En tete sur DirectX 12 : c'est
        // la voie qui pose l'addon et toute sa pile, signatures NVIDIA verifiees.
        var addon = _rhi.Family(RhiRepoService.Dlss5AddonPrefix).FirstOrDefault();
        options.Add(new Dlss5Option
        {
            Title = "RenoDX DLSS 5",
            Description = Loc.T("dlss5.renodx.desc"),
            Backend = Dlss5Backend.RenoDxDlss5,
            Apis = new[] { GameApi.DirectX12 },
            SourceUrl = RhiRepoService.ReleasesPage,
            Version = addon?.Version,
            AutoInstall = true,
            Recommended = dx12,
            BlockedReason = Gate(dx12, "DirectX 12")
                            ?? (addon is null ? Loc.T("dlss5.renodx.offline") : null)
        });

        options.AddRange(new List<Dlss5Option>
        {
            new()
            {
                Title = "OptiScaler DLSSNR",
                Description = Loc.T("dlss5.optinr.desc"),
                Backend = Dlss5Backend.OptiScalerNr,
                Apis = new[] { GameApi.DirectX12 },
                SourceUrl = "https://github.com/Dagherbou/OptiScaler_DLSSNR",
                Version = OptiNrVersion,
                AutoInstall = true,
                BlockedReason = Gate(dx12, "DirectX 12")
            },
            new()
            {
                Title = "OptiScaler PreSR Multipass",
                Description = Loc.T("dlss5.multipass.desc"),
                Backend = Dlss5Backend.OptiScalerMultipass,
                Apis = new[] { GameApi.DirectX12 },
                SourceUrl = "https://github.com/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass",
                Version = MultipassVersion,
                AutoInstall = true,
                BlockedReason = Gate(dx12, "DirectX 12")
            },
            new()
            {
                Title = "DLSS5 One-Click",
                Description = Loc.T("dlss5.oneclick.desc"),
                Backend = Dlss5Backend.OneClick,
                Apis = new[] { GameApi.DirectX11, GameApi.DirectX12 },
                SourceUrl = "https://github.com/faisalkindi/DLSS5oneclick",
                Version = OneClickVersion,
                AutoInstall = true,
                BlockedReason = null
            },
            new()
            {
                Title = "DLSS 5 Bridge",
                Description = Loc.T("dlss5.bridge.desc"),
                Backend = Dlss5Backend.Bridge,
                Apis = new[] { GameApi.DirectX11, GameApi.Vulkan },
                SourceUrl = "https://github.com/NIGos/dlss5-bridge",
                Version = BridgeVersion,
                AutoInstall = true,
                NonDestructive = true,
                Recommended = legacyApi,
                BlockedReason = Gate(legacyApi, Loc.T("api.dx11_vulkan"))
            }
        });

        return options
            .OrderByDescending(o => o.Available)
            .ThenByDescending(o => o.Recommended)
            .ToList();
    }

    // ------------------------------------------------------------- Detection

    /// <summary>RenoDX DLSS 5 et sa pile complete, depuis rhi-repo.</summary>
    public Task<InstallResult> InstallRenoDxDlss5Async(
        GameInfo game, string? addonTag, IProgress<double>? progress = null, CancellationToken ct = default)
        => _package.InstallAsync(game, addonTag, progress, ct);

    /// <summary>Le runtime neural est-il livre par le pilote installe sur ce poste ?</summary>
    public static bool NeuralRuntimeInDriverStore() => FindNeuralRuntimeInDriverStore() is not null;

    private static string? FindNeuralRuntimeInDriverStore()
    {
        try
        {
            var store = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "DriverStore", "FileRepository");

            if (!Directory.Exists(store)) return null;

            return Directory.EnumerateDirectories(store, "nv_disp*")
                .Select(d => Path.Combine(d, NeuralRuntime))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception ex)
        {
            Log.Warn(Src, $"Inspection du driver store impossible : {ex.Message}");
            return null;
        }
    }

    /// <summary>Prerequis communs a toutes les voies, evalues sur le titre selectionne.</summary>
    public List<PrereqCheck> Preflight(GameInfo game, GpuInfo gpu, Dlss5Option? option)
    {
        var checks = new List<PrereqCheck>();

        var official = gpu.SupportsNativeMfg; // Blackwell et plus recent
        checks.Add(new PrereqCheck
        {
            Label = "GPU",
            State = official ? UiStatus.Ready : gpu.IsNvidia ? UiStatus.Warning : UiStatus.Error,
            Detail = gpu.GenerationLabel,
            Hint = official
                ? null
                : Loc.T(gpu.IsNvidia ? "dlss5.gpu.hint_older" : "dlss5.gpu.hint_nonnvidia")
        });

        checks.Add(new PrereqCheck
        {
            Label = "API",
            State = game.Api == GameApi.Unknown ? UiStatus.Warning : UiStatus.Ready,
            Detail = game.Api.Label(),
            Hint = game.Api switch
            {
                GameApi.DirectX12 => Loc.T("dlss5.api.dx12"),
                GameApi.DirectX11 or GameApi.Vulkan => Loc.T("dlss5.api.legacy"),
                _ => Loc.T("dlss5.api.unknown")
            }
        });

        // ReShade n'est requis que pour la voie pont.
        var needsReShade = option?.Backend is Dlss5Backend.Bridge or Dlss5Backend.RenoDxDlss5;
        if (needsReShade)
        {
            checks.Add(FrameGenOptions.ReShadeCheck(game, Loc.T("dlss5.reshade.hint")));
        }

        var sr = game.Dlls.FirstOrDefault(d => d.Kind == DllKind.Dlss);
        checks.Add(new PrereqCheck
        {
            Label = "DLSS SR",
            State = sr is not null ? UiStatus.Ready : UiStatus.Warning,
            Detail = sr?.Display ?? Loc.T("common.absent"),
            Fix = PrereqFix.DeployDlss,
            Hint = sr is null ? Loc.T("dlss5.sr.hint") : null
        });

        var renodx = option?.Backend == Dlss5Backend.RenoDxDlss5;

        // L'addon RenoDX charge nvngx_dlssnr.dll a cote de l'executable ; Streamline charge les DLL
        // NGX a cote de sl.interposer.dll. Absent de l'un d'eux, le runtime du pilote prend le
        // relais et echoue sur RTX 20 a 40 (0xBAD0000B).
        var runtimeDirs = Dlss5PackageInstaller.RuntimeDirectories(game);
        var neuralDirs = Dlss5PackageInstaller.NeuralRuntimeDirectories(game);
        var inPlace = neuralDirs.All(d => File.Exists(Path.Combine(d, NeuralRuntime)));
        // Une copie modifiee ou d'origine inconnue est chargee, puis echoue (0xBAD00005).
        var untrusted = renodx && inPlace && !neuralDirs.All(d => IsTrustedNeural(Path.Combine(d, NeuralRuntime)));
        var misplaced = renodx && game.HasNeuralRuntime && !inPlace;
        var inGame = renodx ? inPlace && !untrusted : game.HasNeuralRuntime;
        var inDriver = NeuralRuntimeInDriverStore();

        checks.Add(new PrereqCheck
        {
            Label = "NEURAL RT",
            State = inGame ? UiStatus.Ready
                  : misplaced || untrusted ? UiStatus.Error
                  : renodx ? UiStatus.Warning : UiStatus.Error,
            Detail = inGame ? (renodx ? Dlss5PackageInstaller.Describe(game, neuralDirs) : Loc.T("dlss5.nr.in_game"))
                   : untrusted ? Loc.T("dlss5.nr.untrusted_detail")
                   : misplaced ? Loc.T("dlss5.nr.misplaced_detail")
                   : renodx ? Loc.T("dlss5.nr.in_package")
                   : inDriver ? Loc.T("dlss5.nr.in_driver") : Loc.T("common.absent"),
            Fix = renodx ? PrereqFix.InstallDlss5Addon
                : inDriver ? PrereqFix.AdoptNeuralRuntime : PrereqFix.None,
            Hint = inGame ? null
                 : untrusted ? Loc.T("dlss5.nr.untrusted")
                 : misplaced ? Loc.T("dlss5.nr.misplaced")
                 : renodx ? Loc.T("dlss5.nr.hint_package")
                 : inDriver ? Loc.T("dlss5.nr.hint_driver") : Loc.T("dlss5.nr.hint_absent")
        });

        if (renodx)
        {
            var dir = DllInstaller.TargetDirectory(game);

            // Streamline complet, a l'endroit meme ou le jeu le charge.
            var slMissing = runtimeDirs
                .SelectMany(d => Dlss5PackageInstaller.RequiredFiles
                    .Where(r => !r.Equals(NeuralRuntime, StringComparison.OrdinalIgnoreCase)
                                && !File.Exists(Path.Combine(d, r))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var interposer = runtimeDirs.Select(d => Path.Combine(d, "sl.interposer.dll")).FirstOrDefault(File.Exists);

            checks.Add(new PrereqCheck
            {
                Label = "STREAMLINE",
                State = slMissing.Count == 0 ? UiStatus.Ready : UiStatus.Error,
                Detail = slMissing.Count == 0 && interposer is not null
                    ? $"{DllDetector.ReadProductVersion(interposer) ?? DllDetector.ReadVersion(interposer)} · {Dlss5PackageInstaller.Describe(game, runtimeDirs)}"
                    : string.Join(", ", slMissing),
                Fix = slMissing.Count == 0 ? PrereqFix.None : PrereqFix.InstallDlss5Addon,
                Hint = slMissing.Count == 0 ? null : Loc.T("dlss5.sl.hint")
            });

            // La signature du runtime en place : un runtime repatche fonctionne parfois,
            // mais plus rien ne permet de savoir d'ou il vient.
            var nr = runtimeDirs.Select(d => Path.Combine(d, NeuralRuntime)).FirstOrDefault(File.Exists);
            if (nr is not null)
            {
                // Signe par NVIDIA (RTX 50), ou build repatchee a l'empreinte connue (RTX 20-40).
                var trusted = Dlss5PackageInstaller.IsTrustedRuntime(nr);
                checks.Add(new PrereqCheck
                {
                    Label = "SIGNATURE",
                    State = trusted ? UiStatus.Ready : UiStatus.Warning,
                    Detail = trusted ? Loc.T("dlss5.sig.trusted") : Loc.T("dlss5.sig.unknown"),
                    Fix = trusted ? PrereqFix.None : PrereqFix.InstallDlss5Addon,
                    Hint = trusted ? null : Loc.T("dlss5.sig.hint")
                });
            }

            var compiler = ShaderCompiler.LocalVersion(dir);
            if (compiler is not null)
            {
                var outdated = ShaderCompiler.IsOutdated(dir);
                checks.Add(new PrereqCheck
                {
                    Label = "D3DCOMPILER",
                    State = outdated ? UiStatus.Error : UiStatus.Ready,
                    Detail = compiler,
                    Fix = outdated ? PrereqFix.InstallDlss5Addon : PrereqFix.None,
                    Hint = outdated ? Loc.T("dlss5.compiler.hint") : null
                });
            }

            var early = ReShadeConfig.IsEarlyLoaded(dir, Dlss5PackageInstaller.AddonFileName);
            checks.Add(new PrereqCheck
            {
                Label = "ADDON",
                State = game.HasDlss5Addon ? (early ? UiStatus.Ready : UiStatus.Warning) : UiStatus.Error,
                Detail = game.HasDlss5Addon ? Dlss5PackageInstaller.AddonFileName : Loc.T("common.absent"),
                Fix = PrereqFix.InstallDlss5Addon,
                Hint = game.HasDlss5Addon
                    ? (early ? null : Loc.T("dlss5.addon.hint_early"))
                    : Loc.T("dlss5.addon.hint_absent")
            });
        }

        return checks;
    }

    public static Dlss5Readiness Readiness(GameInfo game, GpuInfo gpu)
    {
        if (!gpu.IsNvidia) return Dlss5Readiness.Unsupported;
        if (game.HasNeuralRuntime && (game.HasDlss5Bridge || game.HasOptiScaler)) return Dlss5Readiness.Active;
        if (!game.HasNeuralRuntime) return Dlss5Readiness.Incomplete;
        return Dlss5Readiness.Ready;
    }

    // ---------------------------------------------------------- Installation

    public Task<InstallResult> InstallAsync(
        GameInfo game, Dlss5Option option, IProgress<double>? progress = null, CancellationToken ct = default,
        string? addonTag = null)
        => option.Backend switch
        {
            Dlss5Backend.RenoDxDlss5 => InstallRenoDxDlss5Async(game, addonTag, progress, ct),
            Dlss5Backend.Bridge => InstallBridgeAsync(game, progress, ct),
            Dlss5Backend.OptiScalerNr => InstallOptiNrAsync(game, OptiNrRepo, "OptiScaler DLSSNR", progress, ct),
            Dlss5Backend.OptiScalerMultipass => InstallOptiNrAsync(game, MultipassRepo, "OptiScaler PreSR Multipass", progress, ct),
            Dlss5Backend.OneClick => LaunchOneClickAsync(game, progress, ct),
            _ => Task.FromResult(new InstallResult(false, Loc.T("err.unknown_path")))
        };

    /// <summary>
    /// Depose le pont a cote de l'executable. Rien n'est remplace : c'est un addon
    /// ReShade, sa desinstallation se limite a le supprimer.
    /// </summary>
    public async Task<InstallResult> InstallBridgeAsync(
        GameInfo game, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var release = await _github.LatestAsync(BridgeRepo, ct);
        var asset = release?.Assets.FirstOrDefault(a =>
            a.Name.Equals(BridgeAsset, StringComparison.OrdinalIgnoreCase));

        if (release is null || asset is null)
            return new InstallResult(false, Loc.T("dlss5.bridge.not_found", BridgeAsset));

        var dir = DllInstaller.TargetDirectory(game);
        if (!Directory.Exists(dir)) return new InstallResult(false, Loc.T("err.target_missing"));

        try
        {
            var cached = Path.Combine(AppPaths.ComponentCache, "dlss5-bridge", release.Tag, asset.Name);
            await _downloads.DownloadAsync(asset.Url, cached, null, progress, ct);

            var tx = new FileTransaction(game, _backups, _deployments, null);
            tx.Copy(cached, Path.Combine(dir, asset.Name), "DLSS 5 Bridge", release.Tag);
            var written = tx.Commit();
            if (!written.Success) return written;

            BridgeVersion = release.Tag;
            DllDetector.Inspect(game);
            Log.Info(Src, $"dlss5-bridge {release.Tag} depose dans {dir}");

            var missing = new List<string>();
            if (!game.HasReShade) missing.Add(Loc.T("label.reshade_addon"));
            if (!game.HasNeuralRuntime) missing.Add(NeuralRuntime);

            var msg = Loc.T("dlss5.bridge.ok", release.Tag);
            if (missing.Count > 0) msg += " " + Loc.T("common.still_missing", string.Join(", ", missing));
            return new InstallResult(true, msg, 1);
        }
        catch (Exception ex)
        {
            Log.Error(Src, $"Installation du pont echouee : {ex.Message}");
            return new InstallResult(false, Loc.T("err.install_failed", ex.Message));
        }
    }

    /// <summary>
    /// Pose un paquet de type OptiScaler : la bibliotheque principale prend le nom
    /// d'un proxy libre, le reste du paquet l'accompagne.
    /// </summary>
    private async Task<InstallResult> InstallOptiNrAsync(
        GameInfo game, string repo, string label, IProgress<double>? progress, CancellationToken ct)
    {
        var release = await _github.LatestAsync(repo, ct);
        var asset = release?.Assets.FirstOrDefault(a =>
            (a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
             a.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) &&
            !a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase));

        if (release is null || asset is null)
            return new InstallResult(false, Loc.T("err.archive_not_found", label));

        var dir = DllInstaller.TargetDirectory(game);
        if (!Directory.Exists(dir)) return new InstallResult(false, Loc.T("err.target_missing"));

        try
        {
            var archive = Path.Combine(AppPaths.ComponentCache, "dlss5", AppPaths.Sanitize(repo), asset.Name);
            Log.Info(Src, $"Telechargement de {label} {release.Tag} ({asset.Size / 1024 / 1024} Mo)");
            await _downloads.DownloadAsync(asset.Url, archive, null, progress, ct);

            var extractDir = Path.Combine(AppPaths.ComponentCache, "dlss5", AppPaths.Sanitize(repo), release.Tag);
            if (!Directory.Exists(extractDir) || !Directory.EnumerateFileSystemEntries(extractDir).Any())
                await ArchiveExtractor.ExtractAsync(archive, extractDir, ct);

            var core = ArchiveExtractor.FindFile(extractDir, "OptiScaler.dll")
                       ?? ArchiveExtractor.FindFile(extractDir, "OptiScaler.asi");
            if (core is null) return new InstallResult(false, Loc.T("err.missing_in_archive", "OptiScaler.dll"));

            var sourceDir = Path.GetDirectoryName(core)!;
            var proxy = FrameGenService.PickProxyName(dir);
            if (proxy is null)
                return new InstallResult(false, Loc.T("err.no_proxy"));

            var tx = new FileTransaction(game, _backups, _deployments, null);
            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                var name = Path.GetFileName(file);
                // Notices, scripts et marqueurs de l'archive : rien a poser dans le jeu.
                if (!ArchiveExtractor.IsPayload(name)) continue;

                var destName = name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ? proxy : name;
                var dest = Path.Combine(dir, destName);

                // Une configuration deja ajustee ne doit pas etre ecrasee.
                if (name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && File.Exists(dest)) continue;

                tx.Copy(file, dest, label, release.Tag);
            }

            var written = tx.Commit();
            if (!written.Success) return written;
            var copied = tx.Count;

            DllDetector.Inspect(game);
            var msg = Loc.T("opti.ok", label, release.Tag, proxy, copied);
            if (!game.HasNeuralRuntime) msg += " " + Loc.T("dlss5.nr.still_needed", NeuralRuntime);

            Log.Info(Src, msg);
            return new InstallResult(true, msg, copied);
        }
        catch (Exception ex)
        {
            Log.Error(Src, $"Installation {label} echouee : {ex.Message}");
            return new InstallResult(false, Loc.T("err.install_failed", ex.Message));
        }
    }

    /// <summary>Telecharge l'installeur unique et le lance : c'est lui qui opere ensuite.</summary>
    private async Task<InstallResult> LaunchOneClickAsync(
        GameInfo game, IProgress<double>? progress, CancellationToken ct)
    {
        var release = await _github.LatestAsync(OneClickRepo, ct);
        var asset = release?.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (release is null || asset is null)
            return new InstallResult(false, Loc.T("err.installer_not_found", "DLSS5 One-Click"));

        try
        {
            var exe = Path.Combine(AppPaths.ComponentCache, "dlss5oneclick", release.Tag, asset.Name);
            await _downloads.DownloadAsync(asset.Url, exe, null, progress, ct);

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return new InstallResult(true,
                Loc.T("common.launched", "DLSS5 One-Click", release.Tag, DllInstaller.TargetDirectory(game)), 0);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, Loc.T("err.launch_failed", ex.Message));
        }
    }

    /// <summary>
    /// Runtime neural epingle, ou signe NVIDIA et intact. L'empreinte (mise en cache) passe
    /// avant la signature, plus couteuse a verifier sur un fichier de 160 Mo.
    /// </summary>
    private static bool IsTrustedNeural(string path)
        => File.Exists(path)
           && (NeuralRuntimePins.IsKnown(DownloadService.Sha256Cached(path)) || Authenticode.Verify(path).IsNvidia);

    public static InstallResult RemoveBridge(GameInfo game)
    {
        var dir = DllInstaller.TargetDirectory(game);
        var removed = new List<string>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                // Le fichier de configuration est genere par le pont : il part avec lui.
                if (!name.StartsWith("dlss5-bridge", StringComparison.OrdinalIgnoreCase)) continue;

                DllInstaller.ClearReadOnly(file);
                File.Delete(file);
                removed.Add(name);
            }
        }
        catch (Exception ex)
        {
            return new InstallResult(false, Loc.T("err.remove_failed", ex.Message));
        }

        DllDetector.Inspect(game);
        return removed.Count > 0
            ? new InstallResult(true, Loc.T("common.removed_list", string.Join(", ", removed)), removed.Count)
            : new InstallResult(false, Loc.T("dlss5.bridge.none"));
    }

    /// <summary>
    /// Copie nvngx_dlssnr.dll depuis le driver store vers le titre. Seul cas ou Prism
    /// met ce fichier en place : la source est le pilote installe, pas un tiers.
    /// </summary>
    public static InstallResult AdoptNeuralRuntime(GameInfo game)
    {
        var source = FindNeuralRuntimeInDriverStore();
        if (source is null)
            return new InstallResult(false,
                Loc.T("dlss5.adopt.absent", NeuralRuntime));

        try
        {
            var dest = Path.Combine(DllInstaller.TargetDirectory(game), NeuralRuntime);
            if (GameGuard.Check(game, new[] { dest }) is { } blocked) return blocked;

            // Copie a cote puis remplacement d'un geste : jamais de runtime a moitie ecrit.
            var temp = dest + ".prism-new";
            File.Copy(source, temp, overwrite: true);
            DllInstaller.ClearReadOnly(dest);
            File.Move(temp, dest, overwrite: true);

            DllDetector.Inspect(game);
            Log.Info(Src, $"{NeuralRuntime} copie depuis le pilote vers {dest}");
            return new InstallResult(true, Loc.T("dlss5.adopt.ok"), 1);
        }
        catch (Exception ex)
        {
            Log.Error(Src, $"Copie du runtime neural echouee : {ex.Message}");
            return new InstallResult(false, Loc.T("err.copy_failed", ex.Message));
        }
    }
}
