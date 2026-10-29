using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;

namespace Stockhog.Application.Backtesting;

/// <summary>
/// Read side for backtest history. Returns flat read models straight from storage without loading aggregates.
/// </summary>
public interface IBacktestHistoryReader
{
    /// <param name="symbol">Only runs for this symbol, or every run when <c>null</c>.</param>
    /// <param name="includeTrials">Include runs that are trials of an optimization.</param>
    /// <returns>Newest first.</returns>
    Task<IReadOnlyList<BacktestRunSummary>> ListAsync(Symbol? symbol, int take, bool includeTrials = false, CancellationToken cancellationToken = default);

    /// <summary>Completed runs started on their own (not optimization trials) that have metrics, newest first.</summary>
    Task<IReadOnlyList<RankedRun>> ListRankedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A completed run with its metrics flattened into numbers, so a table can sort by any of them.
/// </summary>
/// <param name="ReturnOverDrawdown">Net profit per unit of maximum drawdown; see <see cref="Domain.Optimization.OptimizationObjective.ReturnOverDrawdown"/>.</param>
public sealed record RankedRun(
    string RunId,
    string Symbol,
    string StrategyName,
    DateOnly Start,
    DateOnly End,
    decimal InitialCapital,
    DateTimeOffset CreatedAt,
    decimal NetProfitPercent,
    decimal MaxDrawdownPercent,
    decimal ReturnOverDrawdown,
    decimal SharpeRatio,
    decimal WinRatePercent,
    decimal ProfitFactor,
    int TotalTrades)
{
    public static RankedRun Create(
        string runId, string symbol, string strategyName, DateOnly start, DateOnly end, decimal initialCapital,
        DateTimeOffset createdAt, PerformanceMetrics metrics) => new(
        runId, symbol, strategyName, start, end, initialCapital, createdAt,
        metrics.NetProfitPercent,
        metrics.MaxDrawdownPercent,
        metrics.Score(Domain.Optimization.OptimizationObjective.ReturnOverDrawdown),
        metrics.SharpeRatio,
        metrics.WinRatePercent,
        metrics.ProfitFactor,
        metrics.TotalTrades);

    public BacktestRunSummary ToSummary() =>
        new(RunId, Symbol, StrategyName, Start, End, InitialCapital, BacktestStatus.Completed, 100, CreatedAt, null, null);
}

public sealed record BacktestRunSummary(
    string RunId,
    string Symbol,
    string StrategyName,
    DateOnly Start,
    DateOnly End,
    decimal InitialCapital,
    BacktestStatus Status,
    int Progress,
    DateTimeOffset CreatedAt,
    TimeSpan? Duration,
    string? FailureReason,
    string? OptimizationId = null)
{
    public bool HasReport => Status == BacktestStatus.Completed;
}
