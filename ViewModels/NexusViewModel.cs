using System.Collections.ObjectModel;
using Prism.Core;
using Prism.Models;
using Prism.Services;

namespace Prism.ViewModels;

/// <summary>Un prerequis affiche sous la barre : etat dans le jeu cible, page a ouvrir.</summary>
public sealed class RequirementRow : ObservableObject
{
    public RequirementRow(ModRequirement req, Action<string> open)
    {
        Req = req;
        OpenCommand = new RelayCommand(_ => open(req.Page));
    }

    public ModRequirement Req { get; }
    public string Name => Req.Depth > 1 ? "↳ " + Req.Name : Req.Name;
    public string? Notes => string.IsNullOrWhiteSpace(Req.Notes) ? null : Req.Notes;

    private RequirementState _state = RequirementState.Checking;
    public RequirementState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(CanOpen));
        }
    }

    public UiStatus Status => State switch
    {
        RequirementState.Installed => UiStatus.Ready,
        RequirementState.Missing => UiStatus.Error,
        RequirementState.External => UiStatus.Warning,
        _ => UiStatus.Idle
    };

    public string StateLabel => State switch
    {
        RequirementState.Installed => Loc.T("nexus.req.installed"),
        RequirementState.Missing => Loc.T("nexus.req.missing"),
        RequirementState.External => Loc.T("nexus.req.external"),
        _ => Loc.T("nexus.req.checking")
    };

    public bool CanOpen => State != RequirementState.Installed;
    public RelayCommand OpenCommand { get; }

    public void Relocalize() => OnPropertyChanged(nameof(StateLabel));
}

public enum ModVersionState { Checking, UpToDate, Update, Unknown, Updated }

/// <summary>Un mod Nexus installe dans le jeu cible : version en place, derniere version.</summary>
public sealed class ModUpdateRow : ObservableObject
{
    public ModUpdateRow(NexusInstall install, Func<ModUpdateRow, Task> update)
    {
        Install = install;
        UpdateCommand = new AsyncRelayCommand(_ => update(this), _ => State == ModVersionState.Update);
    }

    public NexusInstall Install { get; }
    public ModUpdateInfo? Info { get; private set; }
    public string Name => Install.Origin.StartsWith("Nexus · ", StringComparison.Ordinal) ? Install.Origin[8..] : Install.Origin;
    public string InstalledVersion => Info?.InstalledVersion ?? Install.Version ?? "?";
    public string? LatestVersion => Info?.LatestVersion;

    private ModVersionState _state = ModVersionState.Checking;
    public ModVersionState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(CanUpdate));
            UpdateCommand.Raise();
        }
    }

    public void Apply(ModUpdateInfo info)
    {
        Info = info;
        OnPropertyChanged(nameof(InstalledVersion));
        OnPropertyChanged(nameof(LatestVersion));
        State = !info.Known ? ModVersionState.Unknown : info.HasUpdate ? ModVersionState.Update : ModVersionState.UpToDate;
    }

    public UiStatus Status => State switch
    {
        ModVersionState.UpToDate or ModVersionState.Updated => UiStatus.Ready,
        ModVersionState.Update => UiStatus.Warning,
        _ => UiStatus.Idle
    };

    public string StateLabel => State switch
    {
        ModVersionState.UpToDate => Loc.T("nexus.mods.state.uptodate"),
        ModVersionState.Updated => Loc.T("nexus.mods.state.updated"),
        ModVersionState.Update => Loc.T("nexus.mods.state.update", LatestVersion ?? "?"),
        ModVersionState.Unknown => Loc.T("nexus.mods.state.unknown"),
        _ => Loc.T("nexus.req.checking")
    };

    public bool CanUpdate => State == ModVersionState.Update;
    public AsyncRelayCommand UpdateCommand { get; }

    public void Relocalize() => OnPropertyChanged(nameof(StateLabel));
}

/// <summary>
/// Page Nexus Mods : le site dans Prism, sur la page du jeu cible. Un telechargement lance
/// sur le site arrive ici, est lu, puis pose au bon endroit et suivi comme le reste.
/// Le controle WebView2 vit dans la vue ; il demande ses adresses ici et y remet ses fichiers.
/// </summary>
public sealed class NexusViewModel : ObservableObject
{
    private const string Src = "nexus";

