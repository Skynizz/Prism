using Prism.Core;

namespace Prism.Models;

/// <summary>D'ou vient l'identite d'un jeu : ce qui la rend sure, ou non.</summary>
public enum IdentitySource
{
    /// <summary>Rien de mieux que le nom du dossier ou de l'executable.</summary>
    Folder,
    /// <summary>Jeu installe par Steam : son manifeste fait foi.</summary>
    Steam,
    /// <summary>Fichier laisse par l'editeur dans le dossier du jeu (steam_appid.txt, goggame-*.info).</summary>
    LocalFile,
    /// <summary>Recherche sur le magasin Steam, sans ambiguite.</summary>
    StoreSearch,
    /// <summary>Choisi par l'utilisateur.</summary>
    User
}

/// <summary>
/// Nom officiel et AppID Steam d'un jeu, quelle que soit sa plateforme. L'AppID sert de cle
/// aux catalogues (mods RenoDX HDR) exactement comme pour un jeu Steam.
/// </summary>
public sealed class GameIdentity
{
    public string Name { get; set; } = "";
    public long? SteamAppId { get; set; }
    public IdentitySource Source { get; set; }

    public string SourceLabel => Source switch
    {
        IdentitySource.Steam => "Steam",
        IdentitySource.LocalFile => Loc.T("ident.src.file"),
        IdentitySource.StoreSearch => Loc.T("ident.src.store"),
        IdentitySource.User => Loc.T("ident.src.user"),
        _ => Loc.T("ident.src.folder")
    };
}

/// <summary>Resultat de recherche propose a l'utilisateur.</summary>
public sealed record IdentityCandidate(string Name, long AppId)
{
    public string Label => $"{Name}  ·  {AppId}";
}
