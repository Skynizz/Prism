using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Prism.Core;
using Prism.Models;
using Prism.Services;

namespace Prism.ViewModels;

/// <summary>Un titre modifie, avec le nombre d'ecritures a annuler.</summary>
public sealed record ChangedGame(string Id, string Name, int Count)
{
    public string CountLabel => Loc.T("changes.count", Count);
}

public sealed class NavItem : ObservableObject
{
    /// <summary>Cle de traduction du libelle.</summary>
    public required string Key { get; init; }
    public string Title => Loc.T(Key);
    public void Relocalize() => OnPropertyChanged(nameof(Title));
    /// <summary>Cle de la geometrie d'icone dans Tokens.xaml.</summary>
    public required string Icon { get; init; }
    public required int Index { get; init; }
    /// <summary>Regroupement affiche dans le rail.</summary>
    public string Group { get; init; } = "";
}

/// <summary>Modele de la coquille : navigation, etat systeme, bibliotheque, journal.</summary>
public sealed class MainViewModel : ObservableObject
{
    private const string Src = "ui";

    private readonly AppServices _svc;
    private readonly DispatcherTimer _processTimer;

    public MainViewModel()
    {
        _svc = new AppServices();

        Nav = new ObservableCollection<NavItem>
        {
            new() { Key = "nav.overview",     Icon = "IcoOverview",   Index = 0, Group = "SYSTEM" },
            new() { Key = "nav.games",        Icon = "IcoGames",      Index = 1, Group = "SYSTEM" },
            new() { Key = "nav.dlss",         Icon = "IcoDlss",       Index = 2, Group = "PIPELINE" },
            new() { Key = "nav.framegen",    Icon = "IcoFrameGen",   Index = 3, Group = "PIPELINE" },
            new() { Key = "nav.injection",    Icon = "IcoInjection",  Index = 4, Group = "PIPELINE" },
            new() { Key = "nav.changes",      Icon = "IcoChanges",    Index = 5, Group = "TOOLING" },
            new() { Key = "nav.components",   Icon = "IcoComponents", Index = 6, Group = "TOOLING" },
            new() { Key = "nav.logs",         Icon = "IcoLogs",       Index = 7, Group = "TOOLING" },
            new() { Key = "nav.settings",     Icon = "IcoSettings",   Index = 8, Group = "TOOLING" }
        };
        SelectedNav = Nav[0];

        // Le rail est groupe par famille : SYSTEM, PIPELINE, TOOLING.
        NavView = CollectionViewSource.GetDefaultView(Nav);
        NavView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavItem.Group)));

        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = FilterGame;

        LogView = CollectionViewSource.GetDefaultView(Log.Buffer);
        LogView.Filter = FilterLog;

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !Busy);
        RefreshCatalogsCommand = new AsyncRelayCommand(RefreshCatalogsAsync, () => !Busy);
        AddFolderCommand = new RelayCommand(_ => AddFolder());
        RemoveFolderCommand = new RelayCommand(p => RemoveFolder(p as string));
        OpenUrlCommand = new RelayCommand(p => OpenUrl(p as string));
        OpenPathCommand = new RelayCommand(p => OpenUrl(p as string));
        GoToCommand = new RelayCommand(p => GoTo(p));
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        ExportLogCommand = new RelayCommand(_ => ExportLog());
        SetLevelCommand = new RelayCommand(p => SetDetailLevel(p));
        RestoreBackupCommand = new RelayCommand(p => RestoreBackup(p as BackupEntry));
        ForgetBackupCommand = new RelayCommand(p => ForgetBackup(p as BackupEntry));
        RevertChangeCommand = new RelayCommand(p => RevertChange(p as ChangeEntry));
        RevertGameCommand = new RelayCommand(p => RevertGame(p as string));
        RevertAllCommand = new RelayCommand(_ => RevertAll(), _ => HasChanges);

        // Le journal est alimente depuis des threads de fond : on marshalle vers l'UI.
        // L'historique anterieur a l'abonnement est rejoue pour que la console montre
        // aussi les lignes de demarrage.
        foreach (var entry in Log.Snapshot()) Log.Push(entry);
        Log.Appended += OnLogAppended;
        Loc.I.LanguageChanged += OnLanguageChanged;

        _processTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _processTimer.Tick += (_, _) => RefreshRunningGame();

        foreach (var f in _svc.Settings.Current.ExtraLibraryFolders) ExtraFolders.Add(f);
        ReloadBackups();
        ReloadChanges();
    }

    // ---------------------------------------------------------- Navigation

    public ObservableCollection<NavItem> Nav { get; }
    public ICollectionView NavView { get; }

    private NavItem? _selectedNav;
    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (!Set(ref _selectedNav, value)) return;
            Log.Trace("nav", $"page -> {value?.Title ?? "(null)"} [{value?.Index}]");
            OnPropertyChanged(nameof(PageIndex));
        }
    }

    public int PageIndex => SelectedNav?.Index ?? 0;

    private void GoTo(object? p)
    {
        if (p is null) return;
        var idx = p is int i ? i : int.TryParse(p.ToString(), out var v) ? v : -1;
        var item = Nav.FirstOrDefault(n => n.Index == idx);
        if (item is not null) SelectedNav = item;
    }

    // --------------------------------------------------------------- Langue

    public IReadOnlyList<Language> Languages => Loc.Languages;

    public Language SelectedLanguage
    {
        get => Loc.I.Current;
        set
        {
            if (value is null || value.Code == Loc.I.Code) return;
            _svc.Settings.Current.Language = value.Code;
            _svc.Settings.Save();
            Loc.I.Set(value.Code);
        }
    }

    /// <summary>Tout ce qui est calcule en C# se recalcule dans la nouvelle langue.</summary>
    private void OnLanguageChanged()
    {
        foreach (var item in Nav) item.Relocalize();
        foreach (var component in Components) component.Relocalize();
        Detail?.Relocalize();
        ReloadChanges();
        ReloadBackups();
        RaiseCatalogProps();
        OnPropertyChanged(string.Empty);
    }

    // ------------------------------------------------------ Niveau de detail

    private DetailLevel _level = DetailLevel.Advanced;
    public DetailLevel Level
    {
        get => _level;
        set
        {
            if (!Set(ref _level, value)) return;
            OnPropertyChanged(nameof(IsAdvanced));
            OnPropertyChanged(nameof(IsExpert));
        }
    }

    public bool IsAdvanced => Level is DetailLevel.Advanced or DetailLevel.Expert;
    public bool IsExpert => Level is DetailLevel.Expert;

    private void SetDetailLevel(object? p)
    {
        if (p is DetailLevel d) Level = d;
        else if (p is string s && Enum.TryParse<DetailLevel>(s, true, out var parsed)) Level = parsed;
    }

    // ------------------------------------------------------------- Systeme

    public GpuInfo Gpu => _svc.Gpu;
    public DisplayInfo Display => _svc.Display;

    public string GpuName => Gpu.Name.Replace("NVIDIA ", "");
    public string DriverLabel => Gpu.DriverBranch ?? Gpu.DriverVersion;
    public string VramLabel => SystemInfo.FormatBytes(Gpu.VramBytes);
    public string ApiLabel => Gpu.DirectXLevel;

    /// <summary>Ce que la carte sait faire, formule sans ambiguite.</summary>
    public string CapabilitySummary => Gpu.Generation switch
    {
        GpuGeneration.Blackwell or GpuGeneration.NewerNvidia => Loc.T("cap.blackwell"),
        GpuGeneration.AdaLovelace => Loc.T("cap.ada"),
        GpuGeneration.Ampere or GpuGeneration.Turing => Loc.T("cap.older"),
        _ => Loc.T("cap.unknown")
    };

    public UiStatus SrStatus => Gpu.SupportsDlss ? UiStatus.Ready : UiStatus.Disabled;
    public UiStatus FgStatus => Gpu.SupportsNativeFg ? UiStatus.Ready : UiStatus.Disabled;
    public UiStatus MfgStatus => Gpu.SupportsNativeMfg ? UiStatus.Ready
        : Gpu.Generation is GpuGeneration.AdaLovelace or GpuGeneration.Ampere ? UiStatus.Warning
        : UiStatus.Disabled;

    public string MfgDetail => Gpu.SupportsNativeMfg ? Loc.T("cap.mfg.native")
        : Gpu.Generation == GpuGeneration.AdaLovelace ? Loc.T("cap.mfg.mod")
        : Gpu.Generation is GpuGeneration.Ampere or GpuGeneration.Turing ? "Via FSR 3.1"
        : Loc.T("common.unavailable");

    public string DlssRuntimeLabel => _svc.Manifest.Recommended(DllKind.Dlss)?.Version ?? "—";
    public string DlssGRuntimeLabel => _svc.Manifest.Recommended(DllKind.DlssG)?.Version ?? "—";
    public string DlssDRuntimeLabel => _svc.Manifest.Recommended(DllKind.DlssD)?.Version ?? "—";

    // --------------------------------------------------------- Bibliotheque

    public ObservableCollection<GameInfo> Games { get; } = new();
    public ICollectionView GamesView { get; }

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) GamesView.Refresh(); }
    }

    private bool _onlyModdable;
    public bool OnlyModdable
    {
        get => _onlyModdable;
        set { if (Set(ref _onlyModdable, value)) GamesView.Refresh(); }
    }

    private GameInfo? _selectedGame;
    public GameInfo? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!Set(ref _selectedGame, value)) return;

            if (value is null) { Detail = null; return; }

            // L'inspection disque n'a lieu qu'a la selection : scanner toute la
            // bibliotheque d'un coup ferait attendre pour rien.
            if (!value.Scanned) DllDetector.Inspect(value);
            Detail = new GameDetailViewModel(value, _svc, Notify);
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => Detail is not null;

    // ------------------------------------------------------------ Adaptatif

    private double _viewport = 1480;

    /// <summary>
    /// Largeur utile de la zone de contenu, publiee par la page. Sous 1250 px
    /// l'inspecteur cede la place a la table ; sous 1150 px les informations
    /// secondaires des lignes disparaissent.
    /// </summary>
    public void SetViewport(double width)
    {
        if (Math.Abs(_viewport - width) < 1) return;
        _viewport = width;
        OnPropertyChanged(nameof(Compact));
        OnPropertyChanged(nameof(InspectorVisible));
    }

    public bool Compact => _viewport < 1150;
    public bool InspectorVisible => _viewport >= 1250 && HasSelection;

    private GameDetailViewModel? _detail;
    public GameDetailViewModel? Detail
    {
        get => _detail;
        private set
        {
            Set(ref _detail, value);
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(InspectorVisible));
        }
    }

    public string GameCountLabel => Games.Count == 0
        ? Loc.T("games.count.none")
        : Loc.T("games.count", GamesView.Cast<object>().Count(), Games.Count);

    private bool FilterGame(object obj)
    {
        if (obj is not GameInfo g) return false;
        if (OnlyModdable && g.Scanned && !g.HasDlss && !g.HasDlssG && !g.HasStreamline) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;
        return g.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase);
    }

    // -------------------------------------------------------- Jeu en cours

    private GameInfo? _runningGame;
    public GameInfo? RunningGame
    {
        get => _runningGame;
        private set
        {
            if (!Set(ref _runningGame, value)) return;
            _svc.RunningGameId = value?.Id;
            OnPropertyChanged(nameof(HasRunningGame));
            OnPropertyChanged(nameof(RunningLabel));
            OnPropertyChanged(nameof(RunningStatus));
        }
    }

    public bool HasRunningGame => RunningGame is not null;
    public string RunningLabel => RunningGame?.Name ?? Loc.T("running.none");
    public UiStatus RunningStatus => RunningGame is not null ? UiStatus.Active : UiStatus.Idle;

    private void RefreshRunningGame()
    {
        var found = ProcessWatcher.FindRunning(Games);
        if (found?.Id == RunningGame?.Id) return;

        RunningGame = found;
        if (found is not null) Log.Info(Src, $"Jeu actif : {found.Name}");
        Detail?.Refresh();
    }

    // ---------------------------------------------------------- Composants

    public IReadOnlyList<ModComponent> Components => _svc.Catalog.Components;

    public string CatalogSummary => _svc.Manifest.Loaded
        ? Loc.T("catalog.fetched", string.Format(Loc.I.Culture, "{0:g}", _svc.Manifest.FetchedAt))
        : Loc.T("catalog.not_loaded");

    public string RenoDxSummary => _svc.RenoDx.Addons.Count > 0
        ? Loc.T("renodx.summary", _svc.RenoDx.Addons.Count, string.Format(Loc.I.Culture, "{0:d}", _svc.RenoDx.PublishedAt))
        : Loc.T("renodx.not_loaded");

    // --------------------------------------------------------- Sauvegardes

    public ObservableCollection<ChangeEntry> Changes { get; } = new();

    /// <summary>Titres reellement touches, pour l'annulation par titre.</summary>
    public ObservableCollection<ChangedGame> ChangedGames { get; } = new();

    /// <summary>Recharge le registre unifie des modifications.</summary>
    public void ReloadChanges()
    {
        Changes.Clear();
        foreach (var c in _svc.Changes.All()) Changes.Add(c);

        ChangedGames.Clear();
        foreach (var g in Changes.GroupBy(c => c.GameId))
            ChangedGames.Add(new ChangedGame(g.Key, g.First().GameName, g.Count()));

        OnPropertyChanged(nameof(ChangesSummary));
        OnPropertyChanged(nameof(HasChanges));
    }

    public bool HasChanges => Changes.Count > 0;

    public string ChangesSummary
    {
        get
        {
            if (Changes.Count == 0) return Loc.T("changes.empty");
            var games = Changes.Select(c => c.GameId).Distinct().Count();
            return Loc.T("changes.summary", Changes.Count, games);
        }
    }

    public ObservableCollection<BackupEntry> Backups { get; } = new();

    public void ReloadBackups()
    {
        Backups.Clear();
        foreach (var e in _svc.Backups.All.OrderByDescending(e => e.CreatedAt)) Backups.Add(e);
        OnPropertyChanged(nameof(BackupsSummary));
    }

    public string BackupsSummary => Backups.Count == 0
        ? Loc.T("backups.none")
        : Loc.T("backups.count", Backups.Count);

    // -------------------------------------------------------------- Journal

    public ICollectionView LogView { get; }

    private string _logSearch = "";
    public string LogSearch
    {
        get => _logSearch;
        set { if (Set(ref _logSearch, value)) LogView.Refresh(); }
    }

    private LogLevel _minLevel = LogLevel.Trace;
    public LogLevel MinLevel
    {
        get => _minLevel;
        set { if (Set(ref _minLevel, value)) LogView.Refresh(); }
    }

    public IReadOnlyList<LogLevel> LogLevels { get; } =
        new[] { LogLevel.Trace, LogLevel.Info, LogLevel.Warn, LogLevel.Error };

    private bool FilterLog(object obj)
    {
        if (obj is not LogEntry e) return false;
        if (e.Level < MinLevel) return false;
        if (string.IsNullOrWhiteSpace(LogSearch)) return true;
        return e.Message.Contains(LogSearch, StringComparison.CurrentCultureIgnoreCase)
               || e.Source.Contains(LogSearch, StringComparison.CurrentCultureIgnoreCase);
    }

    private void OnLogAppended(LogEntry entry)
    {
        var app = Application.Current;
        if (app is null) return;

        if (app.Dispatcher.CheckAccess()) PushLog(entry);
        else app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => PushLog(entry));
    }

    private void PushLog(LogEntry entry)
    {
        Log.Push(entry);
        LastLog = entry;
        if (entry.Level >= LogLevel.Warn) Status = entry.Message;
    }

    private LogEntry? _lastLog;
    public LogEntry? LastLog { get => _lastLog; private set => Set(ref _lastLog, value); }

    private void ClearLog()
    {
        Log.Buffer.Clear();
        Log.Info(Src, "Journal efface.");
    }

    private void ExportLog()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"prism-{DateTime.Now:yyyyMMdd-HHmm}.log",
                Filter = Loc.T("log.filter")
            };
            if (dlg.ShowDialog() != true) return;

            File.WriteAllLines(dlg.FileName, Log.Buffer.Select(e => e.ToLine()));
            Notify(Loc.T("log.exported", dlg.FileName), false);
        }
        catch (Exception ex) { Notify(Loc.T("err.export_failed", ex.Message), true); }
    }

    // --------------------------------------------------------------- Etat

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        private set
        {
            Set(ref _busy, value);
            ScanCommand.Raise();
            RefreshCatalogsCommand.Raise();
        }
    }

    private string _status = "Pret";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    public UiStatus ShellStatus => StatusIsError ? UiStatus.Error : Busy ? UiStatus.Warning : UiStatus.Ready;

    public ObservableCollection<string> ExtraFolders { get; } = new();
    public AppSettings Settings => _svc.Settings.Current;
    public string BackupsPath => AppPaths.Backups;
    public string CachePath => AppPaths.Cache;
    public string LogsPath => AppPaths.Logs;
    public string AppVersion => "1.0.0";

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand RefreshCatalogsCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveFolderCommand { get; }
    public RelayCommand OpenUrlCommand { get; }
    public RelayCommand OpenPathCommand { get; }
    public RelayCommand GoToCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand ExportLogCommand { get; }
    public RelayCommand SetLevelCommand { get; }
    public RelayCommand RestoreBackupCommand { get; }
    public RelayCommand ForgetBackupCommand { get; }
    public RelayCommand RevertChangeCommand { get; }
    public RelayCommand RevertGameCommand { get; }
    public RelayCommand RevertAllCommand { get; }
    private void Notify(string message, bool isError)
    {
        Status = message;
        StatusIsError = isError;
        OnPropertyChanged(nameof(ShellStatus));
        if (isError) Log.Warn(Src, message); else Log.Info(Src, message);
        ReloadBackups();
        ReloadChanges();
        RevertAllCommand.Raise();
    }

    public void SaveSettings() => _svc.Settings.Save();

    // ------------------------------------------------------------ Demarrage

    public async Task StartupAsync()
    {
        Busy = true;
        try
        {
            await _svc.InitializeAsync(new Progress<string>(s => Status = s));
            RaiseCatalogProps();
        }
        finally { Busy = false; }

        await ScanAsync();
        _processTimer.Start();
    }

    private void RaiseCatalogProps()
    {
        OnPropertyChanged(nameof(CatalogSummary));
        OnPropertyChanged(nameof(RenoDxSummary));
        OnPropertyChanged(nameof(DlssRuntimeLabel));
        OnPropertyChanged(nameof(DlssGRuntimeLabel));
        OnPropertyChanged(nameof(DlssDRuntimeLabel));
    }

    private async Task ScanAsync()
    {
        Busy = true;
        StatusIsError = false;
        try
        {
            _svc.Scanner.ExtraFolders = ExtraFolders.ToList();
            var found = await _svc.Scanner.ScanAsync(new Progress<string>(s => Status = s));

            var previous = SelectedGame?.Id;
            Games.Clear();
            foreach (var g in found) Games.Add(g);

            SelectedGame = previous is not null
                ? Games.FirstOrDefault(g => g.Id == previous) ?? Games.FirstOrDefault()
                : Games.FirstOrDefault();

            var platforms = found.Select(g => g.Platform).Distinct().Count();
            Status = Loc.T("scan.done", Games.Count, platforms);
            Log.Info(Src, Status);

            OnPropertyChanged(nameof(GameCountLabel));
            RefreshRunningGame();
        }
        catch (Exception ex) { Notify(Loc.T("err.scan_failed", ex.Message), true); }
        finally { Busy = false; }

        _ = InspectAllAsync();
    }

    /// <summary>
    /// Inspecte en tache de fond les titres non encore analyses, pour que la table
    /// affiche capacites et runtimes sur toutes les lignes et pas seulement sur la
    /// selection. Le disque est lu hors thread d'interface ; seule la notification
    /// repasse par le dispatcher.
    /// </summary>
    private async Task InspectAllAsync()
    {
        var pending = Games.Where(g => !g.Scanned).ToList();
        if (pending.Count == 0) return;

        var app = Application.Current;
        if (app is null) return;

        await Task.Run(() =>
        {
            foreach (var game in pending)
            {
                try
                {
                    DllDetector.Inspect(game, raise: false);
                    app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => game.RaiseAll());
                }
                catch (Exception ex) { Log.Warn(Src, $"Inspection de {game.Name} impossible : {ex.Message}"); }
            }
        });

        await app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            GamesView.Refresh();
            Detail?.Refresh();
            Log.Info(Src, $"{pending.Count} titre(s) analyses.");
        }).Task;
    }

    private async Task RefreshCatalogsAsync()
    {
        Busy = true;
        try
        {
            Status = Loc.T("catalog.updating");
            await _svc.Manifest.LoadAsync(true);
            await _svc.Catalog.RefreshAsync();
            // Le wiki RenoDX et rhi-repo changent plus vite que les runtimes : ils sont relus aussi.
            await _svc.Wiki.LoadAsync();
            await _svc.Rhi.LoadAsync();
            RaiseCatalogProps();
            Detail?.Refresh();
            Status = Loc.T("catalog.updated");
            Log.Info(Src, Status);
        }
        catch (Exception ex) { Notify(Loc.T("err.update_failed", ex.Message), true); }
        finally { Busy = false; }
    }

    // ---------------------------------------------------------- Reglages

    private void AddFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("dialog.games_folder") };
        if (dlg.ShowDialog() != true) return;
        if (ExtraFolders.Contains(dlg.FolderName)) return;

        ExtraFolders.Add(dlg.FolderName);
        _svc.Settings.Current.ExtraLibraryFolders = ExtraFolders.ToList();
        _svc.Settings.Save();
        Notify(Loc.T("library.folder_added", dlg.FolderName), false);
    }

    private void RemoveFolder(string? folder)
    {
        if (folder is null) return;
        ExtraFolders.Remove(folder);
        _svc.Settings.Current.ExtraLibraryFolders = ExtraFolders.ToList();
        _svc.Settings.Save();
    }

    private void RevertChange(ChangeEntry? entry)
    {
        if (entry is null) return;
        var result = _svc.Changes.Revert(entry);
        Notify(result.Message, !result.Success);
        Detail?.Refresh();
    }

    private void RevertGame(string? gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return;
        var result = _svc.Changes.RevertGame(gameId);
        Notify(result.Message, !result.Success);
        Detail?.Refresh();
    }

    private void RevertAll()
    {
        var games = Changes.Select(c => c.GameId).Distinct().ToList();
        var done = games.Sum(g => _svc.Changes.RevertGame(g).FilesChanged);
        Notify(done > 0
            ? Loc.T("changes.msg.reverted_all", done, games.Count)
            : Loc.T("changes.msg.none_reverted"), done == 0);
        Detail?.Refresh();
    }

    private void RestoreBackup(BackupEntry? entry)
    {
        if (entry is null) return;
        var ok = _svc.Backups.Restore(entry);
        Notify(ok ? Loc.T("changes.msg.restored", entry.FileName, entry.GameName) : Loc.T("err.restore_failed"), !ok);
        Detail?.Refresh();
    }

    private void ForgetBackup(BackupEntry? entry)
    {
        if (entry is null) return;
        _svc.Backups.Forget(entry);
        Notify(Loc.T("backups.forgotten", entry.FileName), false);
    }

    private void OpenUrl(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Notify(Loc.T("err.open_failed", ex.Message), true); }
    }
}
