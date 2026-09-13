using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Prism.Core;
using Prism.ViewModels;

namespace Prism.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly bool _startup;

    public MainWindow() : this(new MainViewModel(), startup: true) { }

    /// <summary>
    /// Fenetre reconstruite apres un changement de theme : meme etat, meme modele de vue,
    /// sans nouveau demarrage ni nouveau balayage des jeux.
    /// </summary>
    public MainWindow(MainViewModel vm, bool startup = false)
    {
        _vm = vm;
        _startup = startup;

        InitializeComponent();
        DataContext = _vm;

        StateChanged += OnStateChanged;
        Loaded += OnLoaded;
    }

    public MainViewModel ViewModel => _vm;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyChromeState();
        if (_startup) await _vm.StartupAsync();
    }

    private void OnStateChanged(object? sender, EventArgs e) => ApplyChromeState();

    /// <summary>
    /// Une fenetre WindowChrome maximisee deborde de l'ecran de la largeur de sa
    /// bordure de redimensionnement : on compense par une marge, et on echange le
    /// glyphe agrandir/restaurer.
    /// </summary>
    private void ApplyChromeState()
    {
        var maximized = WindowState == WindowState.Maximized;

        RootShell.Margin = maximized ? new Thickness(7) : new Thickness(0);
        RootShell.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);

        if (TryFindResource(maximized ? "IcoRestore" : "IcoMax") is Geometry g)
            MaxGlyph.Data = g;
    }

    private void OnToggleDetails(object sender, RoutedEventArgs e) => _vm.ShowDetails = !_vm.ShowDetails;

    /// <summary>Theme Studio : chaque page arrive en fondu, avec un leger glissement vertical.</summary>
    private void OnPageChanged(object sender, SelectionChangedEventArgs e)
    {
        // Les listes des pages remontent aussi leur SelectionChanged : seul le changement de page compte.
        if (!Theme.IsStudio || !ReferenceEquals(e.OriginalSource, PagesHost)) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PagesHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });

        if (PagesHost.RenderTransform is TranslateTransform shift)
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
