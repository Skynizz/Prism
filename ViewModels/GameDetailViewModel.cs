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
        InstallFgCommand = new AsyncRelayCommand(PrepareFgAsync, () => SelectedFg?.Available == true && !Busy && !FgInstalled);
        ReinstallFgCommand = new AsyncRelayCommand(PrepareFgAsync, () => SelectedFg?.Available == true && !Busy && FgInstalled);
        RemoveFgCommand = new RelayCommand(_ => RemoveFg());
        PickMultiplierCommand = new RelayCommand(p => SetMultiplier(p));

        InstallRenoDxCommand = new AsyncRelayCommand(PrepareHdrAsync, () => HdrPlan?.CanInstall == true && !Busy && !HdrInstalled);
        ReinstallHdrCommand = new AsyncRelayCommand(PrepareHdrAsync, () => HdrPlan?.CanInstall == true && !Busy && HdrInstalled);
        RemoveRenoDxCommand = new RelayCommand(_ => RemoveHdr(), _ => Game.HasRenoDx);
        OpenHdrPageCommand = new RelayCommand(_ => OpenUrl(HdrPageUrl));

        InstallReShadeCommand = new AsyncRelayCommand(InstallReShadeAsync, () => !Busy);
        RemoveReShadeCommand = new RelayCommand(_ => RemoveReShade(), _ => Game.HasReShade);

        ResetProfileCommand = new RelayCommand(_ => ResetProfile());

        InstallDlss5Command = new AsyncRelayCommand(PrepareDlss5Async,
            () => SelectedDlss5?.Available == true && !Busy && !Dlss5Installed);
        ReinstallDlss5Command = new AsyncRelayCommand(PrepareDlss5Async,
            () => SelectedDlss5?.Available == true && !Busy && Dlss5Installed);
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
        AnalyzeCleanCommand = new RelayCommand(_ => AnalyzeClean());
        RunCleanCommand = new RelayCommand(_ => RunClean(), _ => HasCleanPlan && !Busy);
        UndoCleanCommand = new RelayCommand(_ => UndoClean(), _ => !Busy);
        CopyFileListCommand = new RelayCommand(_ => CopyFileList());
        SteamVerifyCommand = new RelayCommand(_ => SteamVerify());
        ChooseExeCommand = new RelayCommand(_ => ChooseExe(), _ => !Busy);
        HealCommand = new AsyncRelayCommand(HealAsync, () => CanHeal);
        ArmResetCommand = new RelayCommand(_ => ArmReset(), _ => !Busy);
        ConfirmResetCommand = new RelayCommand(_ => ConfirmReset(), _ => !Busy);
        CancelResetCommand = new RelayCommand(_ => ResetArmed = false);
        ToggleIdentifyCommand = new RelayCommand(_ => ToggleIdentify());
        SearchIdentityCommand = new AsyncRelayCommand(SearchIdentityAsync, () => !string.IsNullOrWhiteSpace(IdentitySearch));
        UseCandidateCommand = new RelayCommand(p => { if (p is IdentityCandidate c) UseIdentity(c.Name, c.AppId); });
        UseCustomNameCommand = new RelayCommand(_ => UseIdentity(IdentitySearch, null));
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

    public string EngineLabel => Game.EngineLabel ?? Loc.T("common.unidentified");

    /// <summary>
    /// Ce qui change pour un vieux jeu : API anterieure a DirectX 11, ou Unreal 3. Vide sinon.
    /// Les voies concernees se bloquent d'elles-memes ; cette ligne dit pourquoi, d'un coup.
    /// </summary>
    public string LegacyNote => string.Join(" ", new[]
    {
        Game.Api is GameApi.DirectX9 or GameApi.DirectX10 or GameApi.OpenGL ? Loc.T("compat.legacy_api", Game.Api.Label()) : null,
        Game.EngineGeneration == 3 ? Loc.T("compat.ue3") : null
    }.Where(x => x is not null));

    public bool HasLegacyNote => LegacyNote.Length > 0;
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
            RefreshInstallStates();
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
                var nativeFg = Game.HasDlssG || Game.HasStreamline;
                if (Profile.FgBackend == FgBackend.InjectedDlssG)
                    OptiScalerConfig.Apply(TargetDir, OptiProfile.InjectedFg, value, nativeFg: false);
                else
                    OptiScalerConfig.Apply(TargetDir, OptiProfile.MfgOnly, value,
                        nativeFg: nativeFg);
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
            RefreshInstallStates();
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

    /// <summary>
    /// Le journal dit ce qui s'est passe ; le disque dit ce qui manque. Les deux sont reunis : les
    /// controles du DLSS 5 (runtime neural, Streamline, compilateur, addon) en echec deviennent des
    /// constatations reparables, comme un Streamline aux versions melangees ou une installation posee
    /// par un autre outil.
    /// </summary>
    private void RunDiagnosis()
    {
        var log = _svc.Diagnostics.Diagnose(Game);
        var extra = new List<DiagnosisFinding>();

        var kind = Dlss5Addon.All.FirstOrDefault(a => File.Exists(Path.Combine(TargetDir, a.FileName)));
        if (kind is not null)
        {
            var option = Dlss5Options.FirstOrDefault(o => Dlss5Addon.For(o.Backend) == kind);
            var broken = _svc.Dlss5.Preflight(Game, _svc.Gpu, option)
                .Where(c => c.Label is "NEURAL RT" or "STREAMLINE" or "SIGNATURE" or "D3DCOMPILER" or "ADDON"
                            && c.State is UiStatus.Error or UiStatus.Warning)
                .ToList();
            foreach (var c in broken)
                extra.Add(new DiagnosisFinding
                {
                    Id = "pre-" + c.Label,
                    State = c.State == UiStatus.Error ? UiStatus.Error : UiStatus.Warning,
                    Title = $"{c.Label} · {c.Detail}",
                    Evidence = c.Hint,
                    Fix = DiagnosisFix.Reinstall
                });

            // Addon present sans trace dans le registre : pose par un autre outil.
            var addonPath = Path.Combine(TargetDir, kind.FileName);
            var ours = _svc.Changes.For(Game.Id).Any(e => e.StillApplies
                && string.Equals(e.Path, addonPath, StringComparison.OrdinalIgnoreCase));
            if (!ours)
                extra.Add(new DiagnosisFinding
                {
                    Id = "foreign-install",
                    State = broken.Count > 0 ? UiStatus.Warning : UiStatus.Detected,
                    Title = Loc.T("diag.f.foreign", kind.Label),
                    Evidence = broken.Count > 0
                        ? Loc.T("diag.f.foreign_incomplete", string.Join(", ", broken.Select(b => b.Label)))
                        : Loc.T("diag.f.foreign_ok"),
                    Fix = DiagnosisFix.Migrate
                });
        }

        if (MixedStreamline() is { } mixed)
        {
            // Plugin d'une autre version laisse par un autre outil : reinstaller ne l'enleve jamais,
            // il faut le mettre de cote. Pose par Prism : la reinstallation le remet au bon niveau.
            var foreignOdd = mixed.Odd.Count > 0 && mixed.Odd.All(p => !_svc.Deployments.WasDeployed(p));
            extra.Add(new DiagnosisFinding
            {
                Id = "sl-mixed", State = UiStatus.Error, Title = Loc.T("diag.f.sl_mixed"),
                Evidence = foreignOdd ? mixed.Text + " · " + Loc.T("diag.f.sl_mixed_foreign") : mixed.Text,
                Fix = foreignOdd ? DiagnosisFix.SetAside : kind is not null ? DiagnosisFix.Reinstall : DiagnosisFix.Streamline
            });
        }

        Diagnosis = Merge(log, extra);
        _liveLogStamp = LogStamp();
        OnPropertyChanged(nameof(CanHeal));
        OnPropertyChanged(nameof(IsLive));
        HealCommand?.Raise();
    }

    /// <summary>
    /// Composants sl.* de versions differentes dans un meme dossier : null s'ils sont apparies.
    /// La reference est sl.interposer.dll, qui charge les plugins ; a defaut, la version majoritaire.
    /// <c>Odd</c> : les plugins qui s'en ecartent.
    /// </summary>
    private (string Text, List<string> Odd)? MixedStreamline()
    {
        foreach (var dir in Game.StreamlineDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "sl.*.dll"); } catch { continue; }
            var versions = files
                .Select(f => (Path: f, Name: Path.GetFileName(f), Version: DllDetector.ReadProductVersion(f)))
                .Where(v => !string.IsNullOrWhiteSpace(v.Version))
                .ToList();
            if (versions.Select(v => v.Version).Distinct().Count() <= 1) continue;

            var reference = versions.FirstOrDefault(v => v.Name.Equals("sl.interposer.dll", StringComparison.OrdinalIgnoreCase)).Version
                            ?? versions.GroupBy(v => v.Version).OrderByDescending(g => g.Count()).First().Key;
            var text = string.Join(" · ", versions.GroupBy(v => v.Version)
                .Select(g => $"{g.Key}: {string.Join(", ", g.Select(v => v.Name).Take(3))}"));
            return (text, versions.Where(v => v.Version != reference).Select(v => v.Path).ToList());
        }
        return null;
    }

    private static Diagnosis Merge(Diagnosis log, List<DiagnosisFinding> extra)
    {
        if (extra.Count == 0) return log;
        var findings = extra.Where(f => f.State == UiStatus.Error)
            .Concat(log.Findings)
            .Concat(extra.Where(f => f.State != UiStatus.Error))
            .ToList();
        var verdict = findings.Any(f => f.State == UiStatus.Error) ? DiagnosisVerdict.Failed
            : findings.Any(f => f.State == UiStatus.Warning) ? DiagnosisVerdict.Degraded
            : log.Verdict == DiagnosisVerdict.NotApplicable ? DiagnosisVerdict.Unknown
            : log.Verdict;
        return new Diagnosis
        {
            Verdict = verdict,
            Summary = verdict is DiagnosisVerdict.Failed or DiagnosisVerdict.Degraded && log.Verdict != verdict
                ? Loc.T("diag.issues", findings.Count(f => f.State is UiStatus.Error or UiStatus.Warning))
                : log.Summary,
            LogPath = log.LogPath,
            LogTime = log.LogTime,
            Findings = findings
        };
    }

    // ------------------------------------------------------- Preset DLSS

    /// <summary>Un preset DLSS Super Resolution, valeur de NvApiDriverSettings.h.</summary>
    public sealed record DlssPresetChoice(uint Value, string Label, string Note);

    /// <summary>
    /// Presets proposes. Valeurs officielles (EValues_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION) :
    /// J 10, K 11, L 12, M 13, « Latest » 0x00FFFFFF ; 0 = rien d'impose, le jeu decide.
    /// Roles d'apres NVIDIA (DLSS 4.5) : K par defaut pour DLAA/Qualite/Equilibre, M optimise pour
    /// Performance, L pour l'Ultra Performance en 4K.
    /// </summary>
    public IReadOnlyList<DlssPresetChoice> DlssPresets { get; private set; } = BuildDlssPresets();

    private static DlssPresetChoice[] BuildDlssPresets() => new[]
    {
        new DlssPresetChoice(0, Loc.T("preset.game"), Loc.T("preset.game_note")),
        new DlssPresetChoice(0x00FFFFFF, Loc.T("preset.latest"), Loc.T("preset.latest_note")),
        new DlssPresetChoice(11, "K", Loc.T("preset.k_note")),
        new DlssPresetChoice(13, "M", Loc.T("preset.m_note")),
        new DlssPresetChoice(12, "L", Loc.T("preset.l_note")),
        new DlssPresetChoice(10, "J", Loc.T("preset.j_note")),
    };

    private DlssPresetChoice? _dlssPreset;
    private bool _loadingPreset;

    /// <summary>Preset impose par le profil du pilote pour cet executable. Le changer l'ecrit aussitot.</summary>
    public DlssPresetChoice? SelectedDlssPreset
    {
        get => _dlssPreset;
        set
        {
            if (!Set(ref _dlssPreset, value) || value is null || _loadingPreset) return;
            ApplyDlssPreset(value);
        }
    }

    public bool CanChooseDlssPreset => _svc.Gpu.IsNvidia && Game.Executable is not null;

    private string ExeName => Path.GetFileName(Game.Executable ?? "");

    private void LoadDlssPreset()
    {
        if (!CanChooseDlssPreset) return;
        _loadingPreset = true;
        try
        {
            // Seul ce qui a ete choisi pour ce jeu compte : une valeur heritee du profil global ou
            // predefinie par NVIDIA n'est pas un preset impose.
            var on = NvDriverSettings.ReadOwn(ExeName, NvDriverSettings.DlssSrOverride) == 1;
            var value = on ? NvDriverSettings.ReadOwn(ExeName, NvDriverSettings.DlssSrPresetSelection) ?? 0 : 0;
            SelectedDlssPreset = DlssPresets.FirstOrDefault(p => p.Value == value) ?? DlssPresets[0];
        }
        finally { _loadingPreset = false; }
    }

    /// <summary>
    /// Ecrit dans le profil du pilote : l'override DLSS-SR active, puis le preset. « Le jeu decide »
    /// retire les deux reglages. Aucun fichier du jeu n'est touche.
    /// </summary>
    private void ApplyDlssPreset(DlssPresetChoice choice)
    {
        var ok = choice.Value == 0
            ? NvDriverSettings.Clear(ExeName, NvDriverSettings.DlssSrOverride, NvDriverSettings.DlssSrPresetSelection)
            : NvDriverSettings.Write(ExeName, (NvDriverSettings.DlssSrOverride, 1), (NvDriverSettings.DlssSrPresetSelection, choice.Value));
        _notify(ok ? Loc.T("preset.set", choice.Label, Game.Name) : Loc.T("preset.err"), !ok);
        if (!ok) LoadDlssPreset();
    }

    // ---------------------------------------------------------- Deja installe

    private bool _dlss5Installed, _hdrInstalled, _fgInstalled;

    /// <summary>
    /// La voie choisie est deja en place et valide : fichiers presents, runtime neural de confiance,
    /// Streamline complet. Le bouton affiche alors « Installe » ; un fichier retire ou une signature
    /// invalide le fait revenir a « Installer ».
    /// </summary>
    public bool Dlss5Installed { get => _dlss5Installed; private set => Set(ref _dlss5Installed, value); }
    public bool HdrInstalled { get => _hdrInstalled; private set => Set(ref _hdrInstalled, value); }
    public bool FgInstalled { get => _fgInstalled; private set => Set(ref _fgInstalled, value); }

    public string Dlss5ButtonLabel => Loc.T(Dlss5Installed ? "btn.installed" : "btn.install");
    public string HdrButtonLabel => Loc.T(HdrInstalled ? "btn.installed" : "hdr.apply");
    public string FgButtonLabel => Loc.T(FgInstalled ? "btn.installed" : "btn.install");

    public AsyncRelayCommand ReinstallDlss5Command { get; }
    public AsyncRelayCommand ReinstallHdrCommand { get; }
    public AsyncRelayCommand ReinstallFgCommand { get; }

    private (DateTime, int) _installStamp;

    /// <summary>Recalcule les trois etats et met a jour les boutons.</summary>
    private void RefreshInstallStates()
    {
        Dlss5Installed = IsDlss5InPlace(SelectedDlss5);
        HdrInstalled = HdrPlan?.AddonFileName is { } addon
                       && File.Exists(Path.Combine(TargetDir, addon))
                       && HdrInstaller.ReShadeReady(Game);
        FgInstalled = IsFgInPlace(SelectedFg);
        _installStamp = InstallStamp();

        OnPropertyChanged(nameof(Dlss5ButtonLabel));
        OnPropertyChanged(nameof(HdrButtonLabel));
        OnPropertyChanged(nameof(FgButtonLabel));
        InstallDlss5Command?.Raise(); ReinstallDlss5Command?.Raise();
        InstallRenoDxCommand?.Raise(); ReinstallHdrCommand?.Raise();
        InstallFgCommand?.Raise(); ReinstallFgCommand?.Raise();
    }

    private bool IsDlss5InPlace(Dlss5Option? option)
    {
        if (option is null) return false;
        var dir = TargetDir;
        switch (option.Backend)
        {
            case Dlss5Backend.ShortFuse or Dlss5Backend.RenoDxDlss5:
                var kind = Dlss5Addon.For(option.Backend)!;
                // Les controles du DLSS 5 font foi : addon precharge, runtime neural en place et de
                // confiance, Streamline complet. Le moindre manque ramene le bouton « Installer ».
                return File.Exists(Path.Combine(dir, kind.FileName))
                       && Dlss5Checks.Where(c => c.Label is "NEURAL RT" or "STREAMLINE" or "SIGNATURE" or "ADDON")
                                     .All(c => c.State == UiStatus.Ready);
            case Dlss5Backend.OptiScalerNr or Dlss5Backend.OptiScalerMultipass:
                var nr = Path.Combine(dir, "nvngx_dlssnr.dll");
                return FrameGenService.LoadedOptiScaler(dir) is not null && OptiScalerConfig.IsFork(dir)
                       && File.Exists(nr) && Dlss5PackageInstaller.IsTrustedRuntime(nr);
            case Dlss5Backend.Bridge:
                return Game.HasDlss5Bridge && Game.HasNeuralRuntime;
            default:
                return false;   // installeur externe : Prism ne peut pas le constater
        }
    }

    private bool IsFgInPlace(FgOption? option)
    {
        if (option is null) return false;
        var dir = TargetDir;
        return option.Backend switch
        {
            FgBackend.MfgAdaUnlock => File.Exists(Path.Combine(dir, "renodx-mfgunlock.addon64")) && HdrInstaller.ReShadeReady(Game),
            FgBackend.Rtx40MfgUnlock => Game.HasMfgUnlock && !File.Exists(Path.Combine(dir, "renodx-mfgunlock.addon64")),
            FgBackend.OptiScaler => FrameGenService.LoadedOptiScaler(dir) is not null
                                    && string.Equals(OptiScalerConfig.Read(dir, "FrameGen", "FGOutput"), "fsrfg", StringComparison.OrdinalIgnoreCase),
            FgBackend.InjectedDlssG => FrameGenService.LoadedOptiScaler(dir) is not null && OptiScalerConfig.IsFork(dir)
                                       && string.Equals(OptiScalerConfig.Read(dir, "FrameGen", "FGOutput"), "dlssg", StringComparison.OrdinalIgnoreCase)
                                       && FrameGenService.InjectedStreamlineIntact(dir),
            _ => false
        };
    }

    /// <summary>Empreinte bon marche des dossiers concernes : change des qu'un fichier y apparait ou disparait.</summary>
    private (DateTime, int) InstallStamp()
    {
        var dirs = new[] { TargetDir, Path.Combine(TargetDir, "OptiScaler", "streamline") }
            .Concat(Dlss5PackageInstaller.RuntimeDirectories(Game))
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            // Date la plus recente des fichiers eux-memes : un fichier ecrase sur place (DLL remplacee
            // par une copie non signee) ne change pas la date du dossier, mais la sienne, si.
            var files = dirs.SelectMany(d => new DirectoryInfo(d).EnumerateFiles()).ToList();
            return (files.Select(f => f.LastWriteTimeUtc).DefaultIfEmpty().Max(), files.Count);
        }
        catch { return default; }
    }

    // ------------------------------------------------------ Suivi en direct

    private (DateTime, long) _liveLogStamp;

    /// <summary>Le jeu tourne : le journal est relu a chaque ecriture, sans attendre la fermeture.</summary>
    public bool IsLive => IsRunning && Diagnosis.Applies;

    /// <summary>Appele par la minuterie du processus (toutes les 4 s) : ne relit que si le journal a change.</summary>
    public void LiveTick()
    {
        OnPropertyChanged(nameof(IsLive));
        // Un fichier ajoute ou retire hors de Prism : les boutons Installer / Installe suivent.
        if (!IsRunning && InstallStamp() != _installStamp) { Refresh(); return; }
        if (!IsRunning) return;
        var stamp = LogStamp();
        if (stamp == _liveLogStamp) return;
        RunDiagnosis();
        Log.Trace(Src, $"Live diagnosis: {Diagnosis.Verdict}");
    }

    private (DateTime, long) LogStamp()
    {
        try
        {
            var info = new FileInfo(Path.Combine(TargetDir, "ReShade.log"));
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
        }
        catch { return default; }
    }

    // ----------------------------------------------------------- Reparer

    /// <summary>Corrections que « Reparer » sait enchainer seul, dans l'ordre ou elles s'appliquent.</summary>
    private static readonly DiagnosisFix[] HealOrder = { DiagnosisFix.SetAside, DiagnosisFix.MoveProxy, DiagnosisFix.ReShade, DiagnosisFix.Streamline, DiagnosisFix.Reinstall };

    public bool CanHeal => Diagnosis.Findings.Any(f => HealOrder.Contains(f.Fix)) && !Busy;

    public AsyncRelayCommand HealCommand { get; }

    /// <summary>
    /// Applique toutes les corrections du diagnostic d'un coup. Chaque etape est une transaction ; la
    /// photographie prise avant les rend atomiques ensemble : si l'une echoue, tout revient en l'etat.
    /// Le jeu doit etre ferme — un fichier charge ne se remplace pas.
    /// </summary>
    private async Task HealAsync()
    {
        if (!Guard()) return;
        var fixes = HealOrder.Where(f => Diagnosis.Findings.Any(x => x.Fix == f)).ToList();
        if (fixes.Count == 0) { _notify(Loc.T("heal.none"), false); return; }

        using var snapshot = FileSnapshot.Take(HealDirectories());
        var failed = (DiagnosisFix?)null;
        foreach (var fix in fixes)
        {
            if (!await ApplyHealStepAsync(fix)) { failed = fix; break; }
        }

        if (failed is { } step)
        {
            var left = snapshot.Restore();
            DllDetector.Inspect(Game);
            _notify(Loc.T("heal.rolled_back", Game.Name, new DiagnosisFinding { Id = "", State = UiStatus.Idle, Title = "", Fix = step }.FixLabel) + (left.Count > 0 ? " " + Loc.T("restore.refused", string.Join(", ", left)) : ""), true);
        }
        else
        {
            // Verification : une constatation qui survit a sa correction n'est pas « reparee ».
            var before = Diagnosis.Findings.Where(f => HealOrder.Contains(f.Fix)).Select(f => f.Id).ToHashSet();
            DllDetector.Inspect(Game);
            RunDiagnosis();
            var still = Diagnosis.Findings.Where(f => before.Contains(f.Id)).Select(f => f.Title).ToList();
            if (still.Count > 0) _notify(Loc.T("heal.not_resolved", Game.Name, string.Join(", ", still)), true);
            else _notify(Loc.T("heal.done", Game.Name, fixes.Count), false);
        }

        Refresh();
    }

    private async Task<bool> ApplyHealStepAsync(DiagnosisFix fix)
    {
        switch (fix)
        {
            case DiagnosisFix.ReShade:
                return await InstallReShadeAsync();
            case DiagnosisFix.Streamline:
                return await DeployStreamlineAsync();
            case DiagnosisFix.MoveProxy:
                return MoveOptiScaler();
            case DiagnosisFix.SetAside:
                // Plugins Streamline d'une autre version, poses par un autre outil : mis de cote, annulable.
                if (MixedStreamline() is not { Odd.Count: > 0 } odd) return true;
                var items = odd.Odd.Where(p => !_svc.Deployments.WasDeployed(p))
                    .Select(p => new CleanItem { Path = p, Action = CleanAction.Remove, Source = "Streamline", Display = Path.GetFileName(p) })
                    .ToList();
                if (items.Count == 0) return true;
                var result = _svc.Cleaner.Execute(Game, new CleanPlan { Items = items });
                if (result.Success) Log.Info(Src, $"{Game.Name}: set aside {string.Join(", ", items.Select(i => i.Display))}");
                return result.Success;
            case DiagnosisFix.Reinstall:
                var kind = Dlss5Addon.All.FirstOrDefault(a => File.Exists(Path.Combine(TargetDir, a.FileName))) ?? Dlss5Addon.ShortFuse;
                // L'addon ne se charge qu'avec ReShade en version add-on.
                if (!HdrInstaller.ReShadeReady(Game) && !await InstallReShadeAsync()) return false;
                return await RunAsync(p => _svc.Dlss5.InstallRenoDxAddonAsync(Game, kind, null, p));
            default:
                return true;
        }
    }

    /// <summary>
    /// OptiScaler change de nom : hors des noms reserves aux chargeurs du jeu, et hors de d3d12.dll
    /// quand un nom plus sur est libre. Une transaction : l'original est sauvegarde, le proprietaire garde.
    /// </summary>
    private bool MoveOptiScaler()
    {
        var dir = TargetDir;
        var current = FrameGenService.LoadedOptiScaler(dir);
        if (current is null) return true;
        var reserved = ModRules.ReservedProxies(dir);
        if (!reserved.Contains(current) && current != "d3d12.dll") return true;
        if (FrameGenService.PickProxyName(dir) is not { } to || to == current)
        {
            _notify(Loc.T("nexus.err.proxy_full", current), true);
            return false;
        }

        var from = Path.Combine(dir, current);
        var owner = _svc.Deployments.For(Game.Id).FirstOrDefault(e => string.Equals(e.Path, from, StringComparison.OrdinalIgnoreCase));
        var keep = Path.Combine(AppPaths.Cache, $"optiscaler-{Guid.NewGuid():N}.dll");
        File.Copy(from, keep, overwrite: true);
        var tx = new FileTransaction(Game, _svc.Backups, _svc.Deployments, owner?.Origin ?? "OptiScaler");
        tx.Copy(keep, Path.Combine(dir, to), owner?.Component ?? "OptiScaler", owner?.Version ?? "", track: owner is not null);
        tx.Delete(from);
        var result = tx.Commit();
        try { File.Delete(keep); } catch { /* cache */ }
        if (!result.Success) { _notify(result.Message, true); return false; }
        Log.Info(Src, $"{Game.Name}: OptiScaler {current} → {to}");
        _notify(Loc.T("nexus.opti_moved", current, to), false);
        return true;
    }

    /// <summary>
    /// Apres le retrait d'un mod Nexus : ce que le mod a cree en jeu dans son propre dossier (reglages,
    /// journaux) est mis de cote — annulable depuis Modifications — puis ses dossiers vides partent.
    /// </summary>
    private int CleanModLeftovers(List<string> removedDirs)
    {
        var tracked = _svc.Changes.For(Game.Id).Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = ModCleanup.OrphanDirs(Game, tracked)
            .Where(o => removedDirs.Any(d => (d + Path.DirectorySeparatorChar).StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var items = orphans.SelectMany(o => Directory.EnumerateFiles(o, "*", SearchOption.AllDirectories))
            .Select(f => new CleanItem { Path = f, Action = CleanAction.Remove, Source = ModCleanup.Source, Display = Path.GetFileName(f) })
            .ToList();
        var done = 0;
        if (items.Count > 0 && _svc.Cleaner.Execute(Game, new CleanPlan { Items = items }).Success) done += items.Count;
        done += ModCleanup.PruneEmpty(Game);
        return done;
    }

    /// <summary>Ou les reparations ecrivent : a cote de l'exe, de Streamline et des runtimes NGX.</summary>
    private IEnumerable<string> HealDirectories()
        => new[] { TargetDir }
            .Concat(Game.StreamlineDirectories)
            .Concat(Dlss5PackageInstaller.RuntimeDirectories(Game))
            .Concat(Dlss5PackageInstaller.NeuralRuntimeDirectories(Game));

    // ----------------------------------------------------- Migrer vers Prism

    /// <summary>
    /// Reprend une installation posee par un autre outil : ses fichiers et restes sont mis a l'abri
    /// (nettoyage profond, annulable), puis Prism reinstalle les memes familles — ReShade, addon
    /// RenoDX DLSS de la meme variante, MFG Unlock, HDR RenoDX — dans leur version verifiee et suivie.
    /// Si une installation echoue, tout est remis tel qu'avant la migration.
    /// </summary>
    private async Task MigrateAsync()
    {
        if (!Guard()) return;

        var prism = _svc.Changes.For(Game.Id).Where(e => e.StillApplies).Select(e => e.Origin).ToList();
        var kind = Dlss5Addon.All.FirstOrDefault(a => File.Exists(Path.Combine(TargetDir, a.FileName)));
        var reShade = Game.HasReShade && !Profile.ReShadeInstalled;
        var mfg = Game.HasMfgUnlock && File.Exists(Path.Combine(TargetDir, "renodx-mfgunlock.addon64"))
                  && !prism.Any(o => o.Contains("MFG", StringComparison.OrdinalIgnoreCase));
        var hdr = Game.HasRenoDx && !prism.Any(o => o.StartsWith("RenoDX HDR", StringComparison.OrdinalIgnoreCase));
        var hdrPlan = HdrPlan;

        var plan = _svc.Cleaner.Plan(Game);
        var cleaned = !plan.IsEmpty && _svc.Cleaner.Execute(Game, plan).Success;
        DllDetector.Inspect(Game);

        // Photographie apres le nettoyage : en cas d'echec, on defait d'abord les installations
        // (retour a l'etat nettoye), puis le nettoyage lui-meme — dans cet ordre, les originaux
        // rendus par le nettoyage retrouvent exactement leur place.
        using var snapshot = FileSnapshot.Take(HealDirectories());
        var ok = true;
        // L'addon RenoDX DLSS exige ReShade en version add-on : pose s'il manque apres le nettoyage.
        DllDetector.Inspect(Game);
        if (reShade || (kind is not null && !HdrInstaller.ReShadeReady(Game))) ok = await InstallReShadeAsync();
        if (ok && kind is not null) ok = await RunAsync(p => _svc.Dlss5.InstallRenoDxAddonAsync(Game, kind, null, p));
        if (ok && mfg) ok = await RunAsync(p => _svc.FrameGen.InstallMfgAdaAsync(Game, p));
        if (ok && hdr && hdrPlan?.CanInstall == true) ok = await RunAsync(p => _svc.Hdr.ApplyAsync(Game, hdrPlan, p));

        if (!ok)
        {
            snapshot.Restore();
            if (cleaned) _svc.Cleaner.Undo(Game);
            DllDetector.Inspect(Game);
            _notify(Loc.T("migrate.rolled_back", Game.Name), true);
        }
        else _notify(Loc.T("migrate.done", Game.Name, plan.Items.Count), false);

        Refresh();
    }

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
            case DiagnosisFix.Clean:
                // Le nettoyage montre d'abord sa liste, page Modifications : rien n'est retire d'ici.
                AnalyzeClean();
                _notify(Loc.T("diag.clean_ready", CleanPlan.Items.Count), false);
                break;
            case DiagnosisFix.Streamline:
                await DeployStreamlineAsync();
                break;
            case DiagnosisFix.Migrate:
                await MigrateAsync();
                return;
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
        DlssPresets = BuildDlssPresets();
        LoadDlssPreset();

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
        // La barre de travail s'affiche avant que l'installation ne commence.
        await Ui.Yield();
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

    // ------------------------------------------------------- Nettoyage profond

    private CleanPlan _cleanPlan = CleanPlan.Empty;

    /// <summary>Plan du dernier examen : chaque fichier, ce qui lui arrivera, et d'ou il vient.</summary>
    public CleanPlan CleanPlan
    {
        get => _cleanPlan;
        private set
        {
            Set(ref _cleanPlan, value);
            OnPropertyChanged(nameof(HasCleanPlan));
            RunCleanCommand.Raise();
        }
    }

    public bool HasCleanPlan => !CleanPlan.IsEmpty;

    private bool _cleanScanned;
    public string CleanSummary => _cleanScanned ? CleanPlan.Summary : Loc.T("clean.hint");

    public bool CanUndoClean => _svc.Cleaner.LastSession(Game, out _) is not null;

    /// <summary>Jeu Steam : sa verification d'integrite remet ce qu'aucune sauvegarde ne couvre.</summary>
    public bool IsSteam => Game.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase);

    public RelayCommand AnalyzeCleanCommand { get; }
    public RelayCommand RunCleanCommand { get; }
    public RelayCommand UndoCleanCommand { get; }
    public RelayCommand CopyFileListCommand { get; }
    public RelayCommand SteamVerifyCommand { get; }
    public RelayCommand ChooseExeCommand { get; }

    private void AnalyzeClean()
    {
        CleanPlan = _svc.Cleaner.Plan(Game);
        _cleanScanned = true;
        OnPropertyChanged(nameof(CleanSummary));
    }

    private void RunClean()
    {
        if (!Guard()) return;
        // Le plan est recalcule : le disque a pu changer depuis l'examen affiche.
        var plan = _svc.Cleaner.Plan(Game);
        var result = _svc.Cleaner.Execute(Game, plan);
        _notify(result.Message, !result.Success);
        AnalyzeClean();
        OnPropertyChanged(nameof(CanUndoClean));
        Refresh();
    }

    private void UndoClean()
    {
        if (!Guard()) return;
        var result = _svc.Cleaner.Undo(Game);
        _notify(result.Message, !result.Success);
        AnalyzeClean();
        OnPropertyChanged(nameof(CanUndoClean));
        Refresh();
    }

    /// <summary>Liste complete dans le presse-papiers : ce que Prism a pose, et tout le reste.</summary>
    private void CopyFileList()
    {
        if (!_cleanScanned) AnalyzeClean();
        try
        {
            System.Windows.Clipboard.SetText(_svc.Cleaner.FileList(Game, CleanPlan));
            _notify(Loc.T("clean.copied"), false);
        }
        catch (Exception ex) { _notify(Loc.T("err.open_failed", ex.Message), true); }
    }

    /// <summary>Commande documentee du client Steam (steam://validate/&lt;appid&gt;).</summary>
    private void SteamVerify()
    {
        if (!IsSteam) return;
        OpenUrl("steam://validate/" + Game.Id["steam:".Length..]);
    }

    /// <summary>
    /// Executable choisi a la main : il prime sur la detection, ici et aux prochains scans. Le
    /// dossier cible — ou tout est pose — le suit.
    /// </summary>
    private void ChooseExe()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("dialog.game_exe"),
            Filter = Loc.T("dialog.exe_filter"),
            InitialDirectory = Directory.Exists(TargetDir) ? TargetDir : Game.InstallDir,
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;

        _svc.Settings.Current.ExeOverrides[Game.Id] = dlg.FileName;
        _svc.Settings.Save();
        _svc.Scanner.ExeOverrides[Game.Id] = dlg.FileName;

        Game.Executable = dlg.FileName;
        DllDetector.Inspect(Game);
        _notify(Loc.T("library.exe_set", Game.Name, Path.GetFileName(dlg.FileName)), false);
        Refresh();
        OnPropertyChanged(string.Empty);
    }

    // ------------------------------------------------------------- Identite

    /// <summary>« AppID 3751260 · magasin Steam » : ce qui relie ce jeu aux catalogues.</summary>
    public string IdentityLabel => Game.SteamAppId is { } id
        ? Loc.T("ident.label", id, SourceLabel)
        : Loc.T("ident.none", SourceLabel);

    private string SourceLabel => new GameIdentity { Source = Game.IdentitySource }.SourceLabel;

    public bool IsIdentified => Game.SteamAppId is not null;

    private bool _identifying;
    /// <summary>Panneau de recherche ouvert.</summary>
    public bool Identifying { get => _identifying; set => Set(ref _identifying, value); }

    private string _identitySearch = "";
    public string IdentitySearch
    {
        get => _identitySearch;
        set { if (Set(ref _identitySearch, value)) SearchIdentityCommand?.Raise(); }
    }

    /// <summary>Un jeu Steam a deja son identite certaine : rien a choisir.</summary>
    public bool CanIdentify => Game.Platform != GamePlatform.Steam;

    public ObservableCollection<IdentityCandidate> IdentityCandidates { get; } = new();

    public RelayCommand ToggleIdentifyCommand { get; }
    public AsyncRelayCommand SearchIdentityCommand { get; }
    public RelayCommand UseCandidateCommand { get; }
    public RelayCommand UseCustomNameCommand { get; }

    private void ToggleIdentify()
    {
        Identifying = !Identifying;
        if (!Identifying) return;
        IdentitySearch = Game.Name;
        _ = SearchIdentityAsync();
    }

    private async Task SearchIdentityAsync()
    {
        IdentityCandidates.Clear();
        foreach (var c in await _svc.Identity.SearchAsync(IdentitySearch)) IdentityCandidates.Add(c);
        if (IdentityCandidates.Count == 0) _notify(Loc.T("ident.no_result", IdentitySearch), false);
    }

    private void UseIdentity(string name, long? appId)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _svc.Identity.SetByUser(Game, name, appId);
        Identifying = false;
        _notify(Loc.T("ident.set", Game.Name), false);
        Refresh();
        OnPropertyChanged(string.Empty);
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
        if (option.Backend is FgBackend.OptiScaler or FgBackend.InjectedDlssG)
            ApplyFgConfig(option.Backend, Multiplier);
    }

    /// <summary>
    /// Configuration OptiScaler de la voie de generation : entree DLSS-G du jeu, upscaler du jeu
    /// (OptiFG), ou vrai DLSS-G injecte. Chaque voie a ses propres cles.
    /// </summary>
    private void ApplyFgConfig(FgBackend backend, int multiplier)
    {
        var nativeFg = Game.HasDlssG || Game.HasStreamline;
        var result = backend == FgBackend.InjectedDlssG
            ? OptiScalerConfig.Apply(TargetDir, OptiProfile.InjectedFg, multiplier, nativeFg: false)
            : OptiScalerConfig.Apply(TargetDir, OptiProfile.MfgOnly, multiplier,
                nativeFg: nativeFg);
        _notify(result.Message, !result.Success);
        OnPropertyChanged(nameof(OptiProfileLabel));
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
        foreach (var g in _svc.Changes.InstalledFor(Game.Id))
        {
            // « … · external » : un mod etranger mis de cote, pas une installation. Son « Retirer »
            // remettrait le mod dans le jeu ; il vit dans l'historique des modifications, pas ici.
            if (g.Origin.EndsWith("· external", StringComparison.OrdinalIgnoreCase)) continue;

            // Version inscrite « 5 » par d'anciennes builds pour « RenoDX DLSS 5 » : le nom repete.
            Installed.Add(g.Version is { } v && g.Origin.EndsWith(" " + v, StringComparison.Ordinal)
                ? new InstalledGroup { Origin = g.Origin, At = g.At, Summary = g.Summary, Files = g.Files }
                : g);
        }

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
        var prismChanges = _svc.Changes.For(Game.Id).ToList();
        foreach (var mod in ForeignModScanner.Scan(Game, prismChanges.Select(c => c.Path), Profile.ReShadeInstalled,
                     prismChanges.Select(c => c.Origin)))
            Detected.Add(mod);

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
        int done;
        try { done = RemoveGroup(group); }
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

    /// <summary>Retire une installation et ce qu'elle avait inscrit ; renvoie le nombre d'elements defaits.</summary>
    private int RemoveGroup(InstalledGroup group)
    {
        var origin = group.Origin;
        var dir = TargetDir;
        var done = 0;

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

            var nexus = origin.StartsWith("Nexus · ", StringComparison.Ordinal);
            var removedDirs = nexus
                ? _svc.Deployments.For(Game.Id).Where(e => e.Origin == origin).Select(e => Path.GetFullPath(Path.GetDirectoryName(e.Path)!)).Distinct().ToList()
                : new List<string>();
            done += _svc.Changes.RevertOrigin(Game.Id, origin);
            if (nexus)
            {
                NexusModInstaller.Forget(Game.Id, origin);
                done += CleanModLeftovers(removedDirs);
            }

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
        return done;
    }

    // ------------------------------------------------- Reinitialiser les injections

    private bool _resetArmed;
    /// <summary>Confirmation affichee sur place : rien n'est retire avant le second clic.</summary>
    public bool ResetArmed { get => _resetArmed; private set => Set(ref _resetArmed, value); }

    private string _resetSummary = "";
    public string ResetSummary { get => _resetSummary; private set => Set(ref _resetSummary, value); }

    public RelayCommand ArmResetCommand { get; }
    public RelayCommand ConfirmResetCommand { get; }
    public RelayCommand CancelResetCommand { get; }

    /// <summary>Ce que la reinitialisation va defaire, compte avant d'agir.</summary>
    private void ArmReset()
    {
        var prism = _svc.Changes.For(Game.Id).Count(c => c.StillApplies);
        var foreign = _svc.Cleaner.Plan(Game).Items.Count;
        ResetSummary = Loc.T("reset.summary", Installed.Count, prism, foreign);
        ResetArmed = true;
    }

    /// <summary>
    /// Remet le jeu tel que sa plateforme l'a installe, en trois temps :
    ///  1. chaque installation Prism, par son propre retrait (ReShade, HDR et Engine.ini, addons
    ///     et leurs inscriptions dans ReShade.ini) ;
    ///  2. ce que le registre connait encore, y compris les fichiers modifies depuis ;
    ///  3. tout le reste — autres installeurs, OptiScaler, mods poses a la main — par le nettoyage profond,
    ///     qui met chaque fichier a l'abri et reste annulable.
    /// Le profil repart de zero. L'executable choisi et l'identite du jeu sont gardes : ce ne
    /// sont pas des injections.
    /// </summary>
    private void ConfirmReset()
    {
        ResetArmed = false;
        if (!Guard()) return;

        var done = 0;
        var failed = new List<string>();

        // Les mods « mis de cote » (origine « … · external ») ne sont pas des injections : les
        // defaire remettrait le mod etranger dans le jeu, l'inverse d'une reinitialisation.
        var groups = _svc.Changes.InstalledFor(Game.Id).Concat(Installed)
            .Where(g => !g.Origin.EndsWith("· external", StringComparison.OrdinalIgnoreCase))
            .GroupBy(g => g.Origin).Select(g => g.First()).ToList();
        foreach (var group in groups)
        {
            try { done += RemoveGroup(group); }
            catch (Exception ex)
            {
                failed.Add(group.Origin);
                Log.Warn(Src, $"Reset: cannot remove {group.Origin}: {ex.Message}");
            }
        }

        done += _svc.Restore.RestoreVanilla(Game, includeOrphans: false, force: true).FilesChanged;

        var plan = _svc.Cleaner.Plan(Game);
        if (!plan.IsEmpty) done += _svc.Cleaner.Execute(Game, plan).FilesChanged;

        if (CanChooseDlssPreset)
            NvDriverSettings.Clear(ExeName, NvDriverSettings.DlssSrOverride, NvDriverSettings.DlssSrPresetSelection);

        Profile = new GameProfile { GameId = Game.Id };
        _svc.Profiles.Update(Profile);
        DllDetector.Inspect(Game);

        var msg = done > 0 ? Loc.T("reset.done", Game.Name, done) : Loc.T("reset.nothing", Game.Name);
        if (failed.Count > 0) msg += " " + Loc.T("restore.refused", string.Join(", ", failed));
        Log.Info(Src, $"Reset {Game.Name}: {done} item(s), {failed.Count} refused");
        _notify(msg, failed.Count > 0);
        Build();
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
        Add(Loc.T("label.engine"), Game.EngineLabel ?? "n/d", Game.Engine is not null, UiStatus.Detected);
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
            RunCleanCommand?.Raise();
            UndoCleanCommand?.Raise();
            ChooseExeCommand?.Raise();
            HealCommand?.Raise();
            ReinstallDlss5Command?.Raise();
            ReinstallHdrCommand?.Raise();
            ReinstallFgCommand?.Raise();
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
        LoadDlssPreset();
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
        OnPropertyChanged(nameof(LegacyNote));
        OnPropertyChanged(nameof(HasLegacyNote));
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
        RefreshInstallStates();
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

        // Le registre d'abord : il connait chaque fichier pose, compagnons compris (fakenvapi.dll
        // livre avec OptiScaler), et rend les originaux remplaces. Le balayage par nom ne rattrape
        // ensuite que ce qu'une installation hors registre aurait laisse.
        var tracked = new[] { "OptiScaler", FrameGenService.InjectedFgOrigin, "RTX40MFG-Unlock", "MFGAdaUnlock" }
            .Sum(origin => _svc.Changes.RevertOrigin(Game.Id, origin));
        var result = FrameGenService.RemoveOverlays(Game);
        if (tracked > 0 && !result.Success)
            result = new InstallResult(true, Loc.T("restore.done.removed", tracked), tracked);
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

    /// <summary>ReShade pret a charger addons et presets ; installe s'il manque.</summary>
    public Task<bool> EnsureReShadeAsync()
        => HdrInstaller.ReShadeReady(Game) ? Task.FromResult(true) : InstallReShadeAsync();

    private async Task<bool> InstallReShadeAsync()
    {
        if (!Guard()) return false;
        Busy = true;
        Progress = 0;
        await Ui.Yield();
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
