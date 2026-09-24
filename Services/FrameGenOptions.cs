using Prism.Models;
using Prism.Core;

namespace Prism.Services;

/// <summary>
/// Construit les voies de generation d'images proposees pour un couple (GPU, titre),
/// et evalue leurs prerequis de fichiers.
///
/// Deux familles, qu'il ne faut pas confondre :
///
///  - <b>DLSS-G natif</b> : le vrai moteur NVIDIA. Officiel sur Ada et Blackwell ;
///    porte sur Ampere (sm_86) et Turing (sm_75) par des projets qui recompilent les
///    noyaux d'origine en conservant les modeles.
///  - <b>Pont FSR 3.1</b> : OptiScaler et DLSS Enabler interceptent l'appel DLSS-G et
///    le reroutent vers FidelityFX. Le resultat n'est pas le meme moteur.
///
/// Les debrideurs MFG ne fabriquent rien : ils levent une limite. Ils exigent donc
/// que la pile du titre soit deja la bonne — c'est l'objet des prerequis.
/// </summary>
public static class FrameGenOptions
{
    // Depots des portages natifs pour les generations non supportees officiellement.
    public const string Sm86Repo = "sdli1995/dlssg_for_sm86";
    public const string Sm75Repo = "Coldwood1026/dlssg_for_sm75";
    public const string Sm86Url = "https://github.com/sdli1995/dlssg_for_sm86";
    public const string Sm75Url = "https://github.com/Coldwood1026/dlssg_for_sm75";

    /// <summary>Version de DLSS-G exigee par le mode MFG dynamique de MFGAdaUnlock.</summary>
    private const string DynamicMfgDlssG = "310.9.1";

    public static List<FgOption> Build(GpuInfo gpu, GameInfo game)
    {
        var fgCapable = game.HasDlssG || game.HasStreamline;
        var noFg = fgCapable
            ? null
            : Loc.T("fg.block.no_fg");

        var dx12 = game.Api is GameApi.DirectX12 or GameApi.Unknown;
        var notDx12 = dx12 ? null : Loc.T("fg.block.dx12", game.Api.Label());

        var isAda = gpu.Generation == GpuGeneration.AdaLovelace;
        var isAmpere = gpu.Generation == GpuGeneration.Ampere;
        var isTuring = gpu.Generation == GpuGeneration.Turing;

        var options = new List<FgOption>
        {
            NativeDriver(gpu, noFg),
            MfgAdaUnlock(gpu, game, isAda, noFg ?? notDx12),
            Rtx40MfgUnlock(gpu, game, isAda, isAmpere, noFg ?? notDx12),
            DlssgSm86(gpu, game, isAmpere, noFg ?? notDx12),
            DlssgSm75(gpu, game, isTuring, noFg ?? notDx12),
            // OptiScaler : un seul paquet, deux sorties. FSR FG sur toute carte (depuis le DLSS-G du
            // jeu, ou depuis son upscaler s'il n'en a pas) ; vrai DLSS-G injecte sur RTX 40/50.
            OptiScaler(game, fgCapable),
            InjectedDlssG(gpu, game, fgCapable),
            DlssEnabler(noFg)
        };

        return options
            .OrderByDescending(o => o.Available)
            .ThenByDescending(o => o.Recommended)
            .ThenByDescending(o => o.Native)
            .ThenByDescending(o => o.NonDestructive)
            .ToList();
    }

    // ------------------------------------------------------------- Officiel

    /// <summary>
    /// Prerequis ReShade commun aux voies : pret seulement pour un ReShade add-on 6.8+ charge
    /// une seule fois. Une version standard ou ancienne se remplace sur place ; un doublon bloque.
    /// </summary>
    public static PrereqCheck ReShadeCheck(GameInfo game, string absentHint)
    {
        var state = ReShadeLocator.Scan(game);
        return new PrereqCheck
        {
            Label = "RESHADE",
            State = state.Ready ? UiStatus.Ready
                : state.IsDuplicate || !state.Present ? UiStatus.Error
                : UiStatus.Warning,
            Detail = state.Label,
            Fix = PrereqFix.InstallReShade,
            Hint = state.Ready ? null
                : state.IsDuplicate ? Loc.T("reshade.hint.duplicate")
                : state.Present ? Loc.T("reshade.hint.upgrade")
                : absentHint
        };
    }

