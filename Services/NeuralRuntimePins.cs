namespace Prism.Services;

/// <summary>
/// Builds repatchees de <c>nvngx_dlssnr.dll</c> acceptees par Prism.
///
/// Le runtime neural d'origine ne s'initialise que sur RTX 50 : sur une RTX 40, la
/// surcouche RenoDX le reconnait mais renvoie <c>0xBAD00001</c>. Les RTX 20 a 40 ont
/// donc besoin d'une build repatchee — et le repatch rompt la signature NVIDIA par
/// construction. Faute de signature, Prism exige l'empreinte exacte d'une build
/// connue, relevee sur la release publiee et verifiee a chaque installation.
///
/// Une nouvelle build n'est acceptee qu'apres ajout explicite de son empreinte ici.
/// </summary>
public static class NeuralRuntimePins
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // rhi-repo dlssnr-310.8.SF-v2 : build multi-generations (RTX 20, 30, 40).
        ["6EB209E764F39872625DEBD6ABAF45E2BB6322F6F270F781F70C059AE30B3927"] = "310.8.SF-v2",

        // rhi-repo dlssnr-310.8.0-RTX40 : portage mono-generation pour Ada.
        ["4B8D19BC3EFF58A084F5ECA7489C921501C203450169FB82FF4F649A4482BA05"] = "310.8.0-RTX40"
    };

    public static bool IsKnown(string? sha256) => sha256 is not null && Known.ContainsKey(sha256);

    public static string? BuildOf(string? sha256) => sha256 is not null && Known.TryGetValue(sha256, out var b) ? b : null;
}
