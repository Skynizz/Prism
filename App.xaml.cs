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
        Log.Info("app", "Demarrage de Prism");

        // Une exception non geree dans un gestionnaire d'interface ne doit pas fermer
        // l'application au milieu d'une operation sur les fichiers d'un jeu.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("app", $"Exception non geree : {args.ExceptionObject}");

        var settings = new SettingsStore().Current;

        // Langue, theme et niveau d'explication avant la premiere fenetre : chaque liaison
        // et chaque style se resolvent d'emblee dans le bon etat.
        Loc.I.Set(string.IsNullOrWhiteSpace(settings.Language) ? Loc.DetectDefault() : settings.Language);
        Theme.Apply(settings.Theme);
        UiPrefs.I.ShowDetails = settings.ShowDetails;

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

        Log.Info("app", $"Theme : {Theme.Current}");
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("app", $"Exception interface : {e.Exception}");
        MessageBox.Show(
            $"{e.Exception.Message}\n\nDetail consigne dans :\n{Log.CurrentFile}",
            "Prism — erreur inattendue",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
