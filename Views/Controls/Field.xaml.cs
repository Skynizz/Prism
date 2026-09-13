using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Prism.Views.Controls;

/// <summary>Ligne "libelle / valeur" d'un panneau technique.</summary>
public partial class Field : UserControl
{
    public Field() => InitializeComponent();

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Field), new PropertyMetadata(""));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(Field), new PropertyMetadata("—"));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(
        nameof(LabelWidth), typeof(GridLength), typeof(Field),
        new PropertyMetadata(new GridLength(112)));

    public GridLength LabelWidth
    {
        get => (GridLength)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    /// <summary>Bascule la valeur en monospace : versions, chemins, identifiants.</summary>
    public static readonly DependencyProperty MonoProperty = DependencyProperty.Register(
        nameof(Mono), typeof(bool), typeof(Field), new PropertyMetadata(false, OnMonoChanged));

    public bool Mono
    {
        get => (bool)GetValue(MonoProperty);
        set => SetValue(MonoProperty, value);
    }

    private static void OnMonoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var f = (Field)d;
        if (e.NewValue is true && f.TryFindResource("FontMono") is FontFamily mono)
            f.ValueText.FontFamily = mono;
        else if (f.TryFindResource("FontUi") is FontFamily ui)
            f.ValueText.FontFamily = ui;
    }
}