    private readonly AppServices _svc;
    private readonly Func<GameDetailViewModel?> _detail;
    private readonly Func<IEnumerable<GameInfo>> _games;
    private readonly Action<string, bool> _notify;

    public NexusViewModel(AppServices svc, Func<GameDetailViewModel?> detail, Func<IEnumerable<GameInfo>> games,
        Action<string, bool> notify)
    {
        _svc = svc;
        _detail = detail;
        _games = games;
        _notify = notify;

        InstallToRootCommand = new AsyncRelayCommand(_ => ChooseAsync(ModLayout.GameRoot), _ => Pending is not null && !Busy);
        InstallToExeCommand = new AsyncRelayCommand(_ => ChooseAsync(ModLayout.ExeDir), _ => Pending is not null && !Busy);
        DismissCommand = new RelayCommand(_ => Pending = null);
        DownloadAllCommand = new AsyncRelayCommand(_ => StartQueueAsync(), _ => HasMissing && !QueueActive);
        StopQueueCommand = new RelayCommand(_ => StopQueue(), _ => QueueActive);

        ToggleAccountCommand = new RelayCommand(_ => ShowAccount = !ShowAccount);
        SaveKeyCommand = new AsyncRelayCommand(_ => SaveKeyAsync());
        ForgetKeyCommand = new RelayCommand(_ => { _svc.NexusAccount.Forget(); AccountError = null; });
        OpenKeysPageCommand = new RelayCommand(_ => NavigateRequested?.Invoke(NexusAccount.KeysPage));
        _svc.NexusAccount.Changed += RaiseAccount;

        ToggleInstalledCommand = new RelayCommand(_ => ShowInstalled = !ShowInstalled);
        CheckUpdatesCommand = new AsyncRelayCommand(_ => RefreshInstalledAsync(), _ => !QueueActive);
        UpdateAllCommand = new AsyncRelayCommand(_ => UpdateAsync(InstalledMods.Where(r => r.State == ModVersionState.Update).ToList()),
            _ => UpdateCount > 0 && !QueueActive);
    }

    /// <summary>La vue navigue quand on le lui demande : changement de jeu, lien nxm.</summary>
    public event Action<string>? NavigateRequested;

    // ------------------------------------------------------------------- Etat

    private string _url = "";
    public string Url { get => _url; set => Set(ref _url, value); }

