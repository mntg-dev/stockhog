namespace Stockhog.Infrastructure.Lean.Engine.Results;

// The subset of Lean's backtest result JSON (<RunId>.json) that the app reads.

internal sealed class LeanResultDocument
{
    public Dictionary<string, string>? Statistics { get; init; }
    public Dictionary<string, LeanChart>? Charts { get; init; }
    public Dictionary<string, LeanOrder>? Orders { get; init; }
    public LeanTotalPerformance? TotalPerformance { get; init; }
}

internal sealed class LeanChart
{
    public Dictionary<string, LeanSeries>? Series { get; init; }
}

internal sealed class LeanSeries
{
    /// <summary>
    /// <c>[time, value]</c> for line series, <c>[time, open, high, low, close]</c> for candlestick series. Time is in Unix seconds.
    /// </summary>
    public List<List<double?>>? Values { get; init; }
}

internal sealed class LeanOrder
{
    public int Id { get; init; }
    public string? Tag { get; init; }
}

internal sealed class LeanTotalPerformance
{
    public LeanTradeStatistics? TradeStatistics { get; init; }
    public List<LeanClosedTrade>? ClosedTrades { get; init; }
}

internal sealed class LeanTradeStatistics
{
    public int TotalNumberOfTrades { get; init; }
    public decimal ProfitFactor { get; init; }
    public int MaxConsecutiveWinningTrades { get; init; }
    public int MaxConsecutiveLosingTrades { get; init; }
}

internal sealed class LeanClosedTrade
{
    public List<LeanSymbol>? Symbols { get; init; }
    public DateTime EntryTime { get; init; }
    public decimal EntryPrice { get; init; }

    /// <summary>0 = long, 1 = short.</summary>
    public int Direction { get; init; }

    public decimal Quantity { get; init; }
    public DateTime ExitTime { get; init; }
    public decimal ExitPrice { get; init; }
    public decimal ProfitLoss { get; init; }
    public decimal TotalFees { get; init; }
    public List<int>? OrderIds { get; init; }
}

internal sealed class LeanSymbol
{
    public string? Value { get; init; }
}
