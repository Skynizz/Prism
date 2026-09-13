using System.Collections.ObjectModel;
using Prism.Core;
using Prism.Models;
using Prism.Services;

namespace Prism.ViewModels;

/// <summary>
/// Une famille de runtime pour un titre : ce qui est en place, ce qui est
/// disponible, et les actions possibles.
///
/// Deux cas distincts, et l'interface les nomme differemment :
///  - le titre embarque la bibliotheque : on la met a jour (<i>Apply</i>), l'original
///    part en sauvegarde ;
///  - le titre ne l'embarque pas : on l'ajoute (<i>Deploy</i>), et elle pourra etre
///    retiree proprement.
/// </summary>
public sealed class DllSlotViewModel : ObservableObject
{
    private readonly GameInfo _game;
    private readonly ManifestService _manifest;
    private readonly DllInstaller _installer;
    private readonly BackupService _backups;
    private readonly DeploymentStore _deployments;
    private readonly Action<string, bool> _notify;

    public DllSlotViewModel(
        DllKind kind,
        GameInfo game,
        ManifestService manifest,
        DllInstaller installer,
        BackupService backups,
        DeploymentStore deployments,
        Action<string, bool> notify)
    {
        Kind = kind;
        _game = game;
        _manifest = manifest;
        _installer = installer;
        _backups = backups;
        _deployments = deployments;
        _notify = notify;

        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => Selected is not null && !Busy);
        RestoreCommand = new RelayCommand(_ => Restore(), _ => HasBackup);
        RemoveCommand = new RelayCommand(_ => Remove(), _ => IsDeployed);

        Reload();
    }

    public DllKind Kind { get; }
    public string Label => DllInstaller.LabelFor(Kind);
    public string FileName => DllInstaller.FileNameFor(Kind);

    /// <summary>Sigle compact pour les tableaux et l'inspecteur.</summary>
    public string ShortLabel => Kind switch
    {
        DllKind.Dlss => "DLSS SR",
        DllKind.DlssG => "DLSS-G",
        DllKind.DlssD => "DLSS-NR",
        DllKind.FsrDx12 => "FSR DX12",
        DllKind.FsrVk => "FSR VK",
        DllKind.XeSS => "XESS",
        DllKind.XeSSFg => "XESS-FG",
        DllKind.XeLL => "XELL",
        _ => Kind.ToString().ToUpperInvariant()
    };

    /// <summary>Phrase d'infobulle : courte et technique.</summary>
    public string Tip => Kind switch
    {
        DllKind.Dlss => Loc.T("slot.tip.dlss"),
        DllKind.DlssG => Loc.T("slot.tip.dlssg"),
        DllKind.DlssD => Loc.T("slot.tip.dlssd"),
        _ => Loc.T("slot.tip.other")
    };

    public ObservableCollection<DllRecord> Versions { get; } = new();

    private DllRecord? _selected;
    public DllRecord? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) ApplyCommand.Raise(); }
    }

    private string _installedVersion = "—";
    public string InstalledVersion { get => _installedVersion; private set => Set(ref _installedVersion, value); }

    private bool _isPresent;
    public bool IsPresent
    {
        get => _isPresent;
        private set
        {
            Set(ref _isPresent, value);
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(ActionTip));
            OnPropertyChanged(nameof(State));
        }
    }

    private int _copies;
    public int Copies { get => _copies; private set { Set(ref _copies, value); OnPropertyChanged(nameof(StatusText)); } }

    private bool _hasBackup;
    public bool HasBackup { get => _hasBackup; private set { Set(ref _hasBackup, value); RestoreCommand.Raise(); } }

    private bool _isDeployed;
    /// <summary>Vrai si c'est Prism qui a ajoute ce fichier dans un titre qui ne l'avait pas.</summary>
    public bool IsDeployed
    {
        get => _isDeployed;
        private set
        {
            Set(ref _isDeployed, value);
            RemoveCommand.Raise();
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private bool _busy;
    public bool Busy { get => _busy; private set { Set(ref _busy, value); ApplyCommand.Raise(); } }

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    /// <summary>Vrai quand la version en place est deja la plus recente recommandee.</summary>
    public bool IsUpToDate
    {
        get
        {
            var best = _manifest.Recommended(Kind);
            return best is not null && InstalledVersion == best.Version;
        }
    }

    /// <summary>Le libelle du bouton principal change de sens selon le cas.</summary>
    public string ActionLabel => Loc.T(IsPresent ? "slot.apply" : "slot.deploy");

    public string ActionTip => IsPresent
        ? Loc.T("slot.tip.apply")
        : Loc.T("slot.tip.deploy", FileName);

    public UiStatus State =>
        !IsPresent ? UiStatus.Idle
        : IsDeployed ? UiStatus.Injected
        : IsUpToDate ? UiStatus.Ready
        : UiStatus.Detected;

    public string StatusText => !IsPresent
        ? Loc.T("common.absent")
        : IsDeployed ? Loc.T("slot.status.added", InstalledVersion)
        : Copies > 1 ? Loc.T("slot.status.copies", InstalledVersion, Copies)
        : InstalledVersion;

    public AsyncRelayCommand ApplyCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public void Reload()
    {
        var installed = _game.Dlls.Where(d => d.Kind == Kind).ToList();
        IsPresent = installed.Count > 0;
        Copies = installed.Count;
        InstalledVersion = installed.FirstOrDefault()?.Display ?? "—";
        HasBackup = installed.Any(d => _backups.HasBackup(d.Path));
        IsDeployed = _deployments.Find(_game.Id, Kind) is not null;

        var keepSelection = Selected?.Version;
        Versions.Clear();
        foreach (var r in _manifest.For(Kind).Where(r => r.IsSignatureValid))
            Versions.Add(r);

        Selected = (keepSelection is not null ? Versions.FirstOrDefault(v => v.Version == keepSelection) : null)
                   ?? _manifest.Recommended(Kind)
                   ?? Versions.FirstOrDefault();

        OnPropertyChanged(nameof(IsUpToDate));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(ActionTip));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Tip));
    }

    private async Task ApplyAsync()
    {
        if (Selected is null) return;

        var adding = !IsPresent;
        Busy = true;
        Progress = 0;
        _notify(Loc.T(adding ? "slot.progress.add" : "slot.progress.update", Label, Selected.Version), false);

        try
        {
            var progress = new Progress<double>(p => Progress = p);
            var result = await _installer.InstallAsync(_game, Selected, progress, deployIfMissing: adding);
            _notify(result.Message, !result.Success);
        }
        catch (Exception ex)
        {
            _notify(Loc.T("err.generic", ex.Message), true);
        }
        finally
        {
            Busy = false;
            Progress = 0;
            Reload();
        }
    }

    private void Restore()
    {
        var entries = _backups.For(_game.Id)
            .Where(e => string.Equals(Path.GetFileName(e.OriginalPath), FileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var n = entries.Count(_backups.Restore);
        DllDetector.Inspect(_game);
        Reload();

        _notify(n > 0
            ? Loc.T("slot.restored", Label, n)
            : Loc.T("slot.no_backup", Label), n == 0);
    }

    private void Remove()
    {
        var result = _installer.Undeploy(_game, Kind);
        _notify(result.Message, !result.Success);
        Reload();
    }
}
