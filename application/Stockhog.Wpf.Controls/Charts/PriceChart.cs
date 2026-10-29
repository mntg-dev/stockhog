using System.Windows;
using ScottPlot;
using ScottPlot.WPF;

namespace Stockhog.Wpf.Controls.Charts;

/// <summary>
/// Candlesticks with trades on top: an entry triangle (up for long, down for short), an exit circle
/// coloured by outcome, and a dashed line connecting them.
/// </summary>
public sealed class PriceChart : ChartBase
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(IReadOnlyList<PriceBar>), typeof(PriceChart), RedrawOnChange());

    public static readonly DependencyProperty TradesProperty = DependencyProperty.Register(
        nameof(Trades), typeof(IReadOnlyList<TradeMarker>), typeof(PriceChart), RedrawOnChange());

    public static readonly DependencyProperty PriceLabelProperty = DependencyProperty.Register(
        nameof(PriceLabel), typeof(string), typeof(PriceChart), RedrawOnChange("Price"));

    private readonly WpfPlot _plot = CreatePlot();

    public PriceChart()
    {
        Content = _plot;
    }

    public IReadOnlyList<PriceBar>? Bars
    {
        get => (IReadOnlyList<PriceBar>?)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public IReadOnlyList<TradeMarker>? Trades
    {
        get => (IReadOnlyList<TradeMarker>?)GetValue(TradesProperty);
        set => SetValue(TradesProperty, value);
    }

    public string PriceLabel
    {
        get => (string)GetValue(PriceLabelProperty);
        set => SetValue(PriceLabelProperty, value);
    }

    protected override void Render()
    {
        var plot = _plot.Plot;
        plot.Clear();

        var bars = Bars ?? [];
        if (bars.Count > 0)
            plot.Add.Candlestick(bars.Select(b => new OHLC(b.Open, b.High, b.Low, b.Close, b.Time, b.Span)).ToList());

        foreach (var trade in Trades ?? [])
        {
            var entryX = trade.EntryTime.ToOADate();
            var exitX = trade.ExitTime.ToOADate();
            var outcome = trade.IsWin ? Positive : Negative;

            var link = plot.Add.Line(entryX, trade.EntryPrice, exitX, trade.ExitPrice);
            link.LineColor = outcome.WithAlpha(0.6);
            link.LinePattern = LinePattern.Dashed;
            link.LineWidth = 1;

            plot.Add.Marker(entryX, trade.EntryPrice, trade.IsLong ? MarkerShape.FilledTriangleUp : MarkerShape.FilledTriangleDown, 9, Accent);
            plot.Add.Marker(exitX, trade.ExitPrice, MarkerShape.FilledCircle, 7, outcome);
        }

        plot.Axes.DateTimeTicksBottom();
        plot.YLabel(PriceLabel);
        plot.Axes.AutoScale();
        if (bars.Count == 0 && Trades is not { Count: > 0 })
            ShowRecentDates(plot);

        _plot.Refresh();
    }
}
