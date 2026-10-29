namespace Stockhog.Wpf.Controls.Charts;

public readonly record struct TimeValue(DateTime Time, double Value);

/// <param name="Span">Time covered by the bar, e.g. one day.</param>
public readonly record struct PriceBar(DateTime Time, TimeSpan Span, double Open, double High, double Low, double Close);

/// <summary>One round trip drawn on a price chart: entry marker, exit marker and a line between them.</summary>
public readonly record struct TradeMarker(DateTime EntryTime, double EntryPrice, DateTime ExitTime, double ExitPrice, bool IsLong, bool IsWin);

public sealed record ChartSeries(string Label, IReadOnlyList<TimeValue> Points);

/// <summary>A grid of values coloured by size, e.g. optimization scores over two parameters.</summary>
/// <param name="Values"><c>[row, column]</c>; row 0 is drawn at the bottom. <c>null</c> cells are left empty.</param>
/// <param name="Highlight">Cell to outline, as (column, row).</param>
/// <param name="ValueFormat">Format of the value printed in each cell.</param>
public sealed record HeatmapData(
    string XLabel,
    string YLabel,
    IReadOnlyList<string> XTicks,
    IReadOnlyList<string> YTicks,
    double?[,] Values,
    (int Column, int Row)? Highlight = null,
    string ValueFormat = "0.##");

public readonly record struct HistogramBar(double From, double To, double Count);

/// <summary>Values over a numeric x axis (0, 1, 2, …), e.g. equity after each trade.</summary>
/// <param name="IsHighlighted">Drawn on top, thicker and in the accent colour, and shown in the legend.</param>
public sealed record XYSeries(string Label, IReadOnlyList<double> Values, bool IsHighlighted = false);
