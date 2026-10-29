using Stockhog.Application.Backtesting;
using Stockhog.Domain.Backtesting;

namespace Stockhog.Application.Analytics;

/// <summary>
/// Key figures calculated from a report's equity curve and trades. The engine's own statistics are formatted text
/// whose set depends on the run; these are numbers computed the same way for every run, so runs can be ranked.
/// </summary>
public static class PerformanceCalculator
{
    private const double TradingDaysPerYear = 252;

    public static PerformanceMetrics Calculate(BacktestReport report)
    {
        var equity = report.Equity;
        var netProfit = equity.Count >= 2 && equity[0].Value != 0 ? (equity[^1].Value / equity[0].Value - 1) * 100 : 0;
        var trades = report.ClosedTrades;
        var winRate = trades.Count == 0 ? 0 : trades.Count(t => t.ProfitLoss > 0) * 100m / trades.Count;

        return new PerformanceMetrics(
            Math.Round(netProfit, 4),
            Math.Round(MaxDrawdownPercent(equity), 4),
            Math.Round(SharpeRatio(equity), 4),
            trades.Count,
            Math.Round(winRate, 2),
            report.TradeStatistics.ProfitFactor);
    }

    /// <returns>The deepest fall from a running peak, as a positive percentage.</returns>
    public static decimal MaxDrawdownPercent(IReadOnlyList<EquityPoint> equity)
    {
        decimal peak = 0, worst = 0;
        foreach (var point in equity)
        {
            if (point.Value > peak)
                peak = point.Value;
            else if (peak > 0)
                worst = Math.Max(worst, (peak - point.Value) / peak);
        }

        return worst * 100;
    }

    /// <summary>
    /// Annualized Sharpe ratio of daily returns, with a zero risk-free rate. Each day is represented by its last
    /// equity point, so intraday sampling does not change the result.
    /// </summary>
    public static decimal SharpeRatio(IReadOnlyList<EquityPoint> equity)
    {
        var closes = DailyCloses(equity);
        if (closes.Count < 3)
            return 0;

        var returns = new double[closes.Count - 1];
        for (var i = 1; i < closes.Count; i++)
            returns[i - 1] = closes[i - 1] == 0 ? 0 : closes[i] / closes[i - 1] - 1;

        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Length - 1);
        var deviation = Math.Sqrt(variance);

        return deviation < 1e-12 ? 0 : (decimal)(mean / deviation * Math.Sqrt(TradingDaysPerYear));
    }

    /// <summary>
    /// Every fall from a peak until equity regains that peak, deepest first. The last period may still be open.
    /// </summary>
    public static IReadOnlyList<DrawdownPeriod> DrawdownPeriods(IReadOnlyList<EquityPoint> equity, int top)
    {
        var periods = new List<DrawdownPeriod>();
        if (equity.Count == 0)
            return periods;

        var peak = equity[0];
        EquityPoint? trough = null;

        foreach (var point in equity.Skip(1))
        {
            if (point.Value >= peak.Value)
            {
                if (trough is { } closed)
                    periods.Add(DrawdownPeriod.Create(peak, closed, point.Time));

                peak = point;
                trough = null;
            }
            else if (trough is null || point.Value < trough.Value.Value)
            {
                trough = point;
            }
        }

        if (trough is { } open)
            periods.Add(DrawdownPeriod.Create(peak, open, recoveredAt: null));

        return periods.OrderByDescending(p => p.DepthPercent).ThenBy(p => p.PeakTime).Take(top).ToList();
    }

    private static List<double> DailyCloses(IReadOnlyList<EquityPoint> equity)
    {
        var closes = new List<double>();
        DateTime? day = null;
        foreach (var point in equity)
        {
            if (point.Time.Date == day)
            {
                closes[^1] = (double)point.Value;
            }
            else
            {
                closes.Add((double)point.Value);
                day = point.Time.Date;
            }
        }

        return closes;
    }
}

/// <param name="RecoveredAt">When equity first regained the peak, or <c>null</c> if it has not yet.</param>
public sealed record DrawdownPeriod(
    DateTime PeakTime,
    decimal PeakValue,
    DateTime TroughTime,
    decimal TroughValue,
    DateTime? RecoveredAt,
    decimal DepthPercent)
{
    public TimeSpan Decline => TroughTime - PeakTime;

    public TimeSpan? Recovery => RecoveredAt - TroughTime;

    public bool IsRecovered => RecoveredAt is not null;

    internal static DrawdownPeriod Create(EquityPoint peak, EquityPoint trough, DateTime? recoveredAt) =>
        new(peak.Time, peak.Value, trough.Time, trough.Value, recoveredAt,
            peak.Value == 0 ? 0 : Math.Round((peak.Value - trough.Value) / peak.Value * 100, 4));
}
