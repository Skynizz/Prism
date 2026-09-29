using System.Collections.ObjectModel;
using System.Diagnostics;
using Prism.Core;
using Prism.Models;
using Prism.Services;

namespace Prism.ViewModels;

/// <summary>Une base du jeu, telle que le bilan la voit.</summary>
public sealed class FrameworkRow
{
    public required FrameworkState State { get; init; }
    public string Name => State.Name;
    public UiStatus Status => State.Installed ? UiStatus.Ready : State.Needed ? UiStatus.Error : State.Optional ? UiStatus.Warning : UiStatus.Idle;
    public string StateLabel => State.Installed ? Loc.T("health.state.installed")
        : State.Needed ? Loc.T("health.state.missing")
        : State.Optional ? Loc.T("health.state.optional")
        : Loc.T("health.state.not_needed");
}

/// <summary>Un probleme du bilan, avec son bouton de correction.</summary>
public sealed class FindingRow
{
    public FindingRow(HealthFinding finding, Func<HealthFinding, Task> fix)
    {
        Finding = finding;
        FixCommand = new AsyncRelayCommand(_ => fix(finding), _ => finding.Fix != HealthFixKind.None);
    }

    public HealthFinding Finding { get; }
    public string Title => Finding.Title;
    public string? Detail => Finding.Detail;
    public UiStatus Status => Finding.Severity;
    public bool CanFix => Finding.Fix != HealthFixKind.None;
    public string FixLabel => Finding.Fix switch
    {
        HealthFixKind.SteamInstall => Loc.T("health.fix.steam"),
        HealthFixKind.Reinstall => Loc.T("health.fix.reinstall"),
        HealthFixKind.OpenFile => Loc.T("health.fix.open_log"),
        _ => Loc.T("health.fix.download")
    };
    public AsyncRelayCommand FixCommand { get; }
}

/// <summary>
/// Page « Mods » : le bilan du jeu cible — bases exigees par les mods presents, fichiers manquants,
/// prerequis Nexus, conflits, erreurs des chargeurs au dernier lancement — et une correction par probleme.
/// Le bilan se refait au changement de jeu, apres chaque installation et a la fermeture du jeu.
/// </summary>
public sealed class ModsViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly Func<GameDetailViewModel?> _detail;
    private readonly NexusViewModel _nexus;
    private readonly Action _showNexus;
    private readonly Action<string, bool> _notify;
    private CancellationTokenSource? _cts;

    public ModsViewModel(AppServices svc, Func<GameDetailViewModel?> detail, NexusViewModel nexus, Action showNexus, Action<string, bool> notify)
    {
        _svc = svc;
        _detail = detail;
        _nexus = nexus;
        _showNexus = showNexus;
        _notify = notify;
        CheckCommand = new AsyncRelayCommand(_ => CheckAsync(), _ => !Checking);
        FixAllCommand = new AsyncRelayCommand(_ => FixAllAsync(), _ => !Checking && Findings.Any(f => f.CanFix && f.Finding.Fix != HealthFixKind.OpenFile));
        _nexus.QueueFinished += () => _ = CheckAsync();
    }

    public ObservableCollection<FrameworkRow> Frameworks { get; } = new();
    public ObservableCollection<FindingRow> Findings { get; } = new();

    private bool _checking;
    public bool Checking { get => _checking; private set { if (Set(ref _checking, value)) { CheckCommand.Raise(); FixAllCommand.Raise(); OnPropertyChanged(nameof(Summary)); } } }

    private bool _checked;
    public bool HasFrameworks => Frameworks.Count > 0;
    public bool HasFindings => Findings.Count > 0;
    public int Problems => Findings.Count(f => f.Status is UiStatus.Error or UiStatus.Warning);
    public UiStatus SummaryStatus => !_checked ? UiStatus.Idle : Findings.Any(f => f.Status == UiStatus.Error) ? UiStatus.Error : Problems > 0 ? UiStatus.Warning : UiStatus.Ready;
    public string Summary => Checking ? Loc.T("health.checking") : !_checked ? "" : Problems == 0 ? Loc.T("health.all_good") : Loc.T("health.problems", Problems);

    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand FixAllCommand { get; }

    public async Task CheckAsync()
    {
        var game = _detail()?.Game;
        _cts?.Cancel();
        if (game is null) { Clear(); return; }
        var cts = _cts = new CancellationTokenSource();
        Checking = true;
        try
        {
            var report = await Task.Run(() => _svc.ModHealth.CheckAsync(game, cts.Token));
            if (cts.IsCancellationRequested) return;
            Frameworks.Clear();
            foreach (var f in report.Frameworks) Frameworks.Add(new FrameworkRow { State = f });
            Findings.Clear();
            foreach (var f in report.Findings.OrderBy(f => f.Severity == UiStatus.Error ? 0 : 1)) Findings.Add(new FindingRow(f, FixAsync));
            _checked = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("health", $"{game.Name}: {ex.Message}"); }
        finally
        {
            if (_cts == cts) Checking = false;
            Raise();
        }
    }

    private void Clear()
    {
        Frameworks.Clear();
        Findings.Clear();
        _checked = false;
        Raise();
    }

    private async Task FixAsync(HealthFinding f)
    {
        switch (f.Fix)
        {
            case HealthFixKind.SteamInstall when f.SteamApp is { } app:
                // Protocole Steam : ouvre l'installation du DLC dans le client Steam.
                Process.Start(new ProcessStartInfo($"steam://install/{app}") { UseShellExecute = true });
                _notify(Loc.T("health.steam_opened", f.FixName ?? ""), false);
                break;
            case HealthFixKind.OpenFile when f.Path is not null:
                Process.Start(new ProcessStartInfo(f.Path) { UseShellExecute = true });
                break;
            case HealthFixKind.NexusDownload or HealthFixKind.Reinstall:
                await DownloadAsync(new[] { f });
                break;
        }
    }

    /// <summary>Tout ce qui se corrige : telechargements Nexus d'un coup (en fond en Premium), DLC Steam ouverts.</summary>
    private async Task FixAllAsync()
    {
        foreach (var f in Findings.Select(r => r.Finding).Where(f => f.Fix == HealthFixKind.SteamInstall).ToList()) await FixAsync(f);
        var downloads = Findings.Select(r => r.Finding).Where(f => f.Fix is HealthFixKind.NexusDownload or HealthFixKind.Reinstall).ToList();
        if (downloads.Count > 0) await DownloadAsync(downloads);
    }

    private async Task DownloadAsync(IReadOnlyList<HealthFinding> items)
    {
        var started = await _nexus.DownloadAsync(items);
        // Compte gratuit : un clic par fichier sur la page Nexus, qu'on ouvre.
        if (started && !_svc.NexusAccount.IsPremium) _showNexus();
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(HasFrameworks));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(SummaryStatus));
        OnPropertyChanged(nameof(Summary));
        FixAllCommand.Raise();
    }

    public void Relocalize() => _ = CheckAsync();
}
