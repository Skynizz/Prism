using System.Collections.ObjectModel;
using System.Windows.Threading;
using Prism.Core;
using Prism.Models;
using Prism.Services;

namespace Prism.ViewModels;

/// <summary>Une categorie du filtre : Tout, HDR, Base, Couleur...</summary>
public sealed class ShaderCategory : ObservableObject
{
    public required string Key { get; init; }
    public string Label => LabelOf(Key);

    /// <summary>Libelle d'une categorie ; cles ecrites en toutes lettres pour la verification des traductions.</summary>
    public static string LabelOf(string key) => key switch
    {
        "hdr" => Loc.T("shaders.cat.hdr"),
        "base" => Loc.T("shaders.cat.base"),
        "color" => Loc.T("shaders.cat.color"),
        "light" => Loc.T("shaders.cat.light"),
        "photo" => Loc.T("shaders.cat.photo"),
        "special" => Loc.T("shaders.cat.special"),
        _ => Loc.T("shaders.cat.all")
    };

    private bool _selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    public void Relocalize() => OnPropertyChanged(nameof(Label));
}

/// <summary>Un pack du catalogue, avec son etat dans le jeu cible.</summary>
public sealed class ShaderPackRow : ObservableObject
{
    public ShaderPackRow(ShaderPack pack, Func<ShaderPackRow, Task> install, Func<ShaderPackRow, Task> remove)
    {
        Pack = pack;
        InstallCommand = new AsyncRelayCommand(_ => install(this), _ => !Busy && !Installed);
        RemoveCommand = new AsyncRelayCommand(_ => remove(this), _ => !Busy && Installed);
    }

    public ShaderPack Pack { get; }
    public string Name => Pack.Name;
    public string Author => Pack.Author;
    public string Tags => Pack.Tags;
    public bool Essential => Pack.Essential;
    public string CategoryLabel => ShaderCategory.LabelOf(Pack.Category);

    private bool _installed;
    public bool Installed
    {
        get => _installed;
        set { if (Set(ref _installed, value)) { OnPropertyChanged(nameof(StateLabel)); RaiseCommands(); } }
    }

    private int _files;
    public int Files { get => _files; set { if (Set(ref _files, value)) OnPropertyChanged(nameof(StateLabel)); } }

    private bool _busy;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) RaiseCommands(); } }

    public string StateLabel => Installed ? Loc.T("shaders.state.installed", Files) : "";

    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }

    private void RaiseCommands()
    {
        InstallCommand.Raise();
        RemoveCommand.Raise();
    }

    public void Relocalize()
    {
        OnPropertyChanged(nameof(CategoryLabel));
        OnPropertyChanged(nameof(StateLabel));
    }
}

