using System.Collections.ObjectModel;
using System.Diagnostics;
using Prism.Core;
using Prism.Models;
using Prism.Services;

namespace Prism.ViewModels;

/// <summary>
/// Tout ce que Prism sait et peut faire sur un jeu : bibliotheques, generation
/// d'images, HDR, post-traitement, et l'etat d'injection qui en resulte.
/// </summary>
public sealed class GameDetailViewModel : ObservableObject
{
    private const string Src = "profile";

    private readonly AppServices _svc;
    private readonly Action<string, bool> _notify;

    public GameDetailViewModel(GameInfo game, AppServices services, Action<string, bool> notify)
    {
        Game = game;
        _svc = services;
        _notify = notify;
        Profile = services.Profiles.Get(game.Id);

        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        RefreshCommand = new RelayCommand(_ => Refresh());
        RestoreEverythingCommand = new RelayCommand(_ => RestoreEverything());

        // Un seul bouton Installer : prerequis resolus et telecharges, puis la voie.
        InstallFgCommand = new AsyncRelayCommand(PrepareFgAsync, () => SelectedFg?.Available == true && !Busy);
        RemoveFgCommand = new RelayCommand(_ => RemoveFg());
        PickMultiplierCommand = new RelayCommand(p => SetMultiplier(p));

        InstallRenoDxCommand = new AsyncRelayCommand(PrepareHdrAsync, () => HdrPlan?.CanInstall == true && !Busy);
        RemoveRenoDxCommand = new RelayCommand(_ => RemoveHdr(), _ => Game.HasRenoDx);
        OpenHdrPageCommand = new RelayCommand(_ => OpenUrl(HdrPageUrl));

        InstallReShadeCommand = new AsyncRelayCommand(InstallReShadeAsync, () => !Busy);
        RemoveReShadeCommand = new RelayCommand(_ => RemoveReShade(), _ => Game.HasReShade);

        ResetProfileCommand = new RelayCommand(_ => ResetProfile());

        InstallDlss5Command = new AsyncRelayCommand(PrepareDlss5Async,
            () => SelectedDlss5?.Available == true && !Busy);
        RemoveBridgeCommand = new RelayCommand(_ => RemoveBridge(), _ => Game.HasDlss5Bridge);
        AdoptNeuralCommand = new RelayCommand(_ => AdoptNeural(), _ => !Game.HasNeuralRuntime);
        DeployStreamlineCommand = new AsyncRelayCommand(DeployStreamlineAsync, () => !Busy);
        OpenSourceCommand = new RelayCommand(p => OpenUrl(p as string));
        FixPrereqCommand = new AsyncRelayCommand(p => ApplyFixAsync(p as PrereqCheck), _ => !Busy);
        RemoveInstalledCommand = new RelayCommand(p => RemoveInstalled(p as InstalledGroup), _ => !Busy);
        QuarantineDetectedCommand = new RelayCommand(p => QuarantineDetected(p as DetectedMod), _ => !Busy);
        ApplyOptiProfileCommand = new RelayCommand(p => ApplyOptiProfile(p), _ => !Busy);
        RevertGameCommand = new RelayCommand(_ => RevertGame());
        RunDiagnosisCommand = new RelayCommand(_ => RunDiagnosis());
        ApplyDiagnosisFixCommand = new AsyncRelayCommand(p => ApplyDiagnosisFixAsync(p), _ => !Busy);
        OpenGameLogCommand = new RelayCommand(_ => OpenUrl(Diagnosis.LogPath));
        ApplyMfgSettingsCommand = new RelayCommand(_ => ApplyMfgSettings(), _ => Game.HasReShade);
        EarlyLoadCommand = new RelayCommand(p => EnableEarlyLoad(p as string), _ => Game.HasReShade);
        VerifyCommand = new RelayCommand(_ => VerifyIntegrity());
        // Un CommandParameter XAML arrive en chaine : on accepte les deux formes.
        RestoreVanillaCommand = new RelayCommand(p => RestoreVanilla(
            p is bool b ? b : p is string s && bool.TryParse(s, out var v) && v));

        Build();
    }

    public GameInfo Game { get; }
    public GameProfile Profile { get; private set; }

    // ------------------------------------------------------------ Identite

    public string PlatformLabel => Game.Platform switch
    {
        GamePlatform.Steam => "Steam",
        GamePlatform.Epic => "Epic Games",
        GamePlatform.Gog => "GOG",
        GamePlatform.Xbox => "Microsoft Store",
        GamePlatform.EaApp => "EA App",
        GamePlatform.Ubisoft => "Ubisoft Connect",
        GamePlatform.BattleNet => "Battle.net",
        GamePlatform.Manual => Loc.T("platform.manual"),
        _ => Loc.T("common.unknown")
    };

    public string EngineLabel => Game.Engine ?? Loc.T("common.unidentified");
    public string ApiLabel => Game.Api.Label();
    public string ExecutableName => Path.GetFileName(Game.Executable ?? "") is { Length: > 0 } n ? n : Loc.T("common.not_found");
    public string TargetDir => FrameGenService.TargetDir(Game);

    public bool IsRunning => _svc.RunningGameId == Game.Id;

    /// <summary>Etat global du jeu, tel qu'affiche par le point de statut.</summary>
    public UiStatus Status =>
        IsRunning ? UiStatus.Active
        : Game.HasDlss5 ? UiStatus.Injected
        : Game.HasMfgUnlock || Game.HasOptiScaler || Game.HasRenoDx ? UiStatus.Injected
        : Game.HasDlss || Game.HasDlssG ? UiStatus.Ready
        : UiStatus.Detected;

    // ----------------------------------------------------- Bibliotheques

    public ObservableCollection<DllSlotViewModel> Slots { get; } = new();

    public DllSlotViewModel? Sr => Slots.FirstOrDefault(s => s.Kind == DllKind.Dlss);
    public DllSlotViewModel? Fg => Slots.FirstOrDefault(s => s.Kind == DllKind.DlssG);
    public DllSlotViewModel? Nr => Slots.FirstOrDefault(s => s.Kind == DllKind.DlssD);

    // --------------------------------------------------- Frame generation

    public ObservableCollection<FgOption> FgOptions { get; } = new();

    private FgOption? _selectedFg;
    public FgOption? SelectedFg
    {
        get => _selectedFg;
        set
        {
            if (!Set(ref _selectedFg, value)) return;
            Log.Trace("fg", $"path -> {value?.Title ?? "(null)"}");
            InstallFgCommand.Raise();
            OnPropertyChanged(nameof(FgRequirements));
            OnPropertyChanged(nameof(FgHasRequirements));
            OnPropertyChanged(nameof(FgRequirementsLabel));
            SyncMultipliers();
        }
    }

