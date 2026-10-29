using System.ComponentModel;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Backtesting;

/// <summary>
/// Read model of a finished run. Marked immutable so HybridCache hands out the cached instance instead of a deserialized copy.
/// </summary>
/// <param name="Statistics">Engine statistics as formatted by the engine, e.g. <c>"Sharpe Ratio" = "0.896"</c>.</param>
[ImmutableObject(true)]
public sealed record BacktestReport(
    string RunId,
    IReadOnlyDictionary<string, string> Statistics,
    TradeStatistics TradeStatistics,
    IReadOnlyList<EquityPoint> Equity,
    IReadOnlyList<ClosedTrade> ClosedTrades);

public sealed record TradeStatistics(
    int TotalTrades,
    decimal ProfitFactor,
    int MaxConsecutiveWins,
    int MaxConsecutiveLosses);

public readonly record struct EquityPoint(DateTime Time, decimal Value);

/// <param name="ExitReason">Tag of the closing order, e.g. <c>"Trailing Stop Long"</c>.</param>
public sealed record ClosedTrade(
    int Number,
    string Symbol,
    TradeDirection Direction,
    decimal Quantity,
    DateTime EntryTime,
    decimal EntryPrice,
    DateTime ExitTime,
    decimal ExitPrice,
    string ExitReason,
    decimal ProfitLoss,
    decimal Fees)
{
    public decimal ProfitLossPercent =>
        EntryPrice == 0 || Quantity == 0 ? 0 : ProfitLoss / Math.Abs(EntryPrice * Quantity);
}