/// <summary>
/// Page ReShade : ReShade du jeu et bibliotheque de packs de shaders (Lilium pour le HDR, etc.),
/// ranges par categorie, cherchables, poses et retires pack par pack.
/// </summary>
public sealed class ShaderPacksViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly Func<GameDetailViewModel?> _detail;
    private readonly Action<string, bool> _notify;

    public ShaderPacksViewModel(AppServices svc, Func<GameDetailViewModel?> detail, Action<string, bool> notify)
    {
        _svc = svc;
        _detail = detail;
        _notify = notify;

        foreach (var key in new[] { "all", "hdr", "base", "color", "light", "photo", "special" })
            Categories.Add(new ShaderCategory { Key = key, IsSelected = key == "all" });
        PickCategoryCommand = new RelayCommand(p => Pick(p as ShaderCategory));
        InstallEssentialsCommand = new AsyncRelayCommand(_ => InstallEssentialsAsync(), _ => !Busy && Rows.Any(r => r.Essential && !r.Installed));
        Reload();
    }

    public ObservableCollection<ShaderCategory> Categories { get; } = new();
    public ObservableCollection<ShaderPackRow> Rows { get; } = new();
    public ObservableCollection<ShaderPackRow> Visible { get; } = new();

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) Filter(); } }

    private bool _busy;
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) InstallEssentialsCommand.Raise(); } }

    private string? _activity;
    /// <summary>Ce qui se passe en ce moment (« Lilium HDR · telechargement »).</summary>
    public string? Activity { get => _activity; private set => Set(ref _activity, value); }

    public int InstalledCount => Rows.Count(r => r.Installed);
    public string Header => Loc.T("shaders.header", InstalledCount, Rows.Count);
    public bool HasGame => _detail() is not null;

    public RelayCommand PickCategoryCommand { get; }
    public AsyncRelayCommand InstallEssentialsCommand { get; }

    /// <summary>Etat de chaque pack dans le jeu cible ; appele au changement de jeu.</summary>
    public void Reload()
    {
        var game = _detail()?.Game;
        if (Rows.Count == 0)
            foreach (var p in _svc.Shaders.Catalog)
                Rows.Add(new ShaderPackRow(p, InstallAsync, RemoveAsync));
        foreach (var r in Rows)
        {
            r.Installed = game is not null && _svc.Shaders.IsInstalled(game, r.Pack);
            r.Files = game is null ? 0 : _svc.Shaders.FileCount(game, r.Pack);
        }
        Filter();
        RaiseCounts();
    }

    private void Pick(ShaderCategory? category)
    {
        if (category is null) return;
        foreach (var c in Categories) c.IsSelected = c == category;
        Filter();
    }

    private void Filter()
    {
        var cat = Categories.FirstOrDefault(c => c.IsSelected)?.Key ?? "all";
        var q = Search.Trim();
        Visible.Clear();
        foreach (var r in Rows.Where(r => (cat == "all" || r.Pack.Category == cat)
                                          && (q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                              || r.Tags.Contains(q, StringComparison.OrdinalIgnoreCase)
                                              || r.Author.Contains(q, StringComparison.OrdinalIgnoreCase)))
                     .OrderByDescending(r => r.Essential).ThenByDescending(r => r.Installed))
            Visible.Add(r);
    }

    private async Task InstallAsync(ShaderPackRow row)
    {
        // Un pack a la fois : deux installations ecriraient ReShade.ini en meme temps.
        if (Busy) return;
        var detail = _detail();
        if (detail is null) { _notify(Loc.T("nexus.err.no_game"), true); return; }

        // Dependances declarees par le catalogue (Azen utilise les en-tetes de smolbbsoop).
        var chain = (row.Pack.Requires ?? new List<string>())
            .Select(id => Rows.FirstOrDefault(r => r.Pack.Id == id)).Where(r => r is { Installed: false }).Cast<ShaderPackRow>()
            .Append(row).ToList();

        Busy = true;
        try
        {
            foreach (var r in chain)
            {
                r.Busy = true;
                Activity = Loc.T("shaders.activity", r.Name);
                await Ui.Yield();
                var result = await Task.Run(() => _svc.Shaders.InstallAsync(detail.Game, r.Pack));
                r.Busy = false;
                _notify(result.Message, !result.Success);
                if (!result.Success) break;
            }
        }
        finally
        {
            Busy = false;
            Activity = null;
            Reload();
        }
    }

    private async Task RemoveAsync(ShaderPackRow row)
    {
        var detail = _detail();
        if (detail is null || Busy) return;
        Busy = true;
        row.Busy = true;
        try
        {
            Activity = Loc.T("shaders.activity_remove", row.Name);
            await Ui.Yield();
            var result = await Task.Run(() => _svc.Shaders.Remove(detail.Game, row.Pack));
            _notify(result.Message, !result.Success);
        }
        finally
        {
            row.Busy = false;
            Busy = false;
            Activity = null;
            Reload();
        }
    }

    /// <summary>Les packs marques essentiels (Lilium pour le HDR) en un clic.</summary>
    private async Task InstallEssentialsAsync()
    {
        foreach (var r in Rows.Where(r => r.Essential && !r.Installed).ToList())
            await InstallAsync(r);
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(HasGame));
        InstallEssentialsCommand.Raise();
    }

    public void Relocalize()
    {
        foreach (var c in Categories) c.Relocalize();
        foreach (var r in Rows) r.Relocalize();
        RaiseCounts();
    }
}
