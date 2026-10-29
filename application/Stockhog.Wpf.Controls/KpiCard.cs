using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stockhog.Wpf.Controls;

/// <summary>
/// A titled key figure, e.g. "Sharpe ratio  0.90". Lookless: the template is in Themes/Generic.xaml.
/// </summary>
public sealed class KpiCard : Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(KpiCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(KpiCard), new PropertyMetadata("–"));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(KpiCard), new PropertyMetadata(Brushes.Black));

    static KpiCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(KpiCard), new FrameworkPropertyMetadata(typeof(KpiCard)));
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }
}