    private string? _status;
    /// <summary>Derniere action : telechargement, analyse, resultat.</summary>
    public string? Status { get => _status; private set => Set(ref _status, value); }

    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private bool _busy;
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) OnPropertyChanged(nameof(ShowProgress)); } }

    public bool ShowProgress => Busy;

    private bool _runtimeMissing;
    /// <summary>Runtime WebView2 absent (Windows 10 epure) : la page propose de l'installer.</summary>
    public bool RuntimeMissing { get => _runtimeMissing; set => Set(ref _runtimeMissing, value); }

    private ModPlan? _pending;
    /// <summary>Archive dont l'emplacement n'est pas sur : en attente du choix de l'utilisateur.</summary>
    public ModPlan? Pending
    {
        get => _pending;
        private set
        {
            Set(ref _pending, value);
            PendingFiles.Clear();
            if (value is not null)
                foreach (var f in value.Unmapped.Take(200)) PendingFiles.Add(f);
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(PendingTitle));
            OnPropertyChanged(nameof(PendingNote));
        }
    }

    public bool HasPending => Pending is not null;
    public ObservableCollection<string> PendingFiles { get; } = new();
    public string PendingTitle => Pending is null ? "" : Loc.T("nexus.pending", Pending.Name, Pending.Unmapped.Count);
    public string? PendingNote => Pending?.Note;

    // ------------------------------------------------------------ Prerequis

    /// <summary>Prerequis du mod dont la page est ouverte, avec leur etat dans le jeu cible.</summary>
    public ObservableCollection<RequirementRow> Requirements { get; } = new();

    private (string Domain, long ModId)? _reqFor;
    private CancellationTokenSource? _reqCts;

    private bool _onModPage;
    public bool OnModPage { get => _onModPage; private set { if (Set(ref _onModPage, value)) OnPropertyChanged(nameof(NoRequirements)); } }

    private bool _reqLoaded;
    public bool NoRequirements => OnModPage && _reqLoaded && Requirements.Count == 0;
    public bool HasRequirements => Requirements.Count > 0;

    /// <summary>Prerequis absents du jeu et telechargeables sur Nexus.</summary>
    public int MissingCount => Requirements.Count(r => r.State == RequirementState.Missing);
    public bool HasMissing => MissingCount > 0;

    /// <summary>« PREREQUIS », ou « Fichiers requis necessaires · 2 manquants » quand il en manque.</summary>
    public string RequirementsHeader => HasMissing ? Loc.T("nexus.req.needed", MissingCount) : Loc.T("nexus.req.header");
    public string DownloadAllLabel => Loc.T("nexus.req.download_all", MissingCount);

    private void RaiseMissing()
    {
        OnPropertyChanged(nameof(MissingCount));
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(RequirementsHeader));
        OnPropertyChanged(nameof(DownloadAllLabel));
        DownloadAllCommand.Raise();
    }

    // ------------------------------------------------------- Tout telecharger

    /// <summary>
    /// Prerequis manquants, les plus profonds d'abord (RED4ext avant ArchiveXL). Nexus exige un
    /// telechargement lance par l'utilisateur, fichier par fichier : Prism ouvre chaque prerequis
    /// sur la page de son fichier principal, attend l'archive, l'installe, puis passe au suivant.
    /// </summary>
    /// <summary>Un fichier a obtenir : prerequis manquant, ou nouvelle version d'un mod installe.</summary>
    private sealed record QueueItem(string Name, string Domain, long GameId, long ModId, long? FileId, string? OriginOverride, Action? Done);

    private readonly Queue<QueueItem> _queue = new();
    private QueueItem? _queued;
    private bool _premiumRun;
    private int _queueTotal;
    private string? _returnUrl;

    private bool _queueActive;
    public bool QueueActive
    {
        get => _queueActive;
        private set
        {
            if (!Set(ref _queueActive, value)) return;
            DownloadAllCommand.Raise();
            StopQueueCommand.Raise();
            UpdateAllCommand.Raise();
            CheckUpdatesCommand.Raise();
        }
    }

    public AsyncRelayCommand DownloadAllCommand { get; }
    public RelayCommand StopQueueCommand { get; }

    private Task StartQueueAsync()
    {
        var items = Requirements.Where(r => r.State == RequirementState.Missing).OrderByDescending(r => r.Req.Depth)
            .Select(r => new QueueItem(r.Req.Name, r.Req.GameDomain, r.Req.GameId, r.Req.ModId, null, null,
                () => r.State = RequirementState.Installed))
            .ToList();
        return RunQueueAsync(items);
    }

    /// <summary>
    /// Premium : tout est telecharge et installe en arriere-plan, par l'API. Compte gratuit : Nexus
    /// exige un clic par fichier, Prism ouvre chaque page et enchaine apres chaque installation.
    /// </summary>
    private async Task RunQueueAsync(List<QueueItem> items)
    {
        if (items.Count == 0) return;
        await _svc.NexusAccount.RefreshAsync();
        if (_svc.NexusAccount.IsPremium) { await RunPremiumAsync(items); return; }

        _queue.Clear();
        foreach (var i in items) _queue.Enqueue(i);
        _queueTotal = _queue.Count;
        _returnUrl = Url;
        QueueActive = true;
        await NextInQueueAsync();
    }

    private async Task RunPremiumAsync(List<QueueItem> items)
    {
        _premiumRun = true;
        QueueActive = true;
        var done = new List<string>();
        var failed = new List<string>();
        try
        {
            for (var i = 0; i < items.Count && QueueActive; i++)
            {
                var item = items[i];
                SetStatus(Loc.T("nexus.premium.step", i + 1, items.Count, item.Name), false);
                try
                {
                    var fileId = item.FileId ?? await _svc.Requirements.MainFileIdAsync(item.GameId, item.ModId)
                                 ?? throw new InvalidOperationException(Loc.T("nexus.acct.err.no_link"));
                    var uri = await _svc.NexusAccount.DownloadLinkAsync(item.Domain, item.ModId, fileId);
                    var name = Uri.UnescapeDataString(Path.GetFileName(new Uri(uri).AbsolutePath));
                    var path = DownloadPathFor(name);
                    if (File.Exists(path)) File.Delete(path);

                    Busy = true;
                    await _svc.Downloads.DownloadAsync(uri, path, null, new Progress<double>(p => Progress = p));
                    var page = NexusService.FilePage(new NxmLink(item.Domain, item.ModId, fileId));
                    if (await OnDownloadedAsync(path, page, $"{item.Name} at Nexus", item.OriginOverride))
                    {
                        item.Done?.Invoke();
                        done.Add(item.Name);
                    }
                    else failed.Add(item.Name);
                }
                catch (Exception ex)
                {
                    Log.Warn(Src, $"Premium download of {item.Name}: {ex.Message}");
                    failed.Add($"{item.Name} ({ex.Message})");
                }
            }
        }
        finally
        {
            _premiumRun = false;
            QueueActive = false;
            Busy = false;
            Progress = 0;
        }

        var summary = Loc.T("nexus.premium.done", done.Count, items.Count);
        if (failed.Count > 0) summary += " · " + Loc.T("nexus.premium.failed", string.Join(", ", failed));
        SetStatus(summary, failed.Count > 0);
        _notify(summary, failed.Count > 0);
        if (_reqFor is { } shown) await CheckRequirementsAsync(shown.Domain, CancellationToken.None);
        await RefreshInstalledAsync();
    }

    private async Task NextInQueueAsync()
    {
        while (_queue.Count > 0)
        {
            var item = _queue.Dequeue();
            _queued = item;
            var step = _queueTotal - _queue.Count;
            var url = $"{NexusService.Site}/{item.Domain}/mods/{item.ModId}?tab=files";
            try
            {
                var fileId = item.FileId ?? await _svc.Requirements.MainFileIdAsync(item.GameId, item.ModId);
                if (fileId is { } f) url = NexusService.FilePage(new NxmLink(item.Domain, item.ModId, f));
            }
            catch (Exception ex) { Log.Warn(Src, $"Main file of {item.Name}: {ex.Message}"); }
            SetStatus(Loc.T("nexus.req.queue_step", step, _queueTotal, item.Name), false);
            NavigateRequested?.Invoke(url);
            return;
        }

        // Tout est la : retour sur la page du mod, liste reverifiee.
        _queued = null;
        QueueActive = false;
        SetStatus(Loc.T("nexus.req.queue_done"), false);
        if (_returnUrl is { } back) NavigateRequested?.Invoke(back);
        await RefreshInstalledAsync();
    }

    private void StopQueue()
    {
        _queue.Clear();
        _queued = null;
        QueueActive = false;
        SetStatus(Loc.T("nexus.req.queue_stopped"), false);
    }

    /// <summary>Page ouverte dans le navigateur : si c'est un mod, ses prerequis.</summary>
    public async Task OnPageChangedAsync(string url)
    {
        if (QueueActive) return;
        var domain = NexusService.DomainOf(url);
        var modId = NexusService.ModIdOf(url);
        OnModPage = domain is not null && modId is not null;
        if (!OnModPage) { ClearRequirements(); _reqFor = null; return; }
        if (_reqFor == (domain!, modId!.Value)) return;
        await LoadRequirementsAsync(domain!, modId!.Value);
    }

    private void ClearRequirements()
    {
        _reqCts?.Cancel();
        Requirements.Clear();
        RaiseMissing();
        _reqLoaded = false;
        OnPropertyChanged(nameof(HasRequirements));
        OnPropertyChanged(nameof(NoRequirements));
    }

    private async Task LoadRequirementsAsync(string domain, long modId)
    {
        ClearRequirements();
        _reqFor = (domain, modId);
        var cts = _reqCts = new CancellationTokenSource();
        try
        {
            var list = await _svc.Requirements.AllAsync(domain, modId, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var r in list)
            {
                var row = new RequirementRow(r, url => NavigateRequested?.Invoke(url));
                row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RequirementRow.State)) RaiseMissing(); };
                Requirements.Add(row);
            }
            _reqLoaded = true;
            OnPropertyChanged(nameof(HasRequirements));
            OnPropertyChanged(nameof(NoRequirements));
            await CheckRequirementsAsync(domain, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn(Src, $"Requirements of {domain}/{modId}: {ex.Message}"); }
    }

    /// <summary>Etat de chaque prerequis dans le jeu de la bibliotheque qui correspond a ce domaine.</summary>
    private async Task CheckRequirementsAsync(string domain, CancellationToken ct)
    {
        var game = GameFor(domain);
        foreach (var row in Requirements.ToList())
        {
            if (ct.IsCancellationRequested) return;
            if (game is null && !row.Req.External) continue;
            try { row.State = game is null ? RequirementState.External : await _svc.Requirements.StateAsync(game, row.Req, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn(Src, $"{row.Req.Name}: {ex.Message}"); }
        }
    }

    /// <summary>Le jeu cible s'il correspond, sinon celui de la bibliotheque dont le domaine est connu.</summary>
    private GameInfo? GameFor(string domain)
    {
        var target = _detail()?.Game;
        if (target is not null && _svc.Nexus.CachedDomain(target) == domain) return target;
        return _games().FirstOrDefault(g => _svc.Nexus.CachedDomain(g) == domain);
    }

    public AsyncRelayCommand InstallToRootCommand { get; }
    public AsyncRelayCommand InstallToExeCommand { get; }
    public RelayCommand DismissCommand { get; }

    // ------------------------------------------------------------- Navigation

    /// <summary>Page des mods du jeu cible ; a defaut, la liste des jeux de Nexus.</summary>
    public async Task<string> HomeUrlAsync()
    {
        _ = _svc.NexusAccount.RefreshAsync();
        _ = RefreshInstalledAsync();
        var game = _detail()?.Game;
        if (game is null) return NexusService.GamesPage;
        var domain = await _svc.Nexus.DomainForAsync(game);
        if (domain is null)
        {
            SetStatus(Loc.T("nexus.not_found", game.Name), false);
            return NexusService.GamesPage;
        }
        return NexusService.GamePage(domain);
    }

    public async Task GoHomeAsync() => NavigateRequested?.Invoke(await HomeUrlAsync());

    /// <summary>
    /// « Mod Manager Download » : Prism n'utilise pas l'API de telechargement (reservee aux
    /// applications enregistrees et aux comptes Premium). Il ouvre la page de telechargement
    /// manuel du meme fichier : le telechargement qui en part arrive dans Prism.
    /// </summary>
    public void OnNxm(string uri)
    {
        if (NexusService.ParseNxm(uri) is not { } link) return;
        Log.Info(Src, $"nxm link → manual download page of mod {link.ModId}, file {link.FileId}");
        NavigateRequested?.Invoke(NexusService.FilePage(link));
    }

    // ------------------------------------------------------------ Telechargement

    public string DownloadPathFor(string suggested)
    {
        var name = Path.GetFileName(suggested);
        if (string.IsNullOrWhiteSpace(name)) name = $"nexus-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        return Path.GetFullPath(Path.Combine(NexusService.Downloads, name));
    }

    public void OnDownloadStarted(string file)
    {
        Busy = true;
        Progress = 0;
        SetStatus(Loc.T("nexus.downloading", Path.GetFileName(file)), false);
    }

    public void OnDownloadProgress(long received, long? total)
    {
        if (total is > 0) Progress = received * 100.0 / total.Value;
    }

    public void OnDownloadFailed(string reason)
    {
        Busy = false;
        SetStatus(Loc.T("nexus.err.download", reason), true);
    }

    /// <summary>Archive recue : jeu verifie, contenu lu, puis installation ou choix demande.</summary>
    public async Task<bool> OnDownloadedAsync(string file, string? pageUrl, string? pageTitle, string? originOverride = null)
    {
        Busy = true;
        try
        {
            var detail = _detail();
            if (detail is null) { SetStatus(Loc.T("nexus.err.no_game"), true); return false; }

            var game = detail.Game;
            var domain = NexusService.DomainOf(pageUrl);
            var targetDomain = await _svc.Nexus.DomainForAsync(game);
            if (domain is not null && targetDomain is not null && domain != targetDomain)
            {
                // Le mod vise un autre jeu de la bibliotheque : on le pose la ou il doit aller.
                var other = _games().FirstOrDefault(g => _svc.Nexus.CachedDomain(g) == domain);
                if (other is null)
                {
                    SetStatus(Loc.T("nexus.err.other_game", domain, game.Name), true);
                    return false;
                }
                game = other;
                if (!other.Scanned) DllDetector.Inspect(other);
            }

            SetStatus(Loc.T("nexus.analyzing", Path.GetFileName(file)), false);
            var pageModId = NexusService.ModIdOf(pageUrl);
            var plan = await _svc.NexusMods.AnalyzeAsync(game, file, domain, pageTitle, pageModId: pageModId,
                fileId: NexusService.FileIdOf(pageUrl));
            // Mise a jour demandee depuis la liste : la nouvelle version remplace l'ancienne installation.
            plan.OriginOverride = originOverride
                ?? (QueueActive && _queued is { } q && q.ModId == (plan.ModId ?? pageModId) ? q.OriginOverride : null);

            if (plan.Blocked) { SetStatus(plan.Note!, true); return false; }
            if (plan.Layout == ModLayout.Unknown) { _pendingGame = game; Pending = plan; SetStatus(plan.Note!, false); return false; }

            return await InstallAsync(game, plan);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("err.install_failed", ex.Message), true);
            return false;
        }
        finally
        {
            if (!_premiumRun)
            {
                Busy = false;
                Progress = 0;
            }
        }
    }

    private GameInfo? _pendingGame;

    private async Task ChooseAsync(ModLayout layout)
    {
        if (Pending is not { } plan || _pendingGame is not { } game) return;
        NexusModInstaller.Assign(plan, layout == ModLayout.GameRoot ? game.InstallDir : DllInstaller.TargetDirectory(game), layout);
        Pending = null;
        Busy = true;
        try { await InstallAsync(game, plan); }
        finally { Busy = false; }
    }

    private async Task<bool> InstallAsync(GameInfo game, ModPlan plan)
    {
        var detail = _detail();
        var sameGame = detail?.Game.Id == game.Id;

        // Addon, preset ou shader : ReShade d'abord, par le chemin habituel de Prism.
        if (plan.NeedsReShade && !HdrInstaller.ReShadeReady(game))
        {
            if (!sameGame) { SetStatus(Loc.T("nexus.err.reshade", game.Name), true); return false; }
            SetStatus(Loc.T("nexus.reshade_first"), false);
            if (!await detail!.EnsureReShadeAsync()) { SetStatus(Loc.T("nexus.err.reshade", game.Name), true); return false; }
        }

        var result = _svc.NexusMods.Install(game, plan);
        SetStatus(result.Message, !result.Success);
        _notify(result.Message, !result.Success);
        if (!result.Success) return false;

        DllDetector.Inspect(game);
        if (sameGame) detail!.Refresh();

        // Ce que le nouveau mod a pu mettre en conflit avec l'existant.
        var conflicts = ConflictService.Evaluate(game);
        if (conflicts.Count > 0)
            SetStatus(result.Message + " · " + Loc.T("nexus.conflicts", string.Join(" · ", conflicts.Select(c => c.Title))), true);

        // Prerequis : ceux qui manquent encore sont signales, et la liste de la page est mise a jour.
        if (plan.Domain is { } domain && plan.ModId is { } modId)
        {
            try
            {
                var missing = new List<string>();
                foreach (var req in await _svc.Requirements.AllAsync(domain, modId))
                    if (await _svc.Requirements.StateAsync(game, req) == RequirementState.Missing) missing.Add(req.Name);
                if (missing.Count > 0) SetStatus(result.Message + " · " + Loc.T("nexus.req.still_missing", string.Join(", ", missing)), true);
            }
            catch (Exception ex) { Log.Warn(Src, $"Requirements check after install: {ex.Message}"); }
        }
        if (_reqFor is { } shown) await CheckRequirementsAsync(shown.Domain, CancellationToken.None);

        // File gratuite : le fichier attendu est la, on ouvre le suivant.
        if (QueueActive && !_premiumRun && _queued is { } current && (plan.ModId is null || plan.ModId == current.ModId))
        {
            current.Done?.Invoke();
            await NextInQueueAsync();
        }
        else if (!QueueActive) await RefreshInstalledAsync();
        return true;
    }

    // ------------------------------------------------------------ Compte Nexus

    private bool _showAccount;
    public bool ShowAccount { get => _showAccount; set => Set(ref _showAccount, value); }

    /// <summary>Saisie de la cle API ; jamais affichee une fois enregistree.</summary>
    public string ApiKeyInput { get; set; } = "";

    private string? _accountError;
    public string? AccountError { get => _accountError; private set => Set(ref _accountError, value); }

    public bool HasKey => _svc.NexusAccount.HasKey;
    public bool IsPremium => _svc.NexusAccount.IsPremium;

    public string AccountLabel => !_svc.NexusAccount.HasKey ? Loc.T("nexus.acct.none")
        : !_svc.NexusAccount.Validated ? Loc.T("nexus.acct.checking")
        : _svc.NexusAccount.IsPremium ? Loc.T("nexus.acct.premium", _svc.NexusAccount.UserName ?? "")
        : Loc.T("nexus.acct.free", _svc.NexusAccount.UserName ?? "");

    public RelayCommand ToggleAccountCommand { get; }
    public AsyncRelayCommand SaveKeyCommand { get; }
    public RelayCommand ForgetKeyCommand { get; }
    public RelayCommand OpenKeysPageCommand { get; }

    private async Task SaveKeyAsync()
    {
        AccountError = await _svc.NexusAccount.SetKeyAsync(ApiKeyInput);
        if (AccountError is null)
        {
            ApiKeyInput = "";
            OnPropertyChanged(nameof(ApiKeyInput));
            SetStatus(AccountLabel, false);
        }
    }

    private void RaiseAccount()
    {
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(IsPremium));
        OnPropertyChanged(nameof(AccountLabel));
    }

    // ------------------------------------------------------ Mods installes

    /// <summary>Mods Nexus poses par Prism dans le jeu cible, avec leur version face a la derniere.</summary>
    public ObservableCollection<ModUpdateRow> InstalledMods { get; } = new();

    private bool _showInstalled;
    public bool ShowInstalled { get => _showInstalled; set => Set(ref _showInstalled, value); }

    public bool HasInstalledMods => InstalledMods.Count > 0;
    public int UpdateCount => InstalledMods.Count(r => r.State == ModVersionState.Update);
    public bool HasUpdates => UpdateCount > 0;
    public string InstalledHeader => HasUpdates ? Loc.T("nexus.mods.header", InstalledMods.Count, UpdateCount)
        : Loc.T("nexus.mods.header_ok", InstalledMods.Count);
    public string UpdateAllLabel => Loc.T("nexus.mods.update_all", UpdateCount);

    public RelayCommand ToggleInstalledCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public AsyncRelayCommand UpdateAllCommand { get; }

    private CancellationTokenSource? _modsCts;

    public async Task RefreshInstalledAsync()
    {
        _modsCts?.Cancel();
        var cts = _modsCts = new CancellationTokenSource();
        InstalledMods.Clear();
        RaiseInstalled();
        var game = _detail()?.Game;
        if (game is null) return;

        foreach (var install in NexusModInstaller.Installed().Where(i => i.GameId == game.Id && _svc.Requirements.IsDeployed(game, i)))
        {
            var row = new ModUpdateRow(install, r => UpdateAsync(new List<ModUpdateRow> { r }));
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ModUpdateRow.State)) RaiseInstalled(); };
            InstalledMods.Add(row);
        }
        RaiseInstalled();

        foreach (var row in InstalledMods.ToList())
        {
            if (cts.IsCancellationRequested) return;
            try { row.Apply(await _svc.Requirements.CheckUpdateAsync(row.Install, cts.Token)); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warn(Src, $"Update check of {row.Name}: {ex.Message}");
                row.State = ModVersionState.Unknown;
            }
        }
    }

    private Task UpdateAsync(List<ModUpdateRow> rows)
    {
        var items = rows.Where(r => r.Info is { HasUpdate: true, GameId: not null, LatestFileId: not null, ModId: not null } && r.Install.Domain is not null)
            .Select(r => new QueueItem(r.Name, r.Install.Domain!, r.Info!.GameId!.Value, r.Info.ModId!.Value, r.Info.LatestFileId,
                r.Install.Origin, () => r.State = ModVersionState.Updated))
            .ToList();
        return RunQueueAsync(items);
    }

    private void RaiseInstalled()
    {
        OnPropertyChanged(nameof(HasInstalledMods));
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(InstalledHeader));
        OnPropertyChanged(nameof(UpdateAllLabel));
        UpdateAllCommand.Raise();
    }

    private void SetStatus(string message, bool error)
    {
        Status = message;
        StatusIsError = error;
    }

    public void Relocalize()
    {
        foreach (var r in Requirements) r.Relocalize();
        foreach (var r in InstalledMods) r.Relocalize();
        RaiseMissing();
        RaiseInstalled();
        RaiseAccount();
        OnPropertyChanged(nameof(PendingTitle));
    }
}
