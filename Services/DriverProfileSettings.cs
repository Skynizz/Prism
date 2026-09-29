using Prism.Core;
using Prism.Models;

namespace Prism.Services;

/// <summary>Une valeur proposee pour un reglage du pilote. <c>null</c> : rien d'impose, le jeu ou le pilote decide.</summary>
public sealed record DriverChoice(uint? Value, string Label)
{
    /// <summary>Texte affiche par une liste qui n'a pas de modele d'element.</summary>
    public override string ToString() => Label;
}

/// <summary>Un reglage du profil NVIDIA d'un jeu : lecture et ecriture, parfois sur plusieurs cles.</summary>
public sealed class DriverSettingDef
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string LabelKey { get; init; }
    public string? HintKey { get; init; }
    public required Func<IReadOnlyList<DriverChoice>> Choices { get; init; }
    public required Func<string, uint?> Read { get; init; }
    public required Func<string, uint?, bool> Write { get; init; }
    /// <summary>Le pilote refuse l'ecriture sans droits administrateur (reglages caches).</summary>
    public bool NeedsAdmin { get; init; }
}

/// <summary>
/// Reglages par jeu du profil du pilote NVIDIA, comme l'application NVIDIA ou Profile Inspector.
/// Identifiants et valeurs : en-tete officiel NVIDIA/nvapi NvApiDriverSettings.h. Les reglages absents
/// de cet en-tete (Ultra Low Latency, Smooth Motion) sont recoupes entre le reference XML de
/// Profile Inspector (Orbmu2k) et le code de RHI.
/// </summary>
public static class DriverProfileSettings
{
    // En-tete officiel
    private const uint VsyncMode = 0x00A879CF;            // VSYNCMODE_ID
    private const uint PreRenderLimit = 0x007BA09E;       // PRERENDERLIMIT_ID
    private const uint FrameRateLimit = 0x10835002;       // FRL_FPS_ID (0 a 1023)
    private const uint PowerState = 0x1057EB71;           // PREFERRED_PSTATE_ID
    private const uint VrrAppOverride = 0x10A879CF;       // VRR_APP_OVERRIDE_ID
    private const uint VrrRequestState = 0x10A879AC;      // VRR_APP_OVERRIDE_REQUEST_STATE_ID
    private const uint SrMode = 0x10AFB768;               // NGX_DLSS_SR_MODE_ID (6 = CUSTOM)
    private const uint SrScaling = 0x10E41DF5;            // NGX_DLSS_SR_OVERRIDE_SCALING_RATIO_ID (33 a 100)
    private const uint RrOverride = 0x10E41E02;           // NGX_DLSS_RR_OVERRIDE_ID
    private const uint RrPreset = 0x10E41DF7;             // NGX_DLSS_RR_OVERRIDE_RENDER_PRESET_SELECTION_ID
    private const uint FgOverride = 0x10E41E03;           // NGX_DLSS_FG_OVERRIDE_ID
    private const uint FgPreset = 0x10E41DF1;             // NGX_DLSS_FG_OVERRIDE_RENDER_PRESET_SELECTION_ID

    // Hors en-tete, recoupes (Profile Inspector + RHI)
    private const uint UllEnabled = 0x10835000;           // Ultra Low Latency - Enabled
    private const uint UllCplState = 0x0005F543;          // Ultra Low Latency - CPL State (0 Off, 1 On, 2 Ultra)
    private const uint SmoothMotion = 0xB0D384C0;         // Smooth Motion - Enable (pilote 571.86+)

    private const uint Latest = 0x00FFFFFF;               // ..._RENDER_PRESET_Latest
    private const uint SrModeCustom = 6;                  // NGX_DLSS_SR_MODE_CUSTOM

    private static DriverChoice Default() => new(null, Loc.T("drv.default"));

