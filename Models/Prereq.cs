using Prism.Core;
namespace Prism.Models;

/// <summary>
/// Un prerequis d'une voie d'injection : son etat reel, la valeur constatee, et soit
/// l'action que Prism mene, soit la raison pour laquelle il s'abstient.
/// </summary>
public sealed class PrereqCheck
{
    public required string Label { get; init; }
    public required UiStatus State { get; init; }
    /// <summary>Valeur constatee : version trouvee, "absent", "non supporte".</summary>
    public required string Detail { get; init; }
    public string? Hint { get; init; }

    /// <summary>Correctif que Prism sait appliquer sur place, le cas echeant.</summary>
    public PrereqFix Fix { get; init; } = PrereqFix.None;

    /// <summary>Vrai si Prism sait resoudre ce point lui-meme.</summary>
    public bool Actionable => Fix != PrereqFix.None && State is UiStatus.Error or UiStatus.Warning;

    /// <summary>Libelle du bouton de correction.</summary>
    public string FixLabel => Fix switch
    {
        PrereqFix.DeployDlss => Loc.T("fix.deploy_dlss"),
        PrereqFix.DeployDlssG => Loc.T("fix.deploy_dlssg"),
        PrereqFix.DeployStreamline => Loc.T("fix.deploy_streamline"),
        PrereqFix.InstallReShade => Loc.T("fix.install_reshade"),
        PrereqFix.AdoptNeuralRuntime => Loc.T("fix.adopt"),
        PrereqFix.InstallMfgAddon => Loc.T("fix.install_mfg"),
        PrereqFix.InstallDlss5Addon => Loc.T("fix.install_dlss5"),
        _ => Loc.T("fix.generic")
    };

    /// <summary>Un prerequis en erreur empeche la voie de fonctionner.</summary>
    public bool Blocking => State is UiStatus.Error;
}

/// <summary>API de rendu presumee d'un titre, deduite des fichiers presents.</summary>
public enum GameApi { Unknown, DirectX11, DirectX12, Vulkan }

public static class GameApiExtensions
{
    public static string Label(this GameApi api) => api switch
    {
        GameApi.DirectX11 => "DirectX 11",
        GameApi.DirectX12 => "DirectX 12",
        GameApi.Vulkan => "Vulkan",
        _ => Loc.T("api.unknown")
    };

    /// <summary>Forme courte pour le pipeline et les tableaux.</summary>
    public static string Short(this GameApi api) => api switch
    {
        GameApi.DirectX11 => "D3D11",
        GameApi.DirectX12 => "D3D12",
        GameApi.Vulkan => "VK",
        _ => "n/d"
    };
}
