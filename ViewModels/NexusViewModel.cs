using System.Collections.ObjectModel;
using Prism.Core;
using Prism.Models;
using Prism.Services;

namespace Prism.ViewModels;

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
                foreach (var f in value.Files.Take(200)) PendingFiles.Add(f);
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(PendingTitle));
            OnPropertyChanged(nameof(PendingNote));
        }
    }

    public bool HasPending => Pending is not null;
    public ObservableCollection<string> PendingFiles { get; } = new();
    public string PendingTitle => Pending is null ? "" : Loc.T("nexus.pending", Pending.Name, Pending.Files.Count);
    public string? PendingNote => Pending?.Note;

    public AsyncRelayCommand InstallToRootCommand { get; }
    public AsyncRelayCommand InstallToExeCommand { get; }
    public RelayCommand DismissCommand { get; }

    // ------------------------------------------------------------- Navigation

    /// <summary>Page des mods du jeu cible ; a defaut, la liste des jeux de Nexus.</summary>
    public async Task<string> HomeUrlAsync()
    {
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
    public async Task OnDownloadedAsync(string file, string? pageUrl, string? pageTitle)
    {
        Busy = true;
        try
        {
            var detail = _detail();
            if (detail is null) { SetStatus(Loc.T("nexus.err.no_game"), true); return; }

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
                    return;
                }
                game = other;
                if (!other.Scanned) DllDetector.Inspect(other);
            }

            SetStatus(Loc.T("nexus.analyzing", Path.GetFileName(file)), false);
            var plan = await _svc.NexusMods.AnalyzeAsync(game, file, domain, pageTitle);

            if (plan.Blocked) { SetStatus(plan.Note!, true); return; }
            if (plan.Layout == ModLayout.Unknown) { _pendingGame = game; Pending = plan; SetStatus(plan.Note!, false); return; }

            await InstallAsync(game, plan);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("err.install_failed", ex.Message), true);
        }
        finally
        {
            Busy = false;
            Progress = 0;
        }
    }

    private GameInfo? _pendingGame;

    private async Task ChooseAsync(ModLayout layout)
    {
        if (Pending is not { } plan || _pendingGame is not { } game) return;
        plan.Layout = layout;
        plan.Target = layout == ModLayout.GameRoot ? game.InstallDir : DllInstaller.TargetDirectory(game);
        Pending = null;
        Busy = true;
        try { await InstallAsync(game, plan); }
        finally { Busy = false; }
    }

    private async Task InstallAsync(GameInfo game, ModPlan plan)
    {
        var detail = _detail();
        var sameGame = detail?.Game.Id == game.Id;

        // Addon, preset ou shader : ReShade d'abord, par le chemin habituel de Prism.
        if (plan.NeedsReShade && !HdrInstaller.ReShadeReady(game))
        {
            if (!sameGame) { SetStatus(Loc.T("nexus.err.reshade", game.Name), true); return; }
            SetStatus(Loc.T("nexus.reshade_first"), false);
            if (!await detail!.EnsureReShadeAsync()) { SetStatus(Loc.T("nexus.err.reshade", game.Name), true); return; }
        }

        var result = _svc.NexusMods.Install(game, plan);
        SetStatus(result.Message, !result.Success);
        _notify(result.Message, !result.Success);
        if (!result.Success) return;

        DllDetector.Inspect(game);
        if (sameGame) detail!.Refresh();

        // Ce que le nouveau mod a pu mettre en conflit avec l'existant.
        var conflicts = ConflictService.Evaluate(game);
        if (conflicts.Count > 0)
            SetStatus(result.Message + " · " + Loc.T("nexus.conflicts", string.Join(" · ", conflicts.Select(c => c.Title))), true);
    }

    private void SetStatus(string message, bool error)
    {
        Status = message;
        StatusIsError = error;
    }

    public void Relocalize()
    {
        OnPropertyChanged(nameof(PendingTitle));
    }
}
