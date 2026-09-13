using System.Windows;
using System.Windows.Media;
using Prism.ViewModels;

namespace Prism.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        StateChanged += OnStateChanged;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyChromeState();
        await _vm.StartupAsync();
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

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
