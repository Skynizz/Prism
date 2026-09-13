using System.ComponentModel;

namespace Prism.Core;

/// <summary>
/// Preferences d'affichage partagees par toutes les vues, liees en statique :
/// <c>{Binding ShowDetails, Source={x:Static core:UiPrefs.I}}</c>.
/// </summary>
public sealed class UiPrefs : INotifyPropertyChanged
{
    public static UiPrefs I { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _showDetails;

    /// <summary>
    /// Les explications (style T.Prose) ne s'affichent qu'a la demande : par defaut
    /// l'interface s'en tient aux mots-cles. Le bouton « ? » de la barre de titre bascule.
    /// </summary>
    public bool ShowDetails
    {
        get => _showDetails;
        set
        {
            if (_showDetails == value) return;
            _showDetails = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowDetails)));
        }
    }
}
