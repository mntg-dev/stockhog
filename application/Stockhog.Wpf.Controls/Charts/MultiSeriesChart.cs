using System.Windows;
using ScottPlot;
using ScottPlot.WPF;

namespace Stockhog.Wpf.Controls.Charts;

/// <summary>
/// Several time series on one date axis with a legend, e.g. cumulative returns of compared backtests.
/// </summary>
public sealed class MultiSeriesChart : ChartBase
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(MultiSeriesChart), RedrawOnChange());

    public static readonly DependencyProperty ValueLabelProperty = DependencyProperty.Register(
        nameof(ValueLabel), typeof(string), typeof(MultiSeriesChart), RedrawOnChange(string.Empty));

    public static readonly DependencyProperty ShowPercentProperty = DependencyProperty.Register(
        nameof(ShowPercent), typeof(bool), typeof(MultiSeriesChart), RedrawOnChange(false));

    private readonly WpfPlot _plot = CreatePlot();

    public MultiSeriesChart()
    {
        Content = _plot;
    }

    public IReadOnlyList<ChartSeries>? Series
    {
        get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public string ValueLabel
    {
        get => (string)GetValue(ValueLabelProperty);
        set => SetValue(ValueLabelProperty, value);
    }

    public bool ShowPercent
    {
        get => (bool)GetValue(ShowPercentProperty);
        set => SetValue(ShowPercentProperty, value);
    }

    protected override void Render()
    {
        var plot = _plot.Plot;
        plot.Clear();

        var palette = new ScottPlot.Palettes.Category10();
        var index = 0;
        foreach (var series in Series ?? [])
        {
            if (series.Points.Count == 0)
                continue;

            var line = plot.Add.Scatter(ToOADates(series.Points.Select(p => p.Time)), series.Points.Select(p => p.Value).ToArray());
            line.MarkerSize = 0;
            line.LineWidth = 2;
            line.Color = palette.GetColor(index++);
            line.LegendText = series.Label;
        }

        if (index > 0)
            plot.ShowLegend(Alignment.UpperLeft);

        plot.Axes.DateTimeTicksBottom();
        plot.YLabel(ValueLabel);
        if (ShowPercent)
            PercentTicks(plot.Axes.Left);

        plot.Axes.AutoScale();
        if (index == 0)
            ShowRecentDates(plot);

        _plot.Refresh();
    }
}
