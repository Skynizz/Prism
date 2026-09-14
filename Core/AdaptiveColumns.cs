using System.Windows;
using System.Windows.Controls;

namespace Prism.Core;

/// <summary>
/// Colonnes qui suivent la largeur disponible : autant de colonnes que la largeur en
/// permet, entre 1 et <see cref="MaxColumns"/>. Deux modes :
///  - rangees egales : les blocs se suivent de gauche a droite et partagent la hauteur de
///    leur rangee (tableaux de bord) ;
///  - maconnerie : chaque bloc rejoint la colonne la plus courte (pages longues), ce qui
///    remplit l'ecran sans trous.
/// </summary>
public sealed class AdaptiveColumns : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(AdaptiveColumns),
        new FrameworkPropertyMetadata(480.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(AdaptiveColumns),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(AdaptiveColumns),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty EqualRowsProperty = DependencyProperty.Register(
        nameof(EqualRows), typeof(bool), typeof(AdaptiveColumns),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public bool EqualRows { get => (bool)GetValue(EqualRowsProperty); set => SetValue(EqualRowsProperty, value); }

    private readonly record struct Slot(UIElement Child, double X, double Y, double W, double H);

    private List<UIElement> Visible() =>
        InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    private int ColumnsFor(double width, int count)
    {
        var fit = (int)Math.Floor((width + Gap) / (MinColumnWidth + Gap));
        return Math.Max(1, Math.Min(Math.Min(fit, Math.Max(1, MaxColumns)), Math.Max(1, count)));
    }

    private (List<Slot> Slots, double Height) Layout(double width, bool measure)
    {
        var children = Visible();
        foreach (var hidden in InternalChildren.Cast<UIElement>().Except(children))
            if (measure) hidden.Measure(new Size(0, 0));

        var slots = new List<Slot>();
        if (children.Count == 0) return (slots, 0);

        var cols = ColumnsFor(width, children.Count);
        var colW = Math.Max(0, (width - Gap * (cols - 1)) / cols);

        if (measure)
            foreach (var c in children) c.Measure(new Size(colW, double.PositiveInfinity));

        if (EqualRows)
        {
            var y = 0.0;
            for (var start = 0; start < children.Count; start += cols)
            {
                var row = children.Skip(start).Take(cols).ToList();
                var h = row.Max(c => c.DesiredSize.Height);
                for (var i = 0; i < row.Count; i++)
                    slots.Add(new Slot(row[i], i * (colW + Gap), y, colW, h));
                y += h + Gap;
            }
            return (slots, y - Gap);
        }

        var heights = new double[cols];
        foreach (var c in children)
        {
            var col = Array.IndexOf(heights, heights.Min());
            slots.Add(new Slot(c, col * (colW + Gap), heights[col], colW, c.DesiredSize.Height));
            heights[col] += c.DesiredSize.Height + Gap;
        }
        return (slots, Math.Max(0, heights.Max() - Gap));
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MinColumnWidth * Math.Max(1, MaxColumns) : available.Width;
        var (_, height) = Layout(width, measure: true);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (slots, _) = Layout(final.Width, measure: false);
        foreach (var s in slots) s.Child.Arrange(new Rect(s.X, s.Y, s.W, s.H));
        return final;
    }
}

/// <summary>
/// Retour a la ligne qui remplit la largeur : chaque ligne pleine partage l'espace restant
/// entre ses elements. La derniere ligne d'une chaine sur plusieurs lignes garde sa taille
/// naturelle, pour ne pas etirer deux elements isoles sur tout l'ecran.
/// </summary>
public sealed class StretchWrapPanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        var width = available.Width;
        double x = 0, y = 0, lineH = 0, maxW = 0;

        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var d = c.DesiredSize;
            if (x > 0 && x + d.Width > width) { y += lineH; x = 0; lineH = 0; }
            x += d.Width;
            lineH = Math.Max(lineH, d.Height);
            maxW = Math.Max(maxW, x);
        }

        return new Size(double.IsInfinity(width) ? maxW : width, y + lineH);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var lines = new List<List<UIElement>> { new() };
        var x = 0.0;
        foreach (UIElement c in InternalChildren)
        {
            if (x > 0 && x + c.DesiredSize.Width > final.Width) { lines.Add(new()); x = 0; }
            lines[^1].Add(c);
            x += c.DesiredSize.Width;
        }

        var y = 0.0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Count == 0) continue;

            var used = line.Sum(c => c.DesiredSize.Width);
            var stretch = lines.Count == 1 || i < lines.Count - 1;
            var extra = stretch ? Math.Max(0, (final.Width - used) / line.Count) : 0;
            var h = line.Max(c => c.DesiredSize.Height);

            var cx = 0.0;
            foreach (var c in line)
            {
                var w = c.DesiredSize.Width + extra;
                c.Arrange(new Rect(cx, y, w, h));
                cx += w;
            }
            y += h;
        }
        return final;
    }
}
