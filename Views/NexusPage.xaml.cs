using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Prism.Core;
using Prism.Services;
using Prism.ViewModels;

namespace Prism.Views;

/// <summary>
/// Le site Nexus Mods dans Prism. Le navigateur (WebView2) n'est cree qu'a la premiere
/// visite de la page. Ses cookies vivent dans le dossier de Prism : la connexion Nexus
/// est gardee d'une session a l'autre.
///
/// Deux boutons du site sont pris en charge :
///  - « Manual download » : le fichier arrive dans Prism au lieu du dossier Telechargements ;
///  - « Mod Manager Download » : le lien nxm:// est intercepte et renvoie a la page de
///    telechargement manuel du meme fichier.
/// </summary>
public partial class NexusPage : UserControl
{
    private static readonly string[] Archives = { ".zip", ".7z", ".rar" };

    private WebView2? _web;
    private bool _starting;
    private string? _homeFor;
    private MainViewModel? _main;

    public NexusPage()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
    }

    private NexusViewModel? Vm => _main?.Nexus;

    private void Attach(MainViewModel? main)
    {
        if (_main is not null)
        {
            _main.PropertyChanged -= OnMainChanged;
            _main.Nexus.NavigateRequested -= Navigate;
        }
        _main = main;
        if (_main is null) return;
        _main.PropertyChanged += OnMainChanged;
        _main.Nexus.NavigateRequested += Navigate;
    }

    /// <summary>Un autre jeu choisi pendant que la page est ouverte : sa page Nexus.</summary>
    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Detail) && IsVisible && _web?.CoreWebView2 is not null)
            _ = GoHomeIfNeededAsync();
    }

    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        if (_web is null) await StartAsync();
        else await GoHomeIfNeededAsync();
    }

    private async Task GoHomeIfNeededAsync()
    {
        var id = _main?.Detail?.Game.Id ?? "";
        if (_homeFor == id || Vm is null) return;
        _homeFor = id;
        await Vm.GoHomeAsync();
    }

    private async Task StartAsync()
    {
        if (_starting || Vm is null) return;
        _starting = true;
        try
        {
            var web = new WebView2
            {
                CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = AppPaths.Ensure(Path.Combine(AppPaths.Root, "webview2"))
                },
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 16, 16, 16)
            };
            Host.Content = web;
            await web.EnsureCoreWebView2Async();
            _web = web;

            var core = web.CoreWebView2;
            core.SourceChanged += async (_, _) =>
            {
                Vm.Url = core.Source;
                await Vm.OnPageChangedAsync(core.Source);
            };
            core.NavigationStarting += OnNavigationStarting;
            core.LaunchingExternalUriScheme += OnExternalScheme;
            core.NewWindowRequested += OnNewWindow;
            core.DownloadStarting += OnDownloadStarting;

            // Le navigateur se ferme avec la fenetre, pas au changement de page.
            if (Window.GetWindow(this) is { } window) window.Closed += (_, _) => web.Dispose();

            _homeFor = _main?.Detail?.Game.Id ?? "";
            Navigate(await Vm.HomeUrlAsync());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Host.Content = null;
            Vm.RuntimeMissing = true;
            Log.Warn("nexus", "WebView2 runtime not found");
        }
        catch (Exception ex)
        {
            Log.Error("nexus", $"WebView2 start failed: {ex.Message}");
        }
        finally { _starting = false; }
    }

    private void Navigate(string url)
    {
        if (_web?.CoreWebView2 is { } core) core.Navigate(url);
    }

    // ----------------------------------------------------------------- Liens

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith("nxm:", StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true;
        Vm?.OnNxm(e.Uri);
    }

    private void OnExternalScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
        if (e.Uri.StartsWith("nxm:", StringComparison.OrdinalIgnoreCase)) Vm?.OnNxm(e.Uri);
    }

    /// <summary>Les pages Nexus restent ici ; un lien vers un autre site part dans le navigateur habituel.</summary>
    private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            (uri.Host.EndsWith("nexusmods.com", StringComparison.OrdinalIgnoreCase) || uri.Scheme == "nxm"))
        {
            if (uri.Scheme == "nxm") Vm?.OnNxm(e.Uri);
            else Navigate(e.Uri);
            return;
        }
        if (uri is { Scheme: "https" or "http" })
            Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
    }

    // --------------------------------------------------------- Telechargements

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var suggested = Path.GetFileName(e.ResultFilePath);
        // Autre chose qu'une archive de mod : comportement normal du navigateur.
        if (Vm is null || !Archives.Contains(Path.GetExtension(suggested).ToLowerInvariant())) return;

        var core = _web!.CoreWebView2;
        var pageUrl = core.Source;
        var title = core.DocumentTitle;
        var path = Vm.DownloadPathFor(suggested);
        if (File.Exists(path)) File.Delete(path);

        e.ResultFilePath = path;
        e.Handled = true;

        var op = e.DownloadOperation;
        Vm.OnDownloadStarted(path);
        op.BytesReceivedChanged += (_, _) => Vm.OnDownloadProgress(op.BytesReceived, (long?)op.TotalBytesToReceive);
        op.StateChanged += async (_, _) =>
        {
            if (op.State == CoreWebView2DownloadState.Completed)
                await Vm.OnDownloadedAsync(path, pageUrl, title);
            else if (op.State == CoreWebView2DownloadState.Interrupted)
                Vm.OnDownloadFailed(op.InterruptReason.ToString());
        };
    }

    // ---------------------------------------------------------------- Boutons

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_web?.CoreWebView2 is { CanGoBack: true } core) core.GoBack();
    }

    private void OnForward(object sender, RoutedEventArgs e)
    {
        if (_web?.CoreWebView2 is { CanGoForward: true } core) core.GoForward();
    }

    private void OnReload(object sender, RoutedEventArgs e) => _web?.CoreWebView2?.Reload();

    private async void OnHome(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        _homeFor = _main?.Detail?.Game.Id ?? "";
        await Vm.GoHomeAsync();
    }

    private void OnGetRuntime(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
}
