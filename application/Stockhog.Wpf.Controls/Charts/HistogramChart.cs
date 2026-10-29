using System.Windows;
using ScottPlot;
using ScottPlot.WPF;

namespace Stockhog.Wpf.Controls.Charts;

/// <summary>
/// A distribution as adjacent bars, with an optional reference line, e.g. simulated drawdowns against the historical one.
/// </summary>
public sealed class HistogramChart : ChartBase
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(IReadOnlyList<HistogramBar>), typeof(HistogramChart), RedrawOnChange());

    public static readonly DependencyProperty ValueLabelProperty = DependencyProperty.Register(
        nameof(ValueLabel), typeof(string), typeof(HistogramChart), RedrawOnChange(string.Empty));

    public static readonly DependencyProperty CountLabelProperty = DependencyProperty.Register(
        nameof(CountLabel), typeof(string), typeof(HistogramChart), RedrawOnChange(string.Empty));

    public static readonly DependencyProperty ReferenceValueProperty = DependencyProperty.Register(
        nameof(ReferenceValue), typeof(double?), typeof(HistogramChart), RedrawOnChange());

    public static readonly DependencyProperty ReferenceLabelProperty = DependencyProperty.Register(
        nameof(ReferenceLabel), typeof(string), typeof(HistogramChart), RedrawOnChange(string.Empty));

    public static readonly DependencyProperty ShowPercentProperty = DependencyProperty.Register(
        nameof(ShowPercent), typeof(bool), typeof(HistogramChart), RedrawOnChange(false));

    private readonly WpfPlot _plot = CreatePlot();

    public HistogramChart()
    {
        Content = _plot;
    }

    public IReadOnlyList<HistogramBar>? Bars
    {
        get => (IReadOnlyList<HistogramBar>?)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public string ValueLabel
    {
        get => (string)GetValue(ValueLabelProperty);
        set => SetValue(ValueLabelProperty, value);
    }

    public string CountLabel
    {
        get => (string)GetValue(CountLabelProperty);
        set => SetValue(CountLabelProperty, value);
    }

    /// <summary>Position of a dashed vertical line, or <c>null</c> for none.</summary>
    public double? ReferenceValue
    {
        get => (double?)GetValue(ReferenceValueProperty);
        set => SetValue(ReferenceValueProperty, value);
    }

    public string ReferenceLabel
    {
        get => (string)GetValue(ReferenceLabelProperty);
        set => SetValue(ReferenceLabelProperty, value);
    }

    /// <summary>Format the value axis as percentages.</summary>
    public bool ShowPercent
    {
        get => (bool)GetValue(ShowPercentProperty);
        set => SetValue(ShowPercentProperty, value);
    }

    protected override void Render()
    {
        var plot = _plot.Plot;
        plot.Clear();

        // Trailing empty bins only stretch the axis.
        var bars = (Bars ?? []).ToList();
        var last = bars.FindLastIndex(b => b.Count > 0);
        bars = bars.Take(last + 1).ToList();

        if (bars.Count > 0)
        {
            var plotted = plot.Add.Bars(bars.Select(b => new Bar
            {
                Position = (b.From + b.To) / 2,
                Value = b.Count,
                Size = (b.To - b.From) * 0.95,
                FillColor = Accent.WithAlpha(0.75),
                LineWidth = 0
            }).ToArray());
            plotted.LegendText = string.Empty;
        }

        if (ReferenceValue is { } reference)
        {
            var line = plot.Add.VerticalLine(reference, 2, Negative, LinePattern.Dashed);
            line.LegendText = ReferenceLabel;
            if (!string.IsNullOrEmpty(ReferenceLabel))
                plot.ShowLegend(Alignment.UpperRight);
        }

        plot.Axes.Bottom.TickGenerator = ShowPercent
            ? new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = value => $"{value:0.#}%" }
            : new ScottPlot.TickGenerators.NumericAutomatic();
        plot.XLabel(ValueLabel);
        plot.YLabel(CountLabel);
        plot.Axes.AutoScale();
        plot.Axes.SetLimitsY(0, plot.Axes.GetLimits().Top);
        _plot.Refresh();
    }
}
