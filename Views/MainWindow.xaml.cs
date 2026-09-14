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
        SizeChanged += (_, _) => ApplyScale();
    }

    /// <summary>Taille de fenetre pour laquelle l'interface est dessinee a l'echelle 1.</summary>
    private const double BaseWidth = 1480, BaseHeight = 900;

    /// <summary>Agrandissement maximal : 1,6 couvre un plein ecran 2560 × 1440.</summary>
    private const double MaxScale = 1.6;

    private double _scale = 1;

    /// <summary>
    /// Interface proportionnelle a la fenetre : en plein ecran, texte, controles et marges
    /// grandissent ensemble au lieu de laisser un outil minuscule au milieu du vide. Jamais
    /// en dessous de 1 : une petite fenetre gagne des colonnes en moins, pas du texte illisible.
    /// Par paliers de 5 % pour ne pas refaire la mise en page a chaque pixel.
    /// </summary>
    private void ApplyScale()
    {
        var w = RootShell.ActualWidth;
        var h = RootShell.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var s = Math.Clamp(Math.Min(w / BaseWidth, h / BaseHeight), 1.0, MaxScale);
        s = Math.Round(s * 20) / 20;
        if (Math.Abs(s - _scale) < 0.001 && ShellGrid.LayoutTransform is not null) return;
        _scale = s;

        ShellGrid.LayoutTransform = s == 1 ? Transform.Identity : new ScaleTransform(s, s);
        // Le rendu « Display » aligne le texte sur les pixels a sa taille d'origine : agrandi,
        // il deviendrait flou. « Ideal » le redessine net a la taille reelle.
        TextOptions.SetTextFormattingMode(this, s == 1 ? TextFormattingMode.Display : TextFormattingMode.Ideal);

        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome
            && TryFindResource("TitleBarH") is GridLength title)
            chrome.CaptionHeight = title.Value * s;
    }

    public MainViewModel ViewModel => _vm;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyChromeState();
        ApplyScale();
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
