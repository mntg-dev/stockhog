using System.Windows;
using ScottPlot;
using ScottPlot.WPF;

namespace Stockhog.Wpf.Controls.Charts;

/// <summary>
/// Many series over a numeric x axis: background series in a faint grey, highlighted ones on top in colour and in the
/// legend, e.g. simulated equity paths around the historical one.
/// </summary>
public sealed class XYSeriesChart : ChartBase
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<XYSeries>), typeof(XYSeriesChart), RedrawOnChange());

    public static readonly DependencyProperty XLabelProperty = DependencyProperty.Register(
        nameof(XLabel), typeof(string), typeof(XYSeriesChart), RedrawOnChange(string.Empty));

    public static readonly DependencyProperty YLabelProperty = DependencyProperty.Register(
        nameof(YLabel), typeof(string), typeof(XYSeriesChart), RedrawOnChange(string.Empty));

    private static readonly ScottPlot.Color Background = ScottPlot.Color.FromHex("#90A4AE").WithAlpha(0.35);

    private readonly WpfPlot _plot = CreatePlot();

    public XYSeriesChart()
    {
        Content = _plot;
    }

    public IReadOnlyList<XYSeries>? Series
    {
        get => (IReadOnlyList<XYSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public string XLabel
    {
        get => (string)GetValue(XLabelProperty);
        set => SetValue(XLabelProperty, value);
    }

    public string YLabel
    {
        get => (string)GetValue(YLabelProperty);
        set => SetValue(YLabelProperty, value);
    }

    protected override void Render()
    {
        var plot = _plot.Plot;
        plot.Clear();

        var palette = new ScottPlot.Palettes.Category10();
        var highlighted = 0;

        // Background series first, so the highlighted ones are drawn over them.
        foreach (var series in (Series ?? []).Where(s => s.Values.Count > 0).OrderBy(s => s.IsHighlighted))
        {
            var xs = Enumerable.Range(0, series.Values.Count).Select(i => (double)i).ToArray();
            var line = plot.Add.Scatter(xs, series.Values.ToArray());
            line.MarkerSize = 0;
            if (series.IsHighlighted)
            {
                line.LineWidth = 2.5f;
                line.Color = highlighted == 0 ? Accent : palette.GetColor(highlighted);
                line.LegendText = series.Label;
                highlighted++;
            }
            else
            {
                line.LineWidth = 1;
                line.Color = Background;
            }
        }

        if (highlighted > 0)
            plot.ShowLegend(Alignment.UpperLeft);

        // The x axis counts steps (e.g. trades): whole numbers only.
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { IntegerTicksOnly = true };
        plot.XLabel(XLabel);
        plot.YLabel(YLabel);
        plot.Axes.AutoScale();
        _plot.Refresh();
    }
}
