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

        // Langue avant la premiere fenetre : chaque liaison se resout d'emblee dans la bonne.
        var language = new SettingsStore().Current.Language;
        Loc.I.Set(string.IsNullOrWhiteSpace(language) ? Loc.DetectDefault() : language);

        // Molette fiable dans toutes les pages, y compris au-dessus des listes deployees.
        WheelScroll.Register();

        MainWindow = new Views.MainWindow();
        MainWindow.Show();
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
