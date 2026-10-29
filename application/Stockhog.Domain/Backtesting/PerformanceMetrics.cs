using Stockhog.Domain.Optimization;

namespace Stockhog.Domain.Backtesting;

/// <summary>
/// Key figures of one finished run, calculated by the app from the run's equity curve and trades so every run is
/// measured the same way and runs can be ranked against each other.
/// </summary>
/// <param name="NetProfitPercent">e.g. 12.5 for +12.5%.</param>
/// <param name="MaxDrawdownPercent">Deepest fall from a peak, as a positive number, e.g. 8.2.</param>
/// <param name="SharpeRatio">Annualized, from daily returns.</param>
/// <param name="WinRatePercent">Share of closed trades with a profit, e.g. 55.</param>
public sealed record PerformanceMetrics(
    decimal NetProfitPercent,
    decimal MaxDrawdownPercent,
    decimal SharpeRatio,
    int TotalTrades,
    decimal WinRatePercent,
    decimal ProfitFactor)
{
    /// <summary>Drawdowns below this are treated as this, so a nearly flat equity curve does not get an extreme score.</summary>
    private const decimal MinimumDrawdownPercent = 1m;

    /// <summary>Higher is better for every objective.</summary>
    public decimal Score(OptimizationObjective objective) => objective switch
    {
        OptimizationObjective.SharpeRatio => SharpeRatio,
        OptimizationObjective.NetProfit => NetProfitPercent,
        OptimizationObjective.ReturnOverDrawdown => Math.Round(NetProfitPercent / Math.Max(MaxDrawdownPercent, MinimumDrawdownPercent), 4),
        _ => throw new ArgumentOutOfRangeException(nameof(objective), objective, null)
    };
}