    public static IReadOnlyList<DriverSettingDef> All(GpuInfo gpu, DisplayInfo display) => new List<DriverSettingDef>
    {
        // ------------------------------------------------------------------ DLSS
        new()
        {
            Id = "sr-scale", Group = "dlss", LabelKey = "drv.sr_scale", HintKey = "drv.sr_scale_hint",
            Choices = () => new[] { Default(), new DriverChoice(100, "100 % · DLAA"), new DriverChoice(77, "77 %"), new DriverChoice(67, "67 % · Quality"),
                                    new DriverChoice(58, "58 % · Balanced"), new DriverChoice(50, "50 % · Performance"), new DriverChoice(33, "33 % · Ultra Performance") },
            Read = exe => NvDriverSettings.ReadOwn(exe, SrMode) == SrModeCustom ? NvDriverSettings.ReadOwn(exe, SrScaling) : null,
            Write = (exe, v) => v is null ? NvDriverSettings.Clear(exe, SrMode, SrScaling)
                                          : NvDriverSettings.Write(exe, (SrMode, SrModeCustom), (SrScaling, v.Value))
        },
        new()
        {
            Id = "rr-preset", Group = "dlss", LabelKey = "drv.rr_preset", HintKey = "drv.rr_preset_hint",
            Choices = () => new[] { Default(), new DriverChoice(Latest, "Latest"), new DriverChoice(4, "D"), new DriverChoice(5, "E") },
            Read = exe => NvDriverSettings.ReadOwn(exe, RrOverride) == 1 ? NvDriverSettings.ReadOwn(exe, RrPreset) : null,
            Write = (exe, v) => v is null ? NvDriverSettings.Clear(exe, RrOverride, RrPreset)
                                          : NvDriverSettings.Write(exe, (RrOverride, 1), (RrPreset, v.Value))
        },
        new()
        {
            Id = "fg-preset", Group = "dlss", LabelKey = "drv.fg_preset", HintKey = "drv.fg_preset_hint",
            Choices = () => new[] { Default(), new DriverChoice(Latest, "Latest"), new DriverChoice(1, "A"), new DriverChoice(2, "B") },
            Read = exe => NvDriverSettings.ReadOwn(exe, FgOverride) == 1 ? NvDriverSettings.ReadOwn(exe, FgPreset) : null,
            Write = (exe, v) => v is null ? NvDriverSettings.Clear(exe, FgOverride, FgPreset)
                                          : NvDriverSettings.Write(exe, (FgOverride, 1), (FgPreset, v.Value))
        },

        // --------------------------------------------------------------- Latence
        new()
        {
            // Comme l'application NVIDIA : On = 1 image pre-rendue ; Ultra = en plus le chemin Ultra Low Latency.
            Id = "latency", Group = "latency", LabelKey = "drv.latency", HintKey = "drv.latency_hint", NeedsAdmin = true,
            Choices = () => new[] { Default(), new DriverChoice(0, Loc.T("drv.off")), new DriverChoice(1, Loc.T("drv.on")), new DriverChoice(2, "Ultra") },
            Read = exe => NvDriverSettings.ReadOwn(exe, UllCplState)
                          ?? (NvDriverSettings.ReadOwn(exe, PreRenderLimit) is 1 ? 1u : null),
            Write = (exe, v) => v switch
            {
                null => NvDriverSettings.Clear(exe, PreRenderLimit, UllEnabled, UllCplState),
                0 => NvDriverSettings.Write(exe, (PreRenderLimit, 0), (UllEnabled, 0), (UllCplState, 0)),
                1 => NvDriverSettings.Write(exe, (PreRenderLimit, 1), (UllEnabled, 0), (UllCplState, 1)),
                _ => NvDriverSettings.Write(exe, (PreRenderLimit, 1), (UllEnabled, 1), (UllCplState, 2))
            }
        },
        new()
        {
            Id = "vsync", Group = "latency", LabelKey = "drv.vsync", HintKey = "drv.vsync_hint",
            Choices = () => new[] { Default(), new DriverChoice(0x60925292, Loc.T("drv.vsync_app")), new DriverChoice(0x08416747, Loc.T("drv.off")),
                                    new DriverChoice(0x47814940, Loc.T("drv.on")), new DriverChoice(0x18888888, Loc.T("drv.vsync_fast")) },
            Read = exe => NvDriverSettings.ReadOwn(exe, VsyncMode),
            Write = (exe, v) => v is null ? NvDriverSettings.Clear(exe, VsyncMode) : NvDriverSettings.Write(exe, (VsyncMode, v.Value))
        },
        new()
        {
            Id = "fps-limit", Group = "latency", LabelKey = "drv.fps_limit", HintKey = "drv.fps_limit_hint",
            Choices = () =>
            {
                var list = new List<DriverChoice> { Default() };
                // Avec G-SYNC, quelques images sous la frequence de l'ecran evitent de sortir de la plage VRR.
                if (display.RefreshHz > 60)
                    list.Add(new DriverChoice((uint)(display.RefreshHz - 3), Loc.T("drv.fps_gsync", display.RefreshHz - 3)));
                foreach (var f in new uint[] { 30, 60, 90, 120, 144, 165, 240 })
                    if (f < display.RefreshHz || display.RefreshHz == 0) list.Add(new DriverChoice(f, $"{f} FPS"));
                return list;
            },
            Read = exe => NvDriverSettings.ReadOwn(exe, FrameRateLimit) is { } v and > 0 ? v : null,
            Write = (exe, v) => v is null ? NvDriverSettings.Clear(exe, FrameRateLimit) : NvDriverSettings.Write(exe, (FrameRateLimit, v.Value))
        },

        // ------------------------------------------- Generation d'images du pilote
        new()
        {
            Id = "smooth-motion", Group = "fg", LabelKey = "drv.smooth_motion", HintKey = "drv.smooth_motion_hint", NeedsAdmin = true,
            Choices = () => new[] { Default(), new DriverChoice(0, Loc.T("drv.off")), new DriverChoice(1, Loc.T("drv.on")) },
            Read = exe => NvDriverSettings.ReadOwn(exe, SmoothMotion),
            Write = (exe, v) => v is null ? NvDriverSettings.Clear(exe, SmoothMotion) : NvDriverSettings.Write(exe, (SmoothMotion, v.Value))
        },

        // ------------------------------------------------ Energie et affichage
        new()
        {
            Id = "power", Group = "power", LabelKey = "drv.power", HintKey = "drv.power_hint",
            Choices = () => new[] { Default(), new DriverChoice(5, Loc.T("drv.power_optimal")), new DriverChoice(0, Loc.T("drv.power_adaptive")),
                                    new DriverChoice(1, Loc.T("drv.power_max")) },
            Read = exe => NvDriverSettings.ReadOwn(exe, PowerState),
            Write = (exe, v) => v is null ? NvDriverSettings.Clear(exe, PowerState) : NvDriverSettings.Write(exe, (PowerState, v.Value))
        },
        new()
        {
            Id = "gsync", Group = "power", LabelKey = "drv.gsync", HintKey = "drv.gsync_hint",
            Choices = () => new[] { Default(), new DriverChoice(0, Loc.T("drv.gsync_allow")), new DriverChoice(1, Loc.T("drv.gsync_off")),
                                    new DriverChoice(2, Loc.T("drv.gsync_disallow")) },
            Read = exe => NvDriverSettings.ReadOwn(exe, VrrAppOverride),
            Write = (exe, v) => v is null ? NvDriverSettings.Clear(exe, VrrAppOverride, VrrRequestState)
                                          : NvDriverSettings.Write(exe, (VrrAppOverride, v.Value), (VrrRequestState, v.Value))
        },
    }.Where(d => d.Id != "smooth-motion" || gpu.SupportsNativeFg && DriverAtLeast(gpu, 571.86)).ToList();

    /// <summary>Tous les reglages de cette page effaces du profil : le pilote reprend la main.</summary>
    public static bool ClearAll(string exe) => NvDriverSettings.Clear(exe,
        VsyncMode, PreRenderLimit, FrameRateLimit, PowerState, VrrAppOverride, VrrRequestState, SrMode, SrScaling,
        RrOverride, RrPreset, FgOverride, FgPreset, UllEnabled, UllCplState, SmoothMotion);

    /// <summary>Smooth Motion exige le pilote 571.86 ou plus (Profile Inspector, MinRequiredDriverVersion).</summary>
    private static bool DriverAtLeast(GpuInfo gpu, double version)
        => double.TryParse((gpu.DriverBranch ?? gpu.DriverVersion).Split(' ')[0], System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= version;
}
