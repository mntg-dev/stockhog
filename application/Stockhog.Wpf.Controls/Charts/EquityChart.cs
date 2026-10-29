using System.Windows;
using System.Windows.Controls;
using ScottPlot.WPF;

namespace Stockhog.Wpf.Controls.Charts;

/// <summary>
/// Equity curve with the drawdown from the running peak underneath; both panels share the time axis.
/// </summary>
public sealed class EquityChart : ChartBase
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<TimeValue>), typeof(EquityChart), RedrawOnChange());

    public static readonly DependencyProperty EquityLabelProperty = DependencyProperty.Register(
        nameof(EquityLabel), typeof(string), typeof(EquityChart), RedrawOnChange("Equity"));

    public static readonly DependencyProperty DrawdownLabelProperty = DependencyProperty.Register(
        nameof(DrawdownLabel), typeof(string), typeof(EquityChart), RedrawOnChange("Drawdown"));

    private readonly WpfPlot _equity = CreatePlot();
    private readonly WpfPlot _drawdown = CreatePlot();

    public EquityChart()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(7, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        Grid.SetRow(_drawdown, 1);
        grid.Children.Add(_equity);
        grid.Children.Add(_drawdown);
        Content = grid;

        // Zooming or panning one panel moves the other along the time axis.
        _equity.Plot.Axes.Link(_drawdown, x: true, y: false);
        _drawdown.Plot.Axes.Link(_equity, x: true, y: false);
    }

    public IReadOnlyList<TimeValue>? Points
    {
        get => (IReadOnlyList<TimeValue>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public string EquityLabel
    {
        get => (string)GetValue(EquityLabelProperty);
        set => SetValue(EquityLabelProperty, value);
    }

    public string DrawdownLabel
    {
        get => (string)GetValue(DrawdownLabelProperty);
        set => SetValue(DrawdownLabelProperty, value);
    }

    protected override void Render()
    {
        _equity.Plot.Clear();
        _drawdown.Plot.Clear();

        var points = Points ?? [];
        if (points.Count > 1)
        {
            var xs = ToOADates(points.Select(p => p.Time));
            var equity = points.Select(p => p.Value).ToArray();

            var line = _equity.Plot.Add.Scatter(xs, equity);
            line.MarkerSize = 0;
            line.LineWidth = 2;
            line.Color = Accent;

            var drawdown = Drawdown(equity);
            var area = _drawdown.Plot.Add.Scatter(xs, drawdown);
            area.MarkerSize = 0;
            area.Color = Negative;
            area.FillY = true;
            area.FillYColor = Negative.WithAlpha(0.25);
        }

        _equity.Plot.Axes.DateTimeTicksBottom();
        _drawdown.Plot.Axes.DateTimeTicksBottom();
        _equity.Plot.YLabel(EquityLabel);
        _drawdown.Plot.YLabel(DrawdownLabel);
        PercentTicks(_drawdown.Plot.Axes.Left);

        _equity.Plot.Axes.AutoScale();
        _drawdown.Plot.Axes.AutoScale();
        if (points.Count <= 1)
        {
            ShowRecentDates(_equity.Plot);
            ShowRecentDates(_drawdown.Plot);
        }

        _equity.Refresh();
        _drawdown.Refresh();
    }

    /// <summary>Percent below the highest equity seen so far (0 or negative).</summary>
    internal static double[] Drawdown(IReadOnlyList<double> equity)
    {
        var result = new double[equity.Count];
        var peak = double.MinValue;
        for (var i = 0; i < equity.Count; i++)
        {
            peak = Math.Max(peak, equity[i]);
            result[i] = peak > 0 ? (equity[i] / peak - 1) * 100 : 0;
        }

        return result;
    }
}