    private static FgOption NativeDriver(GpuInfo gpu, string? blocked) => new()
    {
        Title = Loc.T("fg.kind.native"),
        Description = gpu.SupportsNativeMfg
            ? Loc.T("fg.native.desc_mfg")
            : Loc.T("fg.native.desc_fg"),
        Backend = FgBackend.NativeDlssG,
        MaxMultiplier = gpu.NativeMaxMultiplier,
        Multipliers = gpu.SupportsNativeMfg ? new[] { 2, 3, 4 } : new[] { 2 },
        Method = Loc.T("method.driver"),
        Native = true,
        NonDestructive = true,
        Recommended = gpu.SupportsNativeMfg,
        Apis = new[] { GameApi.DirectX12, GameApi.Vulkan },
        BlockedReason = blocked ?? (gpu.SupportsNativeFg
            ? null
            : Loc.T("fg.block.ada", gpu.GenerationLabel))
    };

    // ------------------------------------------------------- Ada : debridage

    /// <summary>
    /// RenoDX MFG Unlock ne fournit pas la generation d'images : il debride les
    /// multiplicateurs d'une pile DLSS-G deja en place. D'ou des prerequis stricts,
    /// et un mode dynamique qui exige des versions exactes.
    ///
    /// Le patch s'applique en memoire — aucun fichier du titre n'est <i>remplace</i> —
    /// mais l'installation exige bel et bien des fichiers : l'addon <c>.addon64</c>,
    /// ReShade en variante Addon, un <c>nvngx_dlssg.dll</c> en 310.x, et le paquet
    /// Streamline complet qui va avec. Sans eux, le titre n'a pas de multi-images.
    /// </summary>
    private static FgOption MfgAdaUnlock(
        GpuInfo gpu, GameInfo game, bool isAda, string? blocked)
    {
        var addonPresent = game.HasMfgUnlock;
        var dlssG = game.Dlls.FirstOrDefault(d => d.Kind == DllKind.DlssG);
        var dlssGVersion = dlssG?.Display;
        var dlssGOk = dlssGVersion is { } v && v.StartsWith("310.", StringComparison.Ordinal);
        var dynamicOk = dlssGVersion is { } dv && dv.StartsWith(DynamicMfgDlssG, StringComparison.Ordinal);

        var slVersion = game.StreamlineVersion;
        var slDynamicOk = StreamlineService.Matches(game, StreamlineService.DynamicMfgVersion);

        var reqs = new List<PrereqCheck>
        {
            // Premier de la liste, parce que c'est le fichier sans lequel rien de ce
            // qui suit ne sert : l'addon lui-meme.
            new()
            {
                Label = "ADDON",
                State = addonPresent ? UiStatus.Ready : UiStatus.Error,
                Detail = addonPresent ? "renodx-mfgunlock.addon64" : Loc.T("common.absent"),
                Fix = PrereqFix.InstallMfgAddon,
                Hint = addonPresent ? null : Loc.T("fg.mfg.addon_hint")
            },
            new()
            {
                Label = "DLSS-G",
                State = dynamicOk ? UiStatus.Ready : dlssGOk ? UiStatus.Warning : UiStatus.Error,
                Detail = dlssGVersion ?? Loc.T("common.absent"),
                Fix = PrereqFix.DeployDlssG,
                Hint = dynamicOk
                    ? null
                    : dlssGOk
                        ? Loc.T("fg.mfg.dlssg_dynamic", DynamicMfgDlssG)
                        : Loc.T("fg.mfg.dlssg_required")
            },
            new()
            {
                Label = "STREAMLINE",
                State = slDynamicOk ? UiStatus.Ready : game.HasStreamline ? UiStatus.Warning : UiStatus.Error,
                Detail = slVersion ?? (game.HasStreamline ? Loc.T("common.unknown_version") : Loc.T("common.absent")),
                Fix = PrereqFix.DeployStreamline,
                Hint = slDynamicOk
                    ? null
                    : game.HasStreamline
                        ? Loc.T("fg.mfg.sl_dynamic", StreamlineService.DynamicMfgVersion)
                        : Loc.T("fg.mfg.sl_absent")
            },
            new()
            {
                Label = Loc.T("label.driver"),
                State = UiStatus.Ready,
                Detail = gpu.DriverBranch ?? gpu.DriverVersion,
                Hint = Loc.T("fg.mfg.driver_hint")
            },
            ReShadeCheck(game, Loc.T("fg.mfg.reshade_hint"))
        };

        return new FgOption
        {
            Title = "RenoDX MFG Unlock",
            Description = Loc.T("fg.mfg.desc"),
            Backend = FgBackend.MfgAdaUnlock,
            MaxMultiplier = 6,
            Multipliers = new[] { 2, 3, 4, 6 },
            Method = Loc.T("method.addon"),
            Native = true,
            NonDestructive = true,
            Experimental = true,
            Recommended = isAda,
            Apis = new[] { GameApi.DirectX12 },
            Requirements = reqs,
            BlockedReason = blocked ?? (isAda
                ? null
                : Loc.T("fg.block.rtx40", gpu.GenerationLabel))
        };
    }

