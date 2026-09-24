namespace Prism.Models;

public enum GamePlatform
{
    Unknown, Steam, Epic, Gog, Xbox, EaApp, Ubisoft, BattleNet, Manual
}

/// <summary>Famille de DLL remplacable, telle qu'exposee par le manifeste DLSS Swapper.</summary>
public enum DllKind
{
    /// <summary>nvngx_dlss.dll — upscaling (Super Resolution).</summary>
    Dlss,
    /// <summary>nvngx_dlssg.dll — Frame Generation / Multi Frame Generation.</summary>
    DlssG,
    /// <summary>nvngx_dlssd.dll — Ray Reconstruction / Neural Rendering.</summary>
    DlssD,
    /// <summary>amd_fidelityfx_dx12.dll</summary>
    FsrDx12,
    /// <summary>amd_fidelityfx_vk.dll</summary>
    FsrVk,
    /// <summary>libxess.dll</summary>
    XeSS,
    /// <summary>libxess_fg.dll</summary>
    XeSSFg,
    /// <summary>libxell.dll</summary>
    XeLL
}

/// <summary>Generation NVIDIA — determine ce qui est faisable en frame generation.</summary>
public enum GpuGeneration
{
    Unknown, NonNvidia, Turing, Ampere, AdaLovelace, Blackwell, NewerNvidia
}

/// <summary>Chemin technique utilise pour produire les images generees.</summary>
public enum FgBackend
{
    /// <summary>Aucun.</summary>
    None,
    /// <summary>DLSS-G natif du jeu, sans surcouche (x2 sur Ada, x2..x6 sur Blackwell).</summary>
    NativeDlssG,
    /// <summary>RTX40MFG-Unlock : debride les multiplicateurs MFG via proxy DLL.</summary>
    Rtx40MfgUnlock,
    /// <summary>MFGAdaUnlock : addon RenoDX, agit en memoire sans remplacer de fichier.</summary>
    MfgAdaUnlock,
    /// <summary>dlssg for sm_86 : moteur DLSS-G recompile pour Ampere (RTX 30).</summary>
    DlssgSm86,
    /// <summary>dlssg for sm_75 : moteur DLSS-G recompile pour Turing (RTX 20).</summary>
    DlssgSm75,
    /// <summary>OptiScaler : pont FSR 3.1 / XeSS-FG, marche sur Turing et Ampere.</summary>
    OptiScaler,
    /// <summary>DLSS Enabler : redirige DLSS-G vers FSR 3.1 FG.</summary>
    DlssEnabler,
    /// <summary>
    /// Vrai DLSS-G de NVIDIA dans un jeu qui n'a qu'un upscaler : fork OptiScaler (wilsjo2),
    /// FGInput=upscaler, FGOutput=dlssg, Streamline 2.14.1 epingle. x2 sur Ada, MFG sur Blackwell.
    /// </summary>
    InjectedDlssG,
    /// <summary>OptiScaler officiel, FGInput=upscaler, FGOutput=fsrfg : x2, toute carte, jeu sans FG.</summary>
    OptiFg
}

public enum InstallState
{
    NotInstalled, Installed, UpdateAvailable, Unknown
}

/// <summary>
/// Etats affiches par le systeme de statut. Le vocabulaire est celui d'un outil
/// technique : un point discret plus un mot, jamais une pastille coloree.
/// </summary>
public enum UiStatus
{
    Idle, Detected, Ready, Active, Injected, Disabled, Warning, Error
}

/// <summary>Niveau de detail expose par l'interface.</summary>
public enum DetailLevel { Basic, Advanced, Expert }
