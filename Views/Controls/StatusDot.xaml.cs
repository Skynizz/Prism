using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Prism.Models;

namespace Prism.Views.Controls;

/// <summary>Point d'etat + libelle. Le vocabulaire est fige par <see cref="UiStatus"/>.</summary>
public partial class StatusDot : UserControl
{
    public StatusDot() => InitializeComponent();

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(UiStatus), typeof(StatusDot),
        new PropertyMetadata(UiStatus.Idle, OnStatusChanged));

    public UiStatus Status
    {
        get => (UiStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>Texte affiche. Vide, il est deduit de l'etat.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusDot),
        new PropertyMetadata(null, OnStatusChanged));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(StatusDot), new PropertyMetadata(Brushes.Gray));

    public Brush Brush
    {
        get => (Brush)GetValue(BrushProperty);
        private set => SetValue(BrushProperty, value);
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(StatusDot), new PropertyMetadata(""));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        private set => SetValue(LabelProperty, value);
    }

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StatusDot)d).Refresh();

    private void Refresh()
    {
        Label = Text ?? Status switch
        {
            UiStatus.Active => "ACTIVE",
            UiStatus.Ready => "READY",
            UiStatus.Detected => "DETECTED",
            UiStatus.Injected => "INJECTED",
            UiStatus.Disabled => "DISABLED",
            UiStatus.Warning => "WARNING",
            UiStatus.Error => "ERROR",
            _ => "IDLE"
        };

        var key = Status switch
        {
            UiStatus.Active or UiStatus.Ready or UiStatus.Injected => "BrOk",
            UiStatus.Detected => "BrInfo",
            UiStatus.Warning => "BrWarn",
            UiStatus.Error => "BrErr",
            _ => "BrIdle"
        };

        Brush = TryFindResource(key) as Brush
                ?? Application.Current?.TryFindResource(key) as Brush
                ?? Brushes.Gray;
    }
}
