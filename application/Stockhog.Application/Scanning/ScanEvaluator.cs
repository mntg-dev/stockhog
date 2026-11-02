using Stockhog.Application.Analytics;
using Stockhog.Application.Backtesting;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.Scanning;

namespace Stockhog.Application.Scanning;

/// <summary>
/// Measures a finished backtest the way a scan judges it: the equity change in each half of the period, and the
/// drawdown the same trades could have produced in another order (Monte Carlo, 95th percentile).
/// </summary>
public static class ScanEvaluator
{
    public const int MonteCarloSimulations = 1000;

    /// <summary>Fixed, so the same run is always judged the same way.</summary>
    public const int MonteCarloSeed = 20261102;

    public static CandidateEvaluation Evaluate(
        BacktestReport report,
        PerformanceMetrics metrics,
        DateRange period,
        decimal initialCapital,
        decimal? buyAndHoldReturnPercent,
        ScanThresholds thresholds,
        CancellationToken cancellationToken = default)
    {
        var (firstHalf, secondHalf) = HalfReturns(report.Equity, period, initialCapital);
        var monteCarloDrawdown = MonteCarloDrawdown(report.ClosedTrades, metrics, initialCapital, cancellationToken);
        var rejections = thresholds.Assess(metrics, firstHalf, secondHalf, monteCarloDrawdown);

        return new CandidateEvaluation(metrics, firstHalf, secondHalf, monteCarloDrawdown, buyAndHoldReturnPercent, rejections);
    }

    /// <summary>Equity change from the start to the middle of the period, and from the middle to the end, in percent.</summary>
    public static (decimal FirstHalf, decimal SecondHalf) HalfReturns(IReadOnlyList<EquityPoint> equity, DateRange period, decimal initialCapital)
    {
        if (equity.Count == 0)
            return (0m, 0m);

        var middle = period.Start.AddDays(period.Days / 2).ToDateTime(TimeOnly.MinValue);
        var start = initialCapital > 0 ? initialCapital : equity[0].Value;
        var atMiddle = equity.LastOrDefault(p => p.Time <= middle) is { Value: > 0 } point ? point.Value : start;
        var end = equity[^1].Value;

        return (Change(start, atMiddle), Change(atMiddle, end));
    }

    /// <summary>
    /// The 95th percentile of the deepest drawdown over reshuffled trade sequences. With fewer than two trades there
    /// is nothing to reshuffle, so the run's own drawdown is used.
    /// </summary>
    public static decimal MonteCarloDrawdown(IReadOnlyList<ClosedTrade> trades, PerformanceMetrics metrics, decimal initialCapital, CancellationToken cancellationToken = default)
    {
        if (trades.Count < 2 || initialCapital <= 0)
            return metrics.MaxDrawdownPercent;

        var settings = new MonteCarloSettings(MonteCarloSimulations, MonteCarloMethod.Shuffle, MonteCarloSeed, SamplePaths: 0);
        var result = MonteCarloSimulator.Run((double)initialCapital, trades.Select(t => (double)t.ProfitLoss).ToList(), settings, cancellationToken);

        // Reshuffling can only make the historical order look better or worse; never report less than what happened.
        return Math.Max(Math.Round((decimal)result.MaxDrawdownPercent.P95, 2), metrics.MaxDrawdownPercent);
    }

    private static decimal Change(decimal from, decimal to) => from == 0 ? 0 : Math.Round((to - from) / from * 100m, 2);
}