    private static FgOption Rtx40MfgUnlock(GpuInfo gpu, GameInfo game, bool isAda, bool isAmpere, string? blocked)
    {
        var dlssG = game.Dlls.FirstOrDefault(d => d.Kind == DllKind.DlssG);
        var proxyFree = FrameGenService.PickProxyName(FrameGenService.TargetDir(game)) is not null;

        var reqs = new List<PrereqCheck>
        {
            new()
            {
                Label = "DLSS-G",
                State = dlssG is not null ? UiStatus.Ready : UiStatus.Error,
                Detail = dlssG?.Display ?? Loc.T("common.absent"),
                Fix = PrereqFix.DeployDlssG,
                Hint = dlssG is null ? Loc.T("fg.rtx40.dlssg_hint") : null
            },
            new()
            {
                Label = "PROXY",
                State = proxyFree ? UiStatus.Ready : UiStatus.Error,
                Detail = proxyFree ? Loc.T("fg.proxy.free") : Loc.T("fg.proxy.taken"),
                Hint = proxyFree ? null : Loc.T("fg.proxy.hint")
            }
        };

        return new FgOption
        {
            Title = "RTX40MFG-Unlock",
            Description = Loc.T("fg.rtx40.desc"),
            Backend = FgBackend.Rtx40MfgUnlock,
            MaxMultiplier = 6,
            Multipliers = new[] { 2, 3, 4, 5, 6 },
            Method = Loc.T("method.proxy"),
            Native = true,
            Experimental = true,
            Apis = new[] { GameApi.DirectX12 },
            Requirements = reqs,
            BlockedReason = blocked ?? (isAda || isAmpere
                ? null
                : Loc.T("fg.block.rtx40_30", gpu.GenerationLabel))
        };
    }

    // --------------------------------------------- Ampere et Turing : portage

    /// <summary>
    /// Portage du moteur DLSS-G d'origine vers Ampere. Ce n'est pas un pont FSR : les
    /// modeles et le pipeline NVIDIA sont conserves, recompiles pour sm_86.
    /// </summary>
    private static FgOption DlssgSm86(GpuInfo gpu, GameInfo game, bool isAmpere, string? blocked) => new()
    {
        Title = "dlssg for sm_86",
        Description = Loc.T("fg.sm86.desc"),
        Backend = FgBackend.DlssgSm86,
        MaxMultiplier = 4,
        Multipliers = new[] { 2, 3, 4 },
        Method = Loc.T("method.proxy_recompiled"),
        Native = true,
        Experimental = true,
        AutoInstall = false,
        SourceUrl = Sm86Url,
        Recommended = isAmpere,
        Apis = new[] { GameApi.DirectX12 },
        Requirements = SmRequirements(game, "310.1"),
        BlockedReason = blocked ?? (isAmpere
            ? null
            : Loc.T("fg.block.rtx30", gpu.GenerationLabel))
    };

