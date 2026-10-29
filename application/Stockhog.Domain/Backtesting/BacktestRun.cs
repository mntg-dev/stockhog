using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Optimization;

namespace Stockhog.Domain.Backtesting;

/// <summary>
/// One execution of a strategy over a historical period.
/// <code>
/// Queued --Start--> Running --Complete--> Completed
///   |                  |------Fail------> Failed
///   |--Fail/Cancel-----'-----Cancel-----> Cancelled
/// </code>
/// Completed, Failed and Cancelled are final.
/// </summary>
public sealed class BacktestRun : AggregateRoot<BacktestRunId>
{
    private BacktestRun(
        BacktestRunId id,
        Symbol symbol,
        StrategyName strategy,
        DateRange period,
        Money initialCapital,
        StrategyParameters parameters,
        DateTimeOffset createdAt,
        OptimizationRunId? optimizationId) : base(id)
    {
        OptimizationId = optimizationId;
        Symbol = symbol;
        Strategy = strategy;
        Period = period;
        InitialCapital = initialCapital;
        Parameters = parameters;
        CreatedAt = createdAt;
        Status = BacktestStatus.Queued;
    }

#pragma warning disable CS8618 // Persistence materializes runs through this constructor and then sets every property.
    private BacktestRun() : base(default!)
    {
    }
#pragma warning restore CS8618

    public Symbol Symbol { get; }
    public StrategyName Strategy { get; }
    public DateRange Period { get; }
    public Money InitialCapital { get; }
    public StrategyParameters Parameters { get; }

    /// <summary>The optimization this run is a trial of, or <c>null</c> for a run started on its own.</summary>
    public OptimizationRunId? OptimizationId { get; }

    public BacktestStatus Status { get; private set; }

    /// <summary>0 to 100; never decreases.</summary>
    public int Progress { get; private set; }

    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? FailureReason { get; private set; }

    /// <summary>Key figures of a completed run, or <c>null</c> while it runs, if it did not complete, or if its
    /// result could not be read.</summary>
    public PerformanceMetrics? Metrics { get; private set; }

    public bool IsFinished => Status is BacktestStatus.Completed or BacktestStatus.Failed or BacktestStatus.Cancelled;

    public TimeSpan? Duration => StartedAt is { } started && FinishedAt is { } finished ? finished - started : null;

    public static BacktestRun Create(
        BacktestRunId id,
        Symbol symbol,
        StrategyName strategy,
        DateRange period,
        Money initialCapital,
        StrategyParameters parameters,
        DateTimeOffset createdAt,
        OptimizationRunId? optimizationId = null)
    {
        if (initialCapital.Amount <= 0)
            throw new DomainException("Initial capital must be positive.");

        return new BacktestRun(id, symbol, strategy, period, initialCapital, parameters, createdAt, optimizationId);
    }

    public void Start(DateTimeOffset at)
    {
        EnsureStatus(BacktestStatus.Queued, "start");
        Status = BacktestStatus.Running;
        StartedAt = at;
    }

    /// <summary>
    /// Ignored unless the run is in progress, because the engine may still report progress while a cancellation is processed.
    /// </summary>
    public void ReportProgress(int percent)
    {
        if (Status != BacktestStatus.Running)
            return;

        Progress = Math.Max(Progress, Math.Clamp(percent, 0, 100));
    }

    public void Complete(DateTimeOffset at, PerformanceMetrics? metrics = null)
    {
        EnsureStatus(BacktestStatus.Running, "complete");
        Status = BacktestStatus.Completed;
        Progress = 100;
        FinishedAt = at;
        Metrics = metrics;
        Raise(new BacktestRunCompleted(Id, Symbol, Strategy, at));
    }

    /// <summary>Adds the key figures to a run completed before they were stored (runs from older versions).</summary>
    public void RecordMetrics(PerformanceMetrics metrics)
    {
        EnsureStatus(BacktestStatus.Completed, "record metrics of");
        Metrics = metrics;
    }

    public void Fail(string reason, DateTimeOffset at)
    {
        EnsureNotFinished("fail");
        Status = BacktestStatus.Failed;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? "Unknown error" : reason.Trim();
        FinishedAt = at;
        Raise(new BacktestRunFailed(Id, FailureReason, at));
    }

    public void Cancel(DateTimeOffset at)
    {
        EnsureNotFinished("cancel");
        Status = BacktestStatus.Cancelled;
        FinishedAt = at;
        Raise(new BacktestRunCancelled(Id, Progress, at));
    }

    private void EnsureStatus(BacktestStatus expected, string action)
    {
        if (Status != expected)
            throw new DomainException($"Cannot {action} backtest {Id}: it is {Status}.");
    }

    private void EnsureNotFinished(string action)
    {
        if (IsFinished)
            throw new DomainException($"Cannot {action} backtest {Id}: it is already {Status}.");
    }
}
