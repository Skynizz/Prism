using Prism.Core;

namespace Prism.Models;

/// <summary>Un jeu detecte sur le disque.</summary>
public sealed class GameInfo : ObservableObject
{
    /// <summary>Identifiant stable : "steam:1091500". Sert de cle pour backups et profils.</summary>
    public required string Id { get; init; }

    private string _name = "";

    /// <summary>Nom affiche : celui de la plateforme, ou le nom officiel retrouve (<see cref="Services.GameIdentityService"/>).</summary>
    public required string Name { get => _name; set => Set(ref _name, value); }

    public required string InstallDir { get; init; }
    public GamePlatform Platform { get; init; }

    /// <summary>
    /// AppID Steam, y compris pour un jeu Epic, GOG ou ajoute a la main : c'est la cle des
    /// catalogues (mods RenoDX HDR), la meme que pour un jeu Steam.
    /// </summary>
    public long? SteamAppId { get; set; }

    /// <summary>D'ou viennent le nom et l'AppID.</summary>
    public IdentitySource IdentitySource { get; set; } = IdentitySource.Folder;

    /// <summary>Executable principal presume (le plus gros .exe hors launchers connus).</summary>
    public string? Executable { get; set; }

    /// <summary>
    /// Vrai une fois le dossier inspecte. Propriete simple sans notification :
    /// l'inspection s'execute hors thread UI, et c'est RaiseAll — appele sur le
    /// thread UI — qui publie l'ensemble du resultat.
    /// </summary>
    public bool Scanned { get; set; }

    /// <summary>DLL remplacables trouvees dans l'arborescence du jeu.</summary>
    public List<InstalledDll> Dlls { get; } = new();

    /// <summary>
    /// Dossiers contenant sl.interposer.dll. Streamline charge ses plugins et les DLL NGX a
    /// cote de l'interposer : c'est la, et pas forcement a cote de l'executable, que la pile
    /// DLSS 5 doit etre posee.
    /// </summary>
    public List<string> StreamlineDirectories { get; } = new();

    /// <summary>Presence de sl.interposer.dll — le jeu embarque Streamline, donc DLSS-G est integrable.</summary>
    public bool HasStreamline { get; set; }

    /// <summary>Version du paquet Streamline en place, lue sur sl.interposer.dll.</summary>
    public string? StreamlineVersion { get; set; }

    /// <summary>API de rendu presumee, deduite des fichiers presents.</summary>
    public GameApi Api { get; set; } = GameApi.Unknown;

    /// <summary>nvngx_dlssnr.dll present : le runtime Neural Rendering de DLSS 5.</summary>
    public bool HasNeuralRuntime { get; set; }

    /// <summary>dlss5-bridge.addon64 present : le pont DLSS 5 est installe.</summary>
    public bool HasDlss5Bridge { get; set; }

    /// <summary>renodx-dlss5.addon64 present : l'addon RenoDX Neural Rendering.</summary>
    public bool HasDlss5Addon { get; set; }

    /// <summary>
    /// Chaine DLSS 5 complete : le runtime neural, et de quoi l'executer — l'addon
    /// RenoDX, le pont ReShade, ou un paquet OptiScaler DLSSNR.
    /// </summary>
    public bool HasDlss5 => HasNeuralRuntime && (HasDlss5Addon || HasDlss5Bridge || HasOptiScaler);

    /// <summary>
    /// Moteur devine depuis l'arborescence ("Unreal", "Unity") ou null. Sert a proposer
    /// les mods RenoDX generiques quand aucun mod specifique au jeu n'existe.
    /// </summary>
    public string? Engine { get; set; }
    public bool HasReShade { get; set; }
    public bool HasRenoDx { get; set; }
    public bool HasOptiScaler { get; set; }
    public bool HasMfgUnlock { get; set; }

    public bool HasDlss => Dlls.Any(d => d.Kind == DllKind.Dlss);
    public bool HasDlssG => Dlls.Any(d => d.Kind == DllKind.DlssG);
    public bool HasDlssD => Dlls.Any(d => d.Kind == DllKind.DlssD);

    /// <summary>Version du runtime d'upscaling presente, pour la colonne de tableau.</summary>
    public string DlssVersionLabel =>
        Dlls.FirstOrDefault(d => d.Kind == DllKind.Dlss)?.Display ?? "—";

    /// <summary>Etat resume affiche dans la liste, en majuscules comme les autres statuts.</summary>
    public string StateLabel =>
        !Scanned ? Loc.T("state.pending")
        : HasMfgUnlock || HasOptiScaler || HasRenoDx ? Loc.T("state.injected")
        : HasDlss || HasDlssG ? Loc.T("state.ready")
        : Loc.T("state.detected");

    public void RaiseAll()
    {
        OnPropertyChanged(nameof(Dlls));
        OnPropertyChanged(nameof(HasDlss));
        OnPropertyChanged(nameof(HasDlssG));
        OnPropertyChanged(nameof(HasDlssD));
        OnPropertyChanged(nameof(HasReShade));
        OnPropertyChanged(nameof(HasRenoDx));
        OnPropertyChanged(nameof(HasOptiScaler));
        OnPropertyChanged(nameof(HasMfgUnlock));
        OnPropertyChanged(nameof(StreamlineVersion));
        OnPropertyChanged(nameof(Api));
        OnPropertyChanged(nameof(HasNeuralRuntime));
        OnPropertyChanged(nameof(HasDlss5Bridge));
        OnPropertyChanged(nameof(HasDlss5Addon));
        OnPropertyChanged(nameof(HasDlss5));
        OnPropertyChanged(nameof(DlssVersionLabel));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(Engine));
    }
}

/// <summary>Une DLL presente dans un jeu, avec sa version lue depuis les metadonnees du fichier.</summary>
public sealed class InstalledDll
{
    public required string Path { get; init; }
    public required DllKind Kind { get; init; }
    public string? FileVersion { get; init; }
    public long Size { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Version affichable : "310.9.1.0" ou "?" si illisible.</summary>
    public string Display => string.IsNullOrWhiteSpace(FileVersion) ? "?" : FileVersion!;
}