    private static FgOption DlssgSm75(GpuInfo gpu, GameInfo game, bool isTuring, string? blocked) => new()
    {
        Title = "dlssg for sm_75",
        Description = Loc.T("fg.sm75.desc"),
        Backend = FgBackend.DlssgSm75,
        MaxMultiplier = 4,
        Multipliers = new[] { 2, 3, 4 },
        Method = Loc.T("method.proxy_recompiled"),
        Native = true,
        Experimental = true,
        AutoInstall = false,
        SourceUrl = Sm75Url,
        Recommended = isTuring,
        Apis = new[] { GameApi.DirectX12 },
        Requirements = SmRequirements(game, "310.1"),
        BlockedReason = blocked ?? (isTuring
            ? null
            : Loc.T("fg.block.rtx20", gpu.GenerationLabel))
    };

    private static List<PrereqCheck> SmRequirements(GameInfo game, string model)
    {
        var dlssG = game.Dlls.FirstOrDefault(d => d.Kind == DllKind.DlssG);
        var proxyFree = FrameGenService.PickProxyName(FrameGenService.TargetDir(game)) is not null;

        return new List<PrereqCheck>
        {
            new()
            {
                Label = "DLSS-G",
                State = dlssG is not null ? UiStatus.Ready : UiStatus.Error,
                Detail = dlssG?.Display ?? Loc.T("common.absent"),
                Fix = PrereqFix.DeployDlssG,
                Hint = dlssG is null
                    ? Loc.T("fg.port.dlssg_hint", model)
                    : null
            },
            new()
            {
                Label = "API",
                State = game.Api == GameApi.DirectX12 ? UiStatus.Ready : UiStatus.Warning,
                Detail = game.Api.Label(),
                Hint = game.Api == GameApi.DirectX12 ? null : Loc.T("fg.port.api_hint")
            },
            new()
            {
                Label = "PROXY",
                State = proxyFree ? UiStatus.Ready : UiStatus.Error,
                Detail = proxyFree ? Loc.T("fg.proxy.free") : Loc.T("fg.proxy.taken"),
                Hint = proxyFree ? null : Loc.T("fg.proxy.hint")
            },
            new()
            {
                Label = Loc.T("label.binaries"),
                State = UiStatus.Warning,
                Detail = Loc.T("fg.port.no_release_detail"),
                Hint = Loc.T("fg.port.no_release_hint")
            }
        };
    }

    // ---------------------------------------------------------- Ponts FSR 3.1

    /// <summary>
    /// OptiScaler, sortie FSR FG en x2 : depuis le DLSS-G du jeu s'il en a un, sinon depuis son
    /// upscaler (OptiFG). Les notes de la 0.9.4 reservent le MFG de XeFG aux cartes Arc, et
    /// FSR-FG ne genere qu'une image : afficher x3 ou x4 serait mentir.
    /// </summary>
    private static FgOption OptiScaler(GameInfo game, bool fgCapable) => new()
    {
        Title = "OptiScaler · FSR FG",
        Description = Loc.T(fgCapable ? "fg.opti.desc" : "fg.optifg.desc"),
        Backend = FgBackend.OptiScaler,
        MaxMultiplier = 2,
        Multipliers = new[] { 2 },
        Method = Loc.T("method.proxy"),
        Native = false,
        Experimental = true,
        SourceUrl = "https://github.com/" + FrameGenService.OptiScalerRepo,
        Apis = new[] { GameApi.DirectX12 },
        Requirements = fgCapable ? Array.Empty<PrereqCheck>() : NoFgRequirements(game, hags: false)
    };

    // ------------------------------------------------------- Jeux sans FG

    /// <summary>
    /// Ce qu'OptiScaler sait intercepter pour fabriquer des images : un upscaler temporel du jeu
    /// (DLSS 2+, FSR 2+, XeSS). Sans lui, ni vecteurs de mouvement ni profondeur.
    /// </summary>
    public static bool HasUpscalerInputs(GameInfo game)
    {
        if (game.Dlls.Any(d => d.Kind is DllKind.Dlss or DllKind.FsrDx12 or DllKind.XeSS)) return true;
        var dir = FrameGenService.TargetDir(game);
        return new[] { "ffx_fsr2_api_dx12_x64.dll", "ffx_fsr2_api_x64.dll", "amd_fidelityfx_upscaler_dx12.dll" }
            .Any(n => File.Exists(Path.Combine(dir, n)));
    }