    public ObservableCollection<int> Multipliers { get; } = new();

    private int _multiplier = 2;
    public int Multiplier
    {
        get => _multiplier;
        set
        {
            if (!Set(ref _multiplier, value)) return;
            Profile.FgMultiplier = value;
            _svc.Profiles.Update(Profile);
            OnPropertyChanged(nameof(MultiplierLabel));
            BuildPipeline();

            // Si un paquet OptiScaler est en place, le multiplicateur devient effectif
            // plutot que purement declaratif.
            if (File.Exists(Path.Combine(TargetDir, "OptiScaler.ini")))
            {
                OptiScalerConfig.Apply(TargetDir, OptiProfile.MfgOnly, value);
                OnPropertyChanged(nameof(OptiProfileLabel));
            }
        }
    }

    public string MultiplierLabel => Multiplier <= 1 ? Loc.T("common.off") : $"×{Multiplier}";

    /// <summary>Prerequis de la voie choisie : c'est ce qui manque, pas ce qui est possible.</summary>
    public IReadOnlyList<PrereqCheck> FgRequirements =>
        SelectedFg?.Requirements ?? Array.Empty<PrereqCheck>();

    public bool FgHasRequirements => FgRequirements.Count > 0;

    public string FgRequirementsLabel
    {
        get
        {
            var total = FgRequirements.Count;
            if (total == 0) return "—";
            var ok = FgRequirements.Count(r => r.State is UiStatus.Ready or UiStatus.Injected);
            return Loc.T("prereq.count", ok, total);
        }
    }

    public string? FgUnavailableReason =>
        FgOptions.Any(o => o.Available) ? null : FgOptions.FirstOrDefault()?.BlockedReason;

    private void SyncMultipliers()
    {
        Multipliers.Clear();
        foreach (var m in SelectedFg?.Multipliers ?? Array.Empty<int>()) Multipliers.Add(m);

        if (Multipliers.Count > 0 && !Multipliers.Contains(Multiplier))
            Multiplier = Multipliers.Contains(Profile.FgMultiplier) ? Profile.FgMultiplier : Multipliers[0];

        OnPropertyChanged(nameof(MultiplierLabel));
        BuildPipeline();
    }

    private void SetMultiplier(object? p)
    {
        if (p is int i) Multiplier = i;
        else if (p is string s && int.TryParse(s, out var v)) Multiplier = v;
    }

    // --------------------------------------------------------------- HDR

    private HdrPlan? _hdrPlan;

