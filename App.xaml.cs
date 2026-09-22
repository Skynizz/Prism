using System.Windows;
using System.Windows.Threading;
using Prism.Core;
using Prism.Services;

namespace Prism;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Info("app", $"Prism {UpdateService.CurrentLabel} starting");

        // Fichiers .old laisses par une mise a jour : la nouvelle version tourne, ils partent.
        UpdateService.CleanupPreviousVersion();

        // Une exception non geree dans un gestionnaire d'interface ne doit pas fermer
        // l'application au milieu d'une operation sur les fichiers d'un jeu.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("app", $"Unhandled exception: {args.ExceptionObject}");

        var settings = new SettingsStore().Current;

        // Langue, theme et niveau d'explication avant la premiere fenetre : chaque liaison
        // et chaque style se resolvent d'emblee dans le bon etat.
        Loc.I.Set(string.IsNullOrWhiteSpace(settings.Language) ? Loc.DetectDefault() : settings.Language);
        Theme.Apply(settings.Theme);
        UiPrefs.I.ShowDetails = settings.ShowDetails;
        UiPrefs.I.ReduceMotion = settings.ReduceMotion;

        // Cadence des animations. Par defaut WPF suit l'ecran : sur une dalle 120 ou 240 Hz,
        // un simple survol fait redessiner la fenetre a cette frequence pour rien. La brider
        // reduit la charge GPU et, surtout, limite les variations de frequence qui font
        // scintiller les OLED en VRR.
        try
        {
            System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(System.Windows.Media.Animation.Timeline),
                new FrameworkPropertyMetadata { DefaultValue = settings.ReduceMotion ? 24 : 60 });
        }
        catch (Exception ex) { Log.Info("app", $"Animation frame rate not capped: {ex.Message}"); }

        // Molette fiable dans toutes les pages, y compris au-dessus des listes deployees.
        WheelScroll.Register();

        MainWindow = new Views.MainWindow();
        MainWindow.Show();
    }

    /// <summary>
    /// Change de theme. Les jetons suivent aussitot ; les gabarits etant fixes a la
    /// creation, la fenetre est reconstruite sur le meme modele de vue, a la meme place.
    /// </summary>
    public static void SwitchTheme(string code)
    {
        if (Theme.Normalize(code) == Theme.Current) return;
        Theme.Apply(code);

        if (Current.MainWindow is not Views.MainWindow old) return;

        var fresh = new Views.MainWindow(old.ViewModel)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = old.Left,
            Top = old.Top,
            Width = old.Width,
            Height = old.Height
        };

        Current.MainWindow = fresh;
        fresh.Show();
        if (old.WindowState == WindowState.Maximized) fresh.WindowState = WindowState.Maximized;
        old.Close();

        Log.Info("app", $"Theme: {Theme.Current}");
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("app", $"UI exception: {e.Exception}");
        MessageBox.Show(
            $"{e.Exception.Message}\n\nDetails written to:\n{Log.CurrentFile}",
            "Prism — unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
