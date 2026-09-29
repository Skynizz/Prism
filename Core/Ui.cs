using System.Windows;
using System.Windows.Threading;

namespace Prism.Core;

public static class Ui
{
    /// <summary>
    /// Laisse l'interface se redessiner (barre de travail) avant un travail lourd. Sans interface
    /// active sur ce fil (bancs de test), ne fait rien.
    /// </summary>
    public static async Task Yield()
    {
        if (Application.Current?.Dispatcher is { } d && d.CheckAccess())
            await Dispatcher.Yield(DispatcherPriority.Background);
    }
}
