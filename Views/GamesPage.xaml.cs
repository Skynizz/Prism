using System.Windows;
using System.Windows.Controls;
using Prism.ViewModels;

namespace Prism.Views;

public partial class GamesPage : UserControl
{
    public GamesPage()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    /// <summary>
    /// La page publie sa largeur utile : le modele en deduit s'il faut replier
    /// l'inspecteur et alleger les lignes de la table.
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        => (DataContext as MainViewModel)?.SetViewport(e.NewSize.Width);
}
