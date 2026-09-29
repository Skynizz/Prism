using System.Collections.ObjectModel;
using Prism.Core;
using Prism.Services;

namespace Prism.ViewModels;

/// <summary>Un reglage du pilote pour le jeu cible : valeur lue dans le profil, ecrite des qu'elle change.</summary>
public sealed class DriverSettingRow : ObservableObject
{
    private readonly Func<string?> _exe;
    private readonly Action<string, bool> _notify;
    private bool _loading;

    public DriverSettingRow(DriverSettingDef def, Func<string?> exe, Action<string, bool> notify)
    {
        Def = def;
        _exe = exe;
        _notify = notify;
    }

    public DriverSettingDef Def { get; }
    public string Label => Loc.T(Def.LabelKey);
    public string? Hint => Def.HintKey is null ? null : Loc.T(Def.HintKey);
    public ObservableCollection<DriverChoice> Choices { get; } = new();

    private DriverChoice? _selected;
    public DriverChoice? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value) || value is null || _loading) return;
            Apply(value);
        }
    }

    public void Load()
    {
        _loading = true;
        try
        {
            Choices.Clear();
            foreach (var c in Def.Choices()) Choices.Add(c);
            var exe = _exe();
            var current = exe is null ? null : Def.Read(exe);
            var match = Choices.FirstOrDefault(c => c.Value == current);
            if (match is null && current is not null)
            {
                // Valeur posee ailleurs (application NVIDIA, Profile Inspector) : montree telle quelle.
                match = new DriverChoice(current, Loc.T("drv.custom", current.Value));
                Choices.Add(match);
            }
            Selected = match ?? Choices.FirstOrDefault();
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Hint));
    }

    private void Apply(DriverChoice choice)
    {
        var exe = _exe();
        if (exe is null) return;
        var ok = Def.Write(exe, choice.Value);
        if (ok)
        {
            Log.Info("driver", $"{exe}: {Def.Id} = {(choice.Value is { } v ? $"0x{v:X}" : "default")}");
            _notify(Loc.T("drv.saved", Label, choice.Label, exe), false);
        }
        else
        {
            _notify(Def.NeedsAdmin ? Loc.T("drv.err_admin", Label) : Loc.T("preset.err"), true);
            Load();
        }
    }
}

/// <summary>Une sous-categorie de la page : DLSS, Latence, Generation d'images, Energie et affichage.</summary>
public sealed class DriverGroup
{
    public required string Key { get; init; }
    public string Label => Key switch
    {
        "dlss" => Loc.T("drv.group.dlss"),
        "latency" => Loc.T("drv.group.latency"),
        "fg" => Loc.T("drv.group.fg"),
        _ => Loc.T("drv.group.power")
    };
    public ObservableCollection<DriverSettingRow> Rows { get; } = new();
}

/// <summary>Page « Pilote NVIDIA » : reglages du profil du pilote pour le jeu cible, sans application NVIDIA.</summary>
public sealed class DriverSettingsViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly Func<GameDetailViewModel?> _detail;
    private readonly Action<string, bool> _notify;

    public DriverSettingsViewModel(AppServices svc, Func<GameDetailViewModel?> detail, Action<string, bool> notify)
    {
        _svc = svc;
        _detail = detail;
        _notify = notify;
        foreach (var def in DriverProfileSettings.All(svc.Gpu, svc.Display))
        {
            var group = Groups.FirstOrDefault(g => g.Key == def.Group);
            if (group is null) Groups.Add(group = new DriverGroup { Key = def.Group });
            group.Rows.Add(new DriverSettingRow(def, () => ExeName, notify));
        }
        ResetCommand = new RelayCommand(_ => Reset(), _ => Available && ExeName is not null);
        RelaunchAdminCommand = new RelayCommand(_ => RelaunchAsAdmin());
    }

    /// <summary>Prism tourne en administrateur : Low Latency et Smooth Motion s'ecrivent.</summary>
    public bool IsAdmin { get; } = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public bool ShowAdminNote => Available && !IsAdmin && Groups.Any(g => g.Rows.Any(r => r.Def.NeedsAdmin));

    public RelayCommand RelaunchAdminCommand { get; }

    /// <summary>Relance Prism avec l'invite UAC ; l'instance actuelle se ferme si l'utilisateur accepte.</summary>
    private void RelaunchAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            System.Windows.Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Invite UAC refusee : Prism continue sans droits administrateur.
        }
    }

    public ObservableCollection<DriverGroup> Groups { get; } = new();

    /// <summary>Carte NVIDIA et nvapi64.dll presents : sinon la page l'explique.</summary>
    public bool Available => _svc.Gpu.IsNvidia && NvDriverSettings.Available;

    private string? ExeName => _detail()?.Game.Executable is { } e ? Path.GetFileName(e) : null;

    public string ProfileLabel => ExeName is { } exe ? Loc.T("drv.profile", exe) : "";

    public RelayCommand ResetCommand { get; }

    public void Reload()
    {
        if (!Available) return;
        foreach (var g in Groups)
            foreach (var r in g.Rows) r.Load();
        OnPropertyChanged(nameof(ProfileLabel));
        ResetCommand.Raise();
    }

    private void Reset()
    {
        if (ExeName is not { } exe) return;
        var ok = DriverProfileSettings.ClearAll(exe);
        _notify(ok ? Loc.T("drv.reset_done", exe) : Loc.T("preset.err"), !ok);
        Reload();
    }

    public void Relocalize()
    {
        OnPropertyChanged(string.Empty);
        Reload();
    }
}