    /// <summary>Planification GPU materielle (HAGS), exigee par DLSS-G : HwSchMode vaut 2 quand elle est active.</summary>
    public static bool? HagsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            return key?.GetValue("HwSchMode") is int mode ? mode == 2 : null;
        }
        catch { return null; }
    }

    private static List<PrereqCheck> NoFgRequirements(GameInfo game, bool hags)
    {
        var upscaler = HasUpscalerInputs(game);
        var proxyFree = FrameGenService.PickProxyName(FrameGenService.TargetDir(game)) is not null;
        var list = new List<PrereqCheck>
        {
            new()
            {
                Label = "UPSCALER",
                State = upscaler ? UiStatus.Ready : UiStatus.Error,
                Detail = upscaler ? Loc.T("fg.nofg.upscaler_ok") : Loc.T("common.absent"),
                Hint = upscaler ? Loc.T("fg.nofg.upscaler_on") : Loc.T("fg.nofg.upscaler_hint")
            },
            new()
            {
                Label = "API",
                State = game.Api == GameApi.DirectX12 ? UiStatus.Ready : UiStatus.Error,
                Detail = game.Api.Label(),
                Hint = game.Api == GameApi.DirectX12 ? null : Loc.T("fg.port.api_hint")
            },
            new()
            {
                Label = "PROXY",
                State = proxyFree ? UiStatus.Ready : UiStatus.Error,
                Detail = proxyFree ? Loc.T("fg.proxy.free") : Loc.T("fg.proxy.taken"),
                Hint = proxyFree ? null : Loc.T("fg.proxy.hint")
            }
        };
        if (hags)
        {
            var on = HagsEnabled();
            list.Add(new PrereqCheck
            {
                Label = "HAGS",
                State = on == true ? UiStatus.Ready : UiStatus.Warning,
                Detail = on switch { true => Loc.T("common.on"), false => Loc.T("common.off"), _ => Loc.T("common.unknown") },
                // Valeur absente : Windows applique son defaut, qu'on ne peut pas lire ici. On le dit.
                Hint = on switch { true => null, false => Loc.T("fg.inj.hags_hint"), _ => Loc.T("fg.inj.hags_check") }
            });
        }
        return list;
    }

    /// <summary>
    /// Le vrai DLSS-G de NVIDIA dans un jeu qui n'a qu'un upscaler (fork wilsjo2, FGOutput=dlssg).
    /// Runtime NVIDIA non modifie : x2 sur RTX 40, MFG sur RTX 50 — la notice du fork le precise,
    /// et ne promet pas le MFG sur RTX 40. Chemin qualifie d'experimental par son auteur.
    /// </summary>
    private static FgOption InjectedDlssG(GpuInfo gpu, GameInfo game, bool fgCapable) => new()
    {
        Title = "OptiScaler · DLSS FG",
        Description = Loc.T("fg.inj.desc"),
        Backend = FgBackend.InjectedDlssG,
        MaxMultiplier = gpu.SupportsNativeMfg ? 4 : 2,
        Multipliers = gpu.SupportsNativeMfg ? new[] { 2, 3, 4 } : new[] { 2 },
        Method = Loc.T("method.proxy"),
        Native = true,
        Experimental = true,
        Recommended = !fgCapable && gpu.SupportsNativeFg,
        SourceUrl = "https://github.com/" + FrameGenService.InjectedFgRepo,
        Apis = new[] { GameApi.DirectX12 },
        Requirements = NoFgRequirements(game, hags: true),
        BlockedReason = fgCapable ? Loc.T("fg.nofg.has_fg")
            : !gpu.SupportsNativeFg ? Loc.T("fg.block.ada", gpu.GenerationLabel)
            : null
    };


    private static FgOption DlssEnabler(string? blocked) => new()
    {
        Title = "DLSS Enabler",
        Description = Loc.T("fg.enabler.desc"),
        Backend = FgBackend.DlssEnabler,
        MaxMultiplier = 4,
        Multipliers = new[] { 2, 3, 4 },
        Method = Loc.T("method.external"),
        Native = false,
        Experimental = true,
        Apis = new[] { GameApi.DirectX12 },
        BlockedReason = blocked
    };
}
