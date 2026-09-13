using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace Prism.Views.Controls;

/// <summary>Representation de la chaine de rendu, du jeu jusqu'a l'ecran.</summary>
public partial class Pipeline : UserControl
{
    public Pipeline() => InitializeComponent();

    public static readonly DependencyProperty StagesProperty = DependencyProperty.Register(
        nameof(Stages), typeof(IEnumerable), typeof(Pipeline), new PropertyMetadata(null));

    public IEnumerable? Stages
    {
        get => (IEnumerable?)GetValue(StagesProperty);
        set => SetValue(StagesProperty, value);
    }
}
