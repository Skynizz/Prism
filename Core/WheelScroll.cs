using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Prism.Core;

/// <summary>
/// Molette fiable partout.
///
/// Une liste pleinement deployee dans une page defilante garde son propre
/// <see cref="ScrollViewer"/> : il capte la molette alors qu'il n'a rien a faire
/// defiler, et la page reste immobile — il faut alors tirer la barre a la main.
/// Ce gestionnaire, pose une fois sur toutes les fenetres, confie chaque cran de
/// molette au defileur le plus proche du curseur qui peut <i>vraiment</i> bouger
/// dans ce sens, et remonte vers les parents sinon.
/// </summary>
public static class WheelScroll
{
    private const double Epsilon = 0.5;

    public static void Register()
        => EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel), handledEventsToo: false);

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || e.OriginalSource is not DependencyObject source) return;

        var up = e.Delta > 0;

        for (var current = source; current is not null; current = ParentOf(current))
        {
            if (current is not ScrollViewer viewer || !CanScroll(viewer, up)) continue;

            // Un cran standard vaut 120 : trois lignes, comme le reste de Windows.
            var lines = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta) / 40.0));
            for (var i = 0; i < lines; i++)
            {
                if (up) viewer.LineUp();
                else viewer.LineDown();
            }

            e.Handled = true;
            return;
        }
    }

    private static bool CanScroll(ScrollViewer viewer, bool up)
    {
        if (viewer.ScrollableHeight <= Epsilon) return false;
        return up
            ? viewer.VerticalOffset > Epsilon
            : viewer.VerticalOffset < viewer.ScrollableHeight - Epsilon;
    }

    private static DependencyObject? ParentOf(DependencyObject node)
    {
        if (node is Visual or Visual3D)
        {
            var parent = VisualTreeHelper.GetParent(node);
            if (parent is not null) return parent;
        }
        return LogicalTreeHelper.GetParent(node);
    }
}