    /// <summary>
    /// Plan RenoDX du titre, tire du wiki en direct : quel mod, pourquoi, et chaque
    /// reglage ou fichier que la ligne du wiki exige.
    /// </summary>
    public HdrPlan? HdrPlan
    {
        get => _hdrPlan;
        private set
        {
            _hdrPlan = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasHdrPlan));
            OnPropertyChanged(nameof(HdrTitle));
            OnPropertyChanged(nameof(HdrKindLabel));
            OnPropertyChanged(nameof(HdrStatus));
            OnPropertyChanged(nameof(HdrStatusLabel));
            OnPropertyChanged(nameof(HdrBlocked));
            OnPropertyChanged(nameof(HdrPageUrl));
            OnPropertyChanged(nameof(HdrSummary));
            OnPropertyChanged(nameof(HdrEmptyLabel));
            InstallRenoDxCommand.Raise();
        }
    }

    public ObservableCollection<HdrStep> HdrSteps { get; } = new();

    public bool HasHdrPlan => HdrPlan is not null;

    public string HdrTitle => HdrPlan?.Entry?.Name ?? HdrKindLabel;

    public string HdrKindLabel => HdrPlan is null ? "" : Loc.T($"hdr.kind.{HdrPlan.Kind.ToString().ToLowerInvariant()}");

    public UiStatus HdrStatus => HdrPlan switch
    {
        null => UiStatus.Idle,
        { BlockedReason: not null } => UiStatus.Error,
        { Status: HdrModStatus.Working } => UiStatus.Ready,
        { Status: HdrModStatus.InProgress } => UiStatus.Warning,
        _ => UiStatus.Detected
    };

    public string HdrStatusLabel => HdrPlan is null ? "" : Loc.T($"hdr.status.{HdrPlan.Status.ToString().ToLowerInvariant()}");

    public string? HdrBlocked => HdrPlan?.BlockedReason;

    public string HdrPageUrl => HdrPlan?.ExternalUrl ?? HdrPlan?.Entry?.ThreadUrl ?? RenoDxWikiService.WikiPageUrl;

    /// <summary>« 3 reglages appliques par Prism · 2 a faire en jeu ».</summary>
    public string HdrSummary => HdrPlan is null
        ? ""
        : Loc.T("hdr.summary", HdrSteps.Count(x => x.Automatic), HdrSteps.Count(x => x.Kind == HdrStepKind.Manual));

    public string HdrEmptyLabel => _svc.Wiki.IsLoaded ? Loc.T("hdr.none") : Loc.T("hdr.wiki_unavailable");

    public string HdrSourceLabel => _svc.Wiki.FetchedAt is { } at
        ? Loc.T(_svc.Wiki.FromCache ? "hdr.source_cache" : "hdr.source_live", at.ToLocalTime().ToString("g", Loc.I.Culture))
        : "";

    private void BuildHdr()
    {
        var plan = _svc.Wiki.PlanFor(Game);
        if (plan is not null) _svc.Hdr.Evaluate(Game, plan);

        HdrSteps.Clear();
        if (plan is not null)
            foreach (var step in plan.Steps) HdrSteps.Add(step);

        HdrPlan = plan;
        OnPropertyChanged(nameof(HdrSourceLabel));
    }

    // ----------------------------------------------------------- DLSS 5

    /// <summary>Voies Neural Rendering, filtrees par l'API du titre.</summary>
    public ObservableCollection<Dlss5Option> Dlss5Options { get; } = new();

    private Dlss5Option? _selectedDlss5;
    public Dlss5Option? SelectedDlss5
    {
        get => _selectedDlss5;
        set
        {
            if (!Set(ref _selectedDlss5, value)) return;
            InstallDlss5Command.Raise();
            RefreshDlss5Checks();
            // Les builds proposees suivent l'addon de la voie : ShortFuse ou DLSS5 Tool.
            _selectedDlss5Build = null;
            OnPropertyChanged(nameof(Dlss5Builds));
            OnPropertyChanged(nameof(SelectedDlss5Build));
            OnPropertyChanged(nameof(HasDlss5Builds));
        }
    }

    /// <summary>Liste de controle des prerequis, dependante de la voie choisie.</summary>
    public ObservableCollection<PrereqCheck> Dlss5Checks { get; } = new();

    public Dlss5Readiness Dlss5State => Dlss5Service.Readiness(Game, _svc.Gpu);

    public UiStatus Dlss5Status => Dlss5State switch
    {
        Dlss5Readiness.Active => UiStatus.Injected,
        Dlss5Readiness.Ready => UiStatus.Ready,
        Dlss5Readiness.Incomplete => UiStatus.Warning,
        _ => UiStatus.Disabled
    };

    public string Dlss5Summary => Dlss5State switch
    {
        Dlss5Readiness.Active => Loc.T("dlss5.state.active"),
        Dlss5Readiness.Ready => Loc.T("dlss5.state.ready"),
        Dlss5Readiness.Incomplete => Loc.T("dlss5.state.incomplete"),
        _ => Loc.T("dlss5.gpu.hint_nonnvidia")
    };

    public string Dlss5Progress
    {
        get
        {
            var total = Dlss5Checks.Count;
            var ok = Dlss5Checks.Count(c => c.State is UiStatus.Ready or UiStatus.Injected);
            return total == 0 ? "—" : Loc.T("prereq.count", ok, total);
        }
    }

    public AsyncRelayCommand InstallDlss5Command { get; }
    public RelayCommand RemoveBridgeCommand { get; }
    public RelayCommand AdoptNeuralCommand { get; }
    public AsyncRelayCommand DeployStreamlineCommand { get; }
    public RelayCommand OpenSourceCommand { get; }
    public AsyncRelayCommand FixPrereqCommand { get; }
    public RelayCommand ApplyOptiProfileCommand { get; }
    public RelayCommand RevertGameCommand { get; }
    public RelayCommand ApplyMfgSettingsCommand { get; }
    public RelayCommand EarlyLoadCommand { get; }
    public RelayCommand VerifyCommand { get; }
    public RelayCommand RestoreVanillaCommand { get; }

    // ------------------------------------------------------- Reglages MFG

    private MfgSettings _mfg = new();

    /// <summary>Reglages lus dans ReShade.ini sous [RenoDX.MFGUnlock].</summary>
    public MfgSettings Mfg { get => _mfg; private set => Set(ref _mfg, value); }

    public IReadOnlyList<string> HdrModes => new[]
    {
        Loc.T("mfg.hdr.0"), Loc.T("mfg.hdr.1"), Loc.T("mfg.hdr.2"), Loc.T("mfg.hdr.3")
    };

    public IReadOnlyList<string> RuntimeModes => new[]
    {
        Loc.T("mfg.runtime.0"), Loc.T("mfg.runtime.1"), Loc.T("mfg.runtime.2")
    };

    public int MfgHdrMode
    {
        get => Mfg.HdrCompatibilityMode;
        set { Mfg.HdrCompatibilityMode = value; OnPropertyChanged(); }
    }

    public int MfgRuntimeMode
    {
        get => Mfg.RuntimeSelectionMode;
        set { Mfg.RuntimeSelectionMode = value; OnPropertyChanged(); }
    }

    public bool MfgDynamic
    {
        get => Mfg.DynamicMfg;
        set { Mfg.DynamicMfg = value; OnPropertyChanged(); }
    }

    public bool MfgAddonPresent => Game.HasMfgUnlock;

    /// <summary>Chargement precoce de l'addon DLSS 5, exige par RenoDX.</summary>
    public bool Dlss5EarlyLoaded => ReShadeConfig.IsEarlyLoaded(TargetDir, "renodx-dlss5.addon64");

    /// <summary>
    /// Refuse d'agir sur un jeu lance ou dont les fichiers sont verrouilles : c'est ce qui
    /// laissait des installations a moitie posees.
    /// </summary>
    private bool Guard()
    {
        if (GameGuard.Check(Game) is not { } blocked) return true;
        _notify(blocked.Message, true);
        return false;
    }

    private void ApplyMfgSettings()
    {
        if (!Guard()) return;
        // Le multiplicateur choisi dans l'interface devient celui force par l'addon.
        Mfg.ForceMultiplier = Multiplier;
        Mfg.MaxCount = Math.Max(Multiplier, 4);

        var result = ReShadeConfig.WriteMfg(TargetDir, Mfg);
        _notify(result.Message, !result.Success);
        RaiseMfg();
    }

    private void EnableEarlyLoad(string? addon)
    {
        if (string.IsNullOrWhiteSpace(addon) || !Guard()) return;
        var result = ReShadeConfig.EnableEarlyLoading(TargetDir, addon);
        _notify(result.Message, !result.Success);
        OnPropertyChanged(nameof(Dlss5EarlyLoaded));
    }

    private void RaiseMfg()
    {
        OnPropertyChanged(nameof(Mfg));
        OnPropertyChanged(nameof(MfgHdrMode));
        OnPropertyChanged(nameof(MfgRuntimeMode));
        OnPropertyChanged(nameof(MfgDynamic));
        OnPropertyChanged(nameof(MfgAddonPresent));
        OnPropertyChanged(nameof(Dlss5EarlyLoaded));
    }

    // ----------------------------------------------------------- Diagnostic

    private Diagnosis _diagnosis = Diagnosis.Empty;

    /// <summary>Ce que le dernier lancement du jeu dit du rendu neural, lu dans ReShade.log.</summary>
    public Diagnosis Diagnosis { get => _diagnosis; private set => Set(ref _diagnosis, value); }

    public RelayCommand RunDiagnosisCommand { get; }
    public AsyncRelayCommand ApplyDiagnosisFixCommand { get; }
    public RelayCommand OpenGameLogCommand { get; }

    private void RunDiagnosis() => Diagnosis = _svc.Diagnostics.Diagnose(Game);

    /// <summary>Applique le correctif d'une constatation, puis relit le journal.</summary>
    private async Task ApplyDiagnosisFixAsync(object? p)
    {
        if (p is not DiagnosisFinding finding || !Guard()) return;

        switch (finding.Fix)
        {
            case DiagnosisFix.Reinstall:
                // La variante deja posee est reinstallee, dans sa derniere version stable.
                var installed = Dlss5Addon.All.FirstOrDefault(a => File.Exists(Path.Combine(TargetDir, a.FileName)));
                SelectDlss5(installed ?? Dlss5Addon.ShortFuse);
                await PrepareDlss5Async();
                break;
            case DiagnosisFix.ReShade:
                await InstallReShadeAsync();
                break;
            case DiagnosisFix.ShortFuse:
                // Une suggestion, pas une installation d'office : la voie est choisie, l'utilisateur installe.
                SelectDlss5(Dlss5Addon.ShortFuse);
                _notify(Loc.T("diag.shortfuse_selected"), false);
                break;
        }

        RunDiagnosis();
    }

    private void SelectDlss5(Dlss5Addon kind)
    {
        var option = Dlss5Options.FirstOrDefault(o => Dlss5Addon.For(o.Backend) == kind && o.Available);
        if (option is not null) SelectedDlss5 = option;
    }

    // --------------------------------------------------------- Builds DLSS 5

    /// <summary>Addon RenoDX de la voie choisie, ou null si elle n'en pose pas.</summary>
    private Dlss5Addon? SelectedAddon => Dlss5Addon.For(SelectedDlss5?.Backend);

    /// <summary>Le choix de build n'a de sens que pour une voie a addon RenoDX.</summary>
    public bool HasDlss5Builds => SelectedAddon is not null;

    /// <summary>
    /// Builds stables de l'addon de la voie choisie, la plus recente d'abord. Les versions
    /// candidates (rc) n'y figurent pas.
    /// </summary>
    public IReadOnlyList<RhiRepoService.Release> Dlss5Builds =>
        SelectedAddon is { } addon ? _svc.Rhi.Family(addon.TagPrefix) : Array.Empty<RhiRepoService.Release>();

    private string? _selectedDlss5Build;

    /// <summary>Build choisie ; par defaut, la plus recente.</summary>
    public RhiRepoService.Release? SelectedDlss5Build
    {
        get => Dlss5Builds.FirstOrDefault(r => r.Tag == _selectedDlss5Build) ?? Dlss5Builds.FirstOrDefault();
        set
        {
            _selectedDlss5Build = value?.Tag;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Apres un changement de langue : les textes calcules (voies, prerequis, plan HDR)
    /// sont regeneres, sans perdre la voie choisie.
    /// </summary>
    public void Relocalize()
    {
        var keep = SelectedFg?.Backend;

        FgOptions.Clear();
        foreach (var o in _svc.FrameGen.OptionsFor(_svc.Gpu, Game)) FgOptions.Add(o);
        SelectedFg = FgOptions.FirstOrDefault(o => o.Backend == keep)
                     ?? FgOptions.FirstOrDefault(o => o.Available && o.Recommended)
                     ?? FgOptions.FirstOrDefault(o => o.Available);

        Refresh();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Une installation : occupation, progression, notification, rafraichissement.</summary>
    private async Task<bool> RunAsync(Func<IProgress<double>, Task<InstallResult>> action)
    {
        Busy = true;
        Progress = 0;
        try
        {
            var result = await action(new Progress<double>(p => Progress = p));
            _notify(result.Message, !result.Success);
            return result.Success;
        }
        catch (Exception ex)
        {
            _notify(Loc.T("err.install_failed", ex.Message), true);
            return false;
        }
        finally
        {
            Busy = false;
            Progress = 0;
            Refresh();
        }
    }

    // -------------------------------------------- Verification et retour vanille

    public ObservableCollection<DriftReport> Drift { get; } = new();
    public ObservableCollection<OrphanFile> Orphans { get; } = new();

    private RestorePlan? _plan;
    public RestorePlan? Plan { get => _plan; private set { Set(ref _plan, value); OnPropertyChanged(nameof(PlanSummary)); } }

    public string PlanSummary => Plan?.Summary ?? Loc.T("restore.plan.hint");

    public CompatVerdict Compat => CompatibilityMatrix.Evaluate(Game, _svc.Gpu);

    private void VerifyIntegrity()
    {
        Drift.Clear();
        foreach (var d in _svc.Restore.Verify(Game.Id)) Drift.Add(d);

        Orphans.Clear();
        foreach (var o in _svc.Restore.FindOrphans(Game)) Orphans.Add(o);

        Plan = _svc.Restore.Plan(Game);

        var drifted = Drift.Count(d => d.State != DriftState.Intact);
        _notify(drifted == 0
            ? Loc.T("verify.ok", Game.Name, Drift.Count, Orphans.Count)
            : Loc.T("verify.drift", Game.Name, drifted, Orphans.Count), drifted > 0);
    }

    private void RestoreVanilla(bool includeOrphans)
    {
        if (!Guard()) return;
        var result = _svc.Restore.RestoreVanilla(Game, includeOrphans);
        _notify(result.Message, !result.Success);
        VerifyIntegrity();
        Refresh();
    }

    // ------------------------------------------------ Resolution automatique

    /// <summary>
    /// Applique un correctif de prerequis. Chaque cas a une source verifiable : le
    /// catalogue signe pour les runtimes, le SDK officiel pour Streamline, reshade.me
    /// pour l'hote d'addons, le pilote local pour le runtime neural.
    /// </summary>
    private async Task<bool> ApplyFixAsync(PrereqCheck? check)
    {
        if (check is null || !Guard()) return false;

        return check.Fix switch
        {
            PrereqFix.DeployDlss => await DeployRuntimeAsync(DllKind.Dlss, null),
            // Le mode MFG dynamique exige 310.9.1 exactement, pas seulement "recent".
            PrereqFix.DeployDlssG => await DeployRuntimeAsync(DllKind.DlssG, "310.9.1"),
            PrereqFix.DeployStreamline => await DeployStreamlineAsync(),
            PrereqFix.InstallReShade => await InstallReShadeAsync(),
            PrereqFix.AdoptNeuralRuntime => AdoptNeural(),
            PrereqFix.InstallMfgAddon => await RunAsync(p => _svc.FrameGen.InstallMfgAdaAsync(Game, p)),
            PrereqFix.InstallDlss5Addon => await RunAsync(p =>
                _svc.Dlss5.InstallRenoDxAddonAsync(Game, SelectedAddon ?? Dlss5Addon.ShortFuse, SelectedDlss5Build?.Tag, p)),
            _ => true
        };
    }

    /// <summary>Pose un runtime, en visant une version precise quand elle est exigee.</summary>
    private async Task<bool> DeployRuntimeAsync(DllKind kind, string? exactPrefix)
    {
        var record = exactPrefix is null
            ? _svc.Manifest.Recommended(kind)
            : _svc.Manifest.For(kind).FirstOrDefault(r => r.IsSignatureValid
                  && r.Version.StartsWith(exactPrefix, StringComparison.Ordinal))
              ?? _svc.Manifest.Recommended(kind);

        if (record is null)
        {
            _notify(Loc.T("dll.err.no_version", DllInstaller.LabelFor(kind)), true);
            return false;
        }

        Busy = true;
        Progress = 0;
        try
        {
            var result = await _svc.Installer.InstallAsync(Game, record,
                new Progress<double>(p => Progress = p), deployIfMissing: true);
            _notify(result.Message, !result.Success);
            return result.Success;
        }
        catch (Exception ex) { Fail(Loc.T("fail.deploy", DllInstaller.LabelFor(kind)), ex.Message); return false; }
        finally { Busy = false; Progress = 0; Refresh(); }
    }

    /// <summary>
    /// Le bouton Installer : chaque prerequis manquant de la voie — runtime, Streamline
    /// apparie, ReShade — est telecharge et pose, puis la voie elle-meme. Un seul geste,
    /// et le premier echec arrete la chaine plutot que d'installer une pile bancale.
    /// </summary>
    private async Task PrepareFgAsync()
    {
        var option = SelectedFg;
        if (option is null || !Guard()) return;

        foreach (var req in option.Requirements.Where(r => r.Actionable).ToList())
            if (!await ApplyFixAsync(req)) return;

        // Les prerequis viennent de changer : on reevalue avant d'installer.
        Refresh();
        if (!await InstallFgAsync()) return;

        // Le multiplicateur choisi est ecrit dans la configuration quand la voie
        // passe par un paquet OptiScaler.
        if (option.Backend is FgBackend.OptiScaler)
            ApplyOptiProfile(OptiProfile.MfgOnly);
    }

    private async Task PrepareDlss5Async()
    {
        var option = SelectedDlss5;
        if (option is null || !Guard()) return;

        // Le paquet RenoDX DLSS 5 apporte lui-meme DLSS SR, le runtime neural et l'addon :
        // les poser a part avant lui serait telecharger deux fois.
        var covered = option.Backend is Dlss5Backend.RenoDxDlss5 or Dlss5Backend.ShortFuse
            ? new[] { PrereqFix.InstallDlss5Addon, PrereqFix.DeployDlss }
            : Array.Empty<PrereqFix>();

        foreach (var req in Dlss5Checks.Where(r => r.Actionable && !covered.Contains(r.Fix)).ToList())
            if (!await ApplyFixAsync(req)) return;

        Refresh();
        if (!await InstallDlss5Async()) return;

        // Voie OptiScaler : tout ce que le paquet sait faire, sans restriction.
        if (option.Backend is Dlss5Backend.OptiScalerNr or Dlss5Backend.OptiScalerMultipass)
            ApplyOptiProfile(OptiProfile.Full);
    }

    // ------------------------------------------------------ Ajoute par Prism

    private const string ReShadeOrigin = ReShadeService.Origin;

    /// <summary>
    /// Installations encore en place dans ce jeu — ce qu'on retrouve en revenant sur
    /// le titre, et ce qu'on retire d'un clic.
    /// </summary>
    public ObservableCollection<InstalledGroup> Installed { get; } = new();

    public bool HasInstalled => Installed.Count > 0;

    public RelayCommand RemoveInstalledCommand { get; }

    /// <summary>Mods presents dans le jeu sans avoir ete poses par Prism.</summary>
    public ObservableCollection<DetectedMod> Detected { get; } = new();

    public bool HasDetected => Detected.Count > 0;

    /// <summary>Quelque chose a montrer dans le bandeau : ajouts Prism ou mods exterieurs.</summary>
    public bool HasAnyMods => HasInstalled || HasDetected;

    public RelayCommand QuarantineDetectedCommand { get; }

    private void BuildInstalled()
    {
        Installed.Clear();
        foreach (var g in _svc.Changes.InstalledFor(Game.Id)) Installed.Add(g);

        // ReShade passe par son propre installateur : pas de trace fichier, seulement le profil.
        if (Profile.ReShadeInstalled && Game.HasReShade
            && Installed.All(g => !g.Origin.Equals(ReShadeOrigin, StringComparison.OrdinalIgnoreCase)))
            Installed.Add(new InstalledGroup
            {
                Origin = ReShadeOrigin,
                Version = ReShadeVersion,
                Summary = Loc.T("installed.reshade")
            });

        // Tout le reste de ce qui n'est pas d'origine : pose a la main ou par un autre outil.
        Detected.Clear();
        var prismPaths = _svc.Changes.For(Game.Id).Select(c => c.Path);
        foreach (var mod in ForeignModScanner.Scan(Game, prismPaths, Profile.ReShadeInstalled)) Detected.Add(mod);

        OnPropertyChanged(nameof(HasInstalled));
        OnPropertyChanged(nameof(HasDetected));
        OnPropertyChanged(nameof(HasAnyMods));
    }

    /// <summary>
    /// Met de cote un mod pose hors Prism : chaque fichier est copie dans les sauvegardes,
    /// puis retire du jeu. Rien n'est perdu — l'historique des modifications le restaure.
    /// </summary>
    private void QuarantineDetected(DetectedMod? mod)
    {
        if (mod is null || !mod.Removable || !Guard()) return;

        var moved = 0;
        foreach (var file in mod.Files)
        {
            try
            {
                if (!File.Exists(file)) continue;

                // Une sauvegarde plus ancienne du meme chemin contiendrait un autre fichier :
                // on ne supprime que ce qui est reellement a l'abri.
                var backup = _svc.Backups.Capture(Game, file, $"{mod.Label} · external");
                if (backup is null || !SameContent(backup.BackupPath, file)) continue;

                DllInstaller.ClearReadOnly(file);
                File.Delete(file);
                moved++;
            }
            catch (Exception ex)
            {
                Log.Warn(Src, $"Cannot set aside {file}: {ex.Message}");
            }
        }

        Log.Info(Src, $"{mod.Label} (outside Prism) set aside in {Game.Name}: {moved} file(s)");
        _notify(moved > 0 ? Loc.T("detected.removed", mod.Label, moved) : Loc.T("changes.msg.none_reverted"),
            moved == 0);
        Refresh();
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            return File.Exists(a) && new FileInfo(a).Length == new FileInfo(b).Length
                   && string.Equals(DownloadService.Sha256Cached(a), DownloadService.Sha256Cached(b),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Retire une installation entiere : fichiers ajoutes supprimes, originaux restaures,
    /// et les reglages qu'elle avait ecrits remis en l'etat.
    /// </summary>
    private void RemoveInstalled(InstalledGroup? group)
    {
        if (group is null || !Guard()) return;

        var origin = group.Origin;
        var dir = TargetDir;
        var done = 0;

        try
        {
            if (origin == ReShadeService.Origin)
            {
                done += ReShadeService.Uninstall(Game).FilesChanged;
                Profile.ReShadeInstalled = false;
                _svc.Profiles.Update(Profile);
            }
            else if (origin == "RenoDX HDR")
            {
                // Le retrait HDR remet aussi les cles de ReShade.ini et l'Engine.ini d'avant.
                done += _svc.Hdr.Remove(Game).FilesChanged;
            }

            done += _svc.Changes.RevertOrigin(Game.Id, origin);

            // Les inscriptions laissees dans ReShade.ini par l'addon retire.
            done += origin switch
            {
                Dlss5PackageInstaller.Origin => ReShadeConfig.CleanUp(dir,
                    new[] { Dlss5PackageInstaller.AddonFileName }, removeMfgSection: false),
                "RenoDX MFG Unlock" => ReShadeConfig.CleanUp(dir, new[] { "renodx-mfgunlock.addon64" }),
                "DLSS 5 Bridge" => ReShadeConfig.CleanUp(dir, new[] { "dlss5-bridge.addon64" }, removeMfgSection: false),
                _ when origin == Dlss5Addon.ShortFuse.Origin => ReShadeConfig.CleanUp(dir,
                    new[] { Dlss5Addon.ShortFuse.FileName }, removeMfgSection: false),
                _ => 0
            };
        }
        catch (Exception ex)
        {
            _notify(Loc.T("err.remove_failed", ex.Message), true);
            Refresh();
            return;
        }

        Log.Info(Src, $"{origin} removed from {Game.Name} ({done} item(s))");
        _notify(done > 0 ? Loc.T("installed.removed", origin, Game.Name) : Loc.T("changes.msg.none_reverted"),
            done == 0);
        Refresh();
    }

    /// <summary>Etat reel du profil, lu dans OptiScaler.ini.</summary>
    public string OptiProfileLabel
    {
        get
        {
            var dir = TargetDir;
            var ss = OptiScalerConfig.Read(dir, "Upscalers", "SuperSamplingEnabled");
            var fg = OptiScalerConfig.Read(dir, "FrameGen", "Enabled");
            if (ss is null && fg is null) return Loc.T("opti.none");

            var parts = new List<string>();
            parts.Add("upscaler " + (ss == "false" ? "off" : ss ?? "auto"));
            parts.Add("frame gen " + (fg == "false" ? "off" : fg ?? "auto"));
            var count = OptiScalerConfig.Read(dir, "DLSSG", "InterpolationCount");
            if (count is not null && count != "auto") parts.Add($"×{count}");
            return string.Join(" · ", parts);
        }
    }

    private void ApplyOptiProfile(object? p)
    {
        var profile = p switch
        {
            OptiProfile op => op,
            string s when Enum.TryParse<OptiProfile>(s, true, out var parsed) => parsed,
            _ => OptiProfile.Full
        };
        if (!Guard()) return;

        var result = OptiScalerConfig.Apply(TargetDir, profile, Multiplier);
        _notify(result.Message, !result.Success);
        OnPropertyChanged(nameof(OptiProfileLabel));
    }

    private void RevertGame()
    {
        if (!Guard()) return;
        var result = _svc.Changes.RevertGame(Game.Id);
        _notify(result.Message, !result.Success);
        Refresh();
    }

    private void RebuildDlss5()
    {
        var keep = SelectedDlss5?.Backend;

        Dlss5Options.Clear();
        foreach (var o in _svc.Dlss5.Options(Game, _svc.Gpu)) Dlss5Options.Add(o);

        SelectedDlss5 = (keep is { } k ? Dlss5Options.FirstOrDefault(o => o.Backend == k) : null)
                        ?? Dlss5Options.FirstOrDefault(o => o.Available && o.Recommended)
                        ?? Dlss5Options.FirstOrDefault(o => o.Available)
                        ?? Dlss5Options.FirstOrDefault();

        RefreshDlss5Checks();

        OnPropertyChanged(nameof(Dlss5State));
        OnPropertyChanged(nameof(Dlss5Status));
        OnPropertyChanged(nameof(Dlss5Summary));
        RemoveBridgeCommand.Raise();
        AdoptNeuralCommand.Raise();
    }

    private void RefreshDlss5Checks()
    {
        Dlss5Checks.Clear();
        foreach (var c in _svc.Dlss5.Preflight(Game, _svc.Gpu, SelectedDlss5)) Dlss5Checks.Add(c);
        OnPropertyChanged(nameof(Dlss5Progress));
    }

    private async Task<bool> InstallDlss5Async()
    {
        if (SelectedDlss5 is null) return false;

        Busy = true;
        Progress = 0;
        try
        {
            var result = await _svc.Dlss5.InstallAsync(Game, SelectedDlss5, new Progress<double>(p => Progress = p), addonTag: SelectedDlss5Build?.Tag);
            _notify(result.Message, !result.Success);
            if (!result.Success)
                Fail(Loc.T("fail.install", SelectedDlss5.Title), result.Message,
                    Loc.T("cause.signature"),
                    Loc.T("cause.nr_missing"),
                    Loc.T("cause.no_proxy"),
                    Loc.T("cause.rights"),
                    Loc.T("cause.release"));
            return result.Success;
        }
        catch (Exception ex) { Fail(Loc.T("fail.dlss5"), ex.Message); return false; }
        finally { Busy = false; Progress = 0; Refresh(); }
    }

    /// <summary>
    /// Depose le paquet Streamline apparie exige par le mode MFG dynamique. Les
    /// composants sl.* ne doivent jamais provenir de paquets differents.
    /// </summary>
    private async Task<bool> DeployStreamlineAsync()
    {
        if (!Guard()) return false;
        Busy = true;
        Progress = 0;
        try
        {
            var result = await _svc.Streamline.DeployAsync(Game, StreamlineService.DynamicMfgVersion,
                new Progress<double>(p => Progress = p));
            _notify(result.Message, !result.Success);
            return result.Success;
        }
        catch (Exception ex) { Fail(Loc.T("fail.streamline"), ex.Message); return false; }
        finally { Busy = false; Progress = 0; Refresh(); }
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _notify(Loc.T("err.open_failed", ex.Message), true); }
    }

    private void RemoveBridge()
    {
        if (!Guard()) return;
        var result = Dlss5Service.RemoveBridge(Game);
        _notify(result.Message, !result.Success);
        Refresh();
    }

    private bool AdoptNeural()
    {
        if (!Guard()) return false;
        var result = Dlss5Service.AdoptNeuralRuntime(Game);
        _notify(result.Message, !result.Success);
        Refresh();
        return result.Success;
    }

    // ---------------------------------------------------------- ReShade

    private string? _reShadeVersion;
    public string? ReShadeVersion { get => _reShadeVersion; private set => Set(ref _reShadeVersion, value); }

    /// <summary>
    /// Etat de ReShade, releve lors du rafraichissement. Ces proprietes sont lues par des
    /// liaisons et des infobulles : elles ne doivent jamais toucher au disque a la volee.
    /// </summary>
    private Services.ReShadeState _reShade = ReShadeLocator.Empty;

    /// <summary>« 6.8.0 · add-on · dxgi.dll » : version, build et nom sous lequel le jeu le charge.</summary>
    public string ReShadeStatus => _reShade.Present ? _reShade.Label : Loc.T("common.not_installed");

    public UiStatus ReShadeState => _reShade switch
    {
        { Ready: true } => UiStatus.Injected,
        { Present: true } => UiStatus.Warning,
        _ => UiStatus.Idle
    };

    // -------------------------------------------------------- Injection

    public ObservableCollection<PipelineStage> Pipeline { get; } = new();

    /// <summary>Nom du fichier proxy reellement present, ou celui qui serait utilise.</summary>
    public string InjectionMethod
    {
        get
        {
            if (Game.HasMfgUnlock && HasAddonFile("mfgunlock")) return Loc.T("method.addon");
            if (Game.HasOptiScaler) return Loc.T("method.proxy");
            if (Game.HasReShade) return Loc.T("method.proxy_reshade");
            return SelectedFg?.Method ?? "—";
        }
    }

    public UiStatus InjectionState =>
        Game.HasMfgUnlock || Game.HasOptiScaler || Game.HasRenoDx ? UiStatus.Injected
        : Game.HasReShade ? UiStatus.Ready
        : UiStatus.Disabled;

    public string InjectedAddons
    {
        get
        {
            try
            {
                var files = Directory.EnumerateFiles(TargetDir, "*.addon64")
                    .Select(Path.GetFileName).Where(n => n is not null).ToList();
                return files.Count == 0 ? Loc.T("common.none") : string.Join(", ", files);
            }
            catch { return "—"; }
        }
    }

    private bool HasAddonFile(string fragment)
    {
        try
        {
            return Directory.EnumerateFiles(TargetDir, "*.addon*")
                .Any(f => Path.GetFileName(f).Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// Construit la chaine de rendu affichee. Un etage reste visible meme lorsqu'il
    /// n'est pas emprunte : c'est la chaine complete qui informe, pas seulement la
    /// portion active.
    /// </summary>
    private void BuildPipeline()
    {
        Pipeline.Clear();

        void Add(string name, string detail, bool engaged, UiStatus state = UiStatus.Active, bool first = false)
            => Pipeline.Add(new PipelineStage
            {
                Name = name, Detail = detail, Engaged = engaged,
                State = engaged ? state : UiStatus.Idle, IsFirst = first
            });

        Add(Loc.T("pipeline.game"), ExecutableName, true, UiStatus.Detected, first: true);
        Add(Loc.T("label.engine"), Game.Engine ?? "n/d", Game.Engine is not null, UiStatus.Detected);
        Add("API", Game.Api.Short(), Game.Api != GameApi.Unknown, UiStatus.Detected);

        var sr = Sr;
        Add("DLSS SR", sr?.IsPresent == true ? sr.InstalledVersion : Loc.T("common.absent"), sr?.IsPresent == true);

        var fg = Fg;
        Add("DLSS-G", fg?.IsPresent == true ? fg.InstalledVersion : Loc.T("common.absent"), fg?.IsPresent == true);

        var mfgOn = Game.HasMfgUnlock || _svc.Gpu.SupportsNativeMfg;
        Add("MFG", mfgOn ? $"×{Multiplier}" : Loc.T("common.unavailable_short"), mfgOn, UiStatus.Injected);

        var nr = Nr;
        Add("RAY RECON", nr?.IsPresent == true ? nr.InstalledVersion : Loc.T("common.absent"), nr?.IsPresent == true);

        // Neural Rendering est un etage distinct de la reconstruction des rayons.
        Add("DLSS 5", Game.HasDlss5 ? "neural" : Game.HasDlss5Bridge ? Loc.T("pipeline.bridge_only") : Loc.T("common.absent"),
            Game.HasDlss5, UiStatus.Injected);

        Add("POST", Game.HasRenoDx ? "RenoDX" : Game.HasReShade ? "ReShade" : Loc.T("common.none"),
            Game.HasReShade || Game.HasRenoDx, UiStatus.Injected);

        Add(Loc.T("label.display"), _svc.Display.Resolution, true, UiStatus.Active);
    }

    // ------------------------------------------------------------- Etat

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        private set
        {
            Set(ref _busy, value);
            InstallFgCommand.Raise();
            InstallDlss5Command.Raise();
            InstallRenoDxCommand.Raise();
            InstallReShadeCommand.Raise();
            RemoveInstalledCommand.Raise();
            QuarantineDetectedCommand.Raise();
            ApplyDiagnosisFixCommand?.Raise();
        }
    }

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private string? _lastError;
    /// <summary>Dernier echec, conserve pour l'encart de diagnostic de la vue Injection.</summary>
    public string? LastError { get => _lastError; private set { Set(ref _lastError, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(LastError);

    private string? _lastErrorTitle;
    public string? LastErrorTitle { get => _lastErrorTitle; private set => Set(ref _lastErrorTitle, value); }

    public ObservableCollection<string> ErrorCauses { get; } = new();

    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand RestoreEverythingCommand { get; }
    public AsyncRelayCommand InstallFgCommand { get; }
    public RelayCommand RemoveFgCommand { get; }
    public RelayCommand PickMultiplierCommand { get; }
    public AsyncRelayCommand InstallRenoDxCommand { get; }
    public RelayCommand RemoveRenoDxCommand { get; }
    public RelayCommand OpenHdrPageCommand { get; }
    public AsyncRelayCommand InstallReShadeCommand { get; }
    public RelayCommand RemoveReShadeCommand { get; }
    public RelayCommand ResetProfileCommand { get; }

    // ------------------------------------------------------- Construction

    private void Build()
    {
        Slots.Clear();
        foreach (var kind in new[] { DllKind.Dlss, DllKind.DlssG, DllKind.DlssD })
            Slots.Add(new DllSlotViewModel(kind, Game, _svc.Manifest, _svc.Installer,
                _svc.Backups, _svc.Deployments, _notify));

        // Les bibliotheques non-NVIDIA n'apparaissent que si le jeu les embarque.
        foreach (var kind in new[] { DllKind.FsrDx12, DllKind.XeSS, DllKind.XeSSFg })
            if (Game.Dlls.Any(d => d.Kind == kind))
                Slots.Add(new DllSlotViewModel(kind, Game, _svc.Manifest, _svc.Installer,
                    _svc.Backups, _svc.Deployments, _notify));

        FgOptions.Clear();
        foreach (var o in _svc.FrameGen.OptionsFor(_svc.Gpu, Game)) FgOptions.Add(o);
        SelectedFg = FgOptions.FirstOrDefault(o => o.Available && o.Recommended)
                     ?? FgOptions.FirstOrDefault(o => o.Available);

        BuildHdr();
        _reShade = ReShadeLocator.Scan(Game);
        ReShadeVersion = _reShade.Present ? _reShade.VersionLabel : null;
        SyncMultipliers();
        RaiseDerived();
    }

    private void RaiseDerived()
    {
        RebuildDlss5();
        BuildInstalled();
        Mfg = ReShadeConfig.ReadMfg(TargetDir);
        RaiseMfg();
        OnPropertyChanged(nameof(Compat));
        OnPropertyChanged(nameof(Dlss5Builds));
        OnPropertyChanged(nameof(SelectedDlss5Build));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(EngineLabel));
        OnPropertyChanged(nameof(FgUnavailableReason));
        BuildHdr();
        OnPropertyChanged(nameof(ReShadeStatus));
        OnPropertyChanged(nameof(ReShadeState));
        OnPropertyChanged(nameof(InjectionMethod));
        OnPropertyChanged(nameof(InjectionState));
        OnPropertyChanged(nameof(InjectedAddons));
        OnPropertyChanged(nameof(OptiProfileLabel));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(Sr));
        OnPropertyChanged(nameof(Fg));
        OnPropertyChanged(nameof(Nr));
        RemoveRenoDxCommand.Raise();
        RemoveReShadeCommand.Raise();
        BuildPipeline();
        RunDiagnosis();
    }

    public void Refresh()
    {
        DllDetector.Inspect(Game);
        foreach (var slot in Slots) slot.Reload();
        _reShade = ReShadeLocator.Scan(Game);
        ReShadeVersion = _reShade.Present ? _reShade.VersionLabel : null;
        RaiseDerived();
    }

    // ----------------------------------------------------------- Actions

    private void OpenFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{TargetDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Fail(Loc.T("fail.open_folder"), ex.Message); }
    }

    private void RestoreEverything()
    {
        if (!Guard()) return;
        var result = _svc.Installer.RestoreAll(Game);
        _notify(result.Message, !result.Success);
        Refresh();
    }

    private async Task<bool> InstallFgAsync()
    {
        if (SelectedFg is null) return false;

        Busy = true;
        Progress = 0;
        LastError = null;

        try
        {
            var result = await _svc.FrameGen.InstallAsync(Game, SelectedFg, new Progress<double>(p => Progress = p));
            _notify(result.Message, !result.Success);

            if (result.Success)
            {
                Profile.FgBackend = SelectedFg.Backend;
                Profile.FgMultiplier = Multiplier;
                _svc.Profiles.Update(Profile);
            }
            else
            {
                Fail(Loc.T("fail.inject", SelectedFg.Title), result.Message,
                    Loc.T("cause.dlssg_old"),
                    Loc.T("cause.conflict"),
                    Loc.T("cause.no_streamline"),
                    Loc.T("cause.rights"));
            }
            return result.Success;
        }
        catch (Exception ex) { Fail(Loc.T("fail.inject_interrupted"), ex.Message); return false; }
        finally { Busy = false; Progress = 0; Refresh(); }
    }

    private void RemoveFg()
    {
        if (!Guard()) return;
        var result = FrameGenService.RemoveOverlays(Game);
        _notify(result.Message, !result.Success);
        Profile.FgBackend = FgBackend.None;
        _svc.Profiles.Update(Profile);
        Refresh();
    }

    /// <summary>ReShade d'abord s'il manque, puis le plan HDR complet.</summary>
    private async Task PrepareHdrAsync()
    {
        if (HdrPlan is null || !Guard()) return;

        // Un ReShade add-on 6.8+ deja charge est garde tel quel ; sinon il est pose ou mis a niveau.
        if (!HdrInstaller.ReShadeReady(Game))
        {
            if (!await InstallReShadeAsync()) return;
            DllDetector.Inspect(Game);
            if (!HdrInstaller.ReShadeReady(Game)) return;
        }

        var plan = HdrPlan;
        if (plan is null) return;
        await RunAsync(p => _svc.Hdr.ApplyAsync(Game, plan, p));
    }

    private void RemoveHdr()
    {
        if (!Guard()) return;
        var result = _svc.Hdr.Remove(Game);
        _notify(result.Message, !result.Success);
        Refresh();
    }

    private async Task<bool> InstallReShadeAsync()
    {
        if (!Guard()) return false;
        Busy = true;
        Progress = 0;
        try
        {
            var result = await _svc.ReShade.InstallAsync(Game, new Progress<double>(p => Progress = p));
            _notify(result.Message, !result.Success);
            // Un ReShade deja present et garde tel quel n'est pas une installation de Prism ;
            // un echec, lui, ne doit pas faire oublier une installation precedente.
            Profile.ReShadeInstalled = (result.Success && result.FilesChanged > 0) || Profile.ReShadeInstalled;
            _svc.Profiles.Update(Profile);
            return result.Success;
        }
        catch (Exception ex) { Fail(Loc.T("fail.reshade"), ex.Message); return false; }
        finally { Busy = false; Progress = 0; Refresh(); }
    }

    private void RemoveReShade()
    {
        if (!Guard()) return;
        var result = ReShadeService.Uninstall(Game);
        _notify(result.Message, !result.Success);
        Refresh();
    }

    private void ResetProfile()
    {
        Profile = new GameProfile { GameId = Game.Id };
        _svc.Profiles.Update(Profile);
        _notify(Loc.T("profile.reset", Game.Name), false);
        Build();
    }

    /// <summary>Enregistre un echec avec ses causes probables, pour l'encart de diagnostic.</summary>
    private void Fail(string title, string detail, params string[] causes)
    {
        LastErrorTitle = title;
        LastError = detail;
        ErrorCauses.Clear();
        foreach (var c in causes) ErrorCauses.Add(c);
        Log.Error(Src, $"{title} — {detail}");
        _notify(Loc.T("common.title_detail", title, detail), true);
    }
}
