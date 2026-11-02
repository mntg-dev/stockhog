namespace Stockhog.Application.Screening;

/// <summary>
/// Pure screening maths: measures a symbol from its daily bars, then filters and ranks the measured symbols.
/// </summary>
public static class ScreenerCalculator
{
    public const int AverageWindow = 20;
    public const int AtrWindow = 14;
    public const int OneMonthSessions = 21;
    public const int SixMonthSessions = 126;
    public const int OneYearSessions = 252;

    /// <summary>A symbol whose last bar is this much older than the newest bar in the run is treated as halted or delisted.</summary>
    public const int MaxStaleDays = 7;

    /// <param name="bars">Oldest first.</param>
    /// <returns><c>null</c> when there are too few bars to compute the averages.</returns>
    public static SymbolMetrics? Measure(string symbol, IReadOnlyList<DailyBar> bars)
    {
        if (bars.Count < AverageWindow + 1)
            return null;

        var last = bars[^1];
        if (last.Close <= 0)
            return null;

        var dollarVolume = 0m;
        for (var i = bars.Count - AverageWindow; i < bars.Count; i++)
            dollarVolume += bars[i].Close * bars[i].Volume;

        // True range needs the previous close, so the first bar of the window looks one bar back.
        var trueRange = 0m;
        for (var i = bars.Count - AtrWindow; i < bars.Count; i++)
        {
            var previousClose = bars[i - 1].Close;
            trueRange += Math.Max(bars[i].High - bars[i].Low, Math.Max(Math.Abs(bars[i].High - previousClose), Math.Abs(bars[i].Low - previousClose)));
        }

        var yearHigh = 0m;
        for (var i = Math.Max(0, bars.Count - OneYearSessions); i < bars.Count; i++)
            yearHigh = Math.Max(yearHigh, bars[i].High);

        return new SymbolMetrics(
            symbol,
            last.Date,
            last.Close,
            Math.Round(dollarVolume / AverageWindow, 0),
            Percent(trueRange / AtrWindow, last.Close),
            ReturnOver(bars, OneMonthSessions),
            ReturnOver(bars, SixMonthSessions),
            yearHigh > 0 ? Percent(last.Close - yearHigh, yearHigh) : 0m,
            bars.Count);
    }

    /// <param name="newestDate">The latest session any symbol had; older last bars count as stale.</param>
    public static bool Matches(SymbolMetrics metrics, ScreenerCriteria criteria, DateOnly newestDate) =>
        newestDate.DayNumber - metrics.LastDate.DayNumber <= MaxStaleDays
        && metrics.TradingDays >= criteria.MinTradingDays
        && (criteria.MinPrice is not { } minPrice || metrics.LastClose >= minPrice)
        && (criteria.MaxPrice is not { } maxPrice || metrics.LastClose <= maxPrice)
        && metrics.AverageDollarVolume >= criteria.MinAverageDollarVolume
        && (criteria.MinAtrPercent is not { } minAtr || metrics.AtrPercent >= minAtr)
        && (criteria.MaxAtrPercent is not { } maxAtr || metrics.AtrPercent <= maxAtr);

    /// <summary>Best first by the criteria's sort; symbols without a value for it (short history) go last.</summary>
    public static IEnumerable<SymbolMetrics> Rank(IEnumerable<SymbolMetrics> metrics, ScreenerSort sortBy) =>
        metrics
            .OrderByDescending(m => SortValue(m, sortBy) is not null)
            .ThenByDescending(m => SortValue(m, sortBy))
            .ThenBy(m => m.Symbol, StringComparer.Ordinal);

    private static decimal? SortValue(SymbolMetrics metrics, ScreenerSort sortBy) => sortBy switch
    {
        ScreenerSort.DollarVolume => metrics.AverageDollarVolume,
        ScreenerSort.Return6M => metrics.Return6MPercent,
        ScreenerSort.Return1M => metrics.Return1MPercent,
        ScreenerSort.AtrPercent => metrics.AtrPercent,
        _ => throw new ArgumentOutOfRangeException(nameof(sortBy), sortBy, null)
    };

    private static decimal? ReturnOver(IReadOnlyList<DailyBar> bars, int sessions)
    {
        if (bars.Count <= sessions)
            return null;

        var start = bars[^(sessions + 1)].Close;
        return start > 0 ? Percent(bars[^1].Close - start, start) : null;
    }

    private static decimal Percent(decimal part, decimal whole) => Math.Round(part / whole * 100m, 2);
}
