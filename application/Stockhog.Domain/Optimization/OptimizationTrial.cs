using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;

namespace Stockhog.Domain.Optimization;

public enum TrialStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// One point of the parameter grid: a backtest with a full set of parameters. Changed only through its
/// <see cref="OptimizationRun"/>; the id is the trial's position in the grid.
/// </summary>
public sealed class OptimizationTrial : Entity<int>
{
    internal OptimizationTrial(int index, StrategyParameters parameters) : base(index)
    {
        Parameters = parameters;
        Status = TrialStatus.Pending;
    }

#pragma warning disable CS8618 // Persistence materializes trials through this constructor and then sets every property.
    private OptimizationTrial() : base(0)
    {
    }
#pragma warning restore CS8618

    public int Index => Id;

    /// <summary>Every parameter of the run, fixed and varied, so the trial can be reproduced as a normal backtest.</summary>
    public StrategyParameters Parameters { get; }

    public TrialStatus Status { get; private set; }

    public BacktestRunId? RunId { get; private set; }

    public PerformanceMetrics? Metrics { get; private set; }

    public string? FailureReason { get; private set; }

    public bool IsFinished => Status is TrialStatus.Completed or TrialStatus.Failed or TrialStatus.Cancelled;

    /// <summary>The trial's state in the latest check on another period, or <c>null</c> if it is not part of it.</summary>
    public TrialStatus? CheckStatus { get; private set; }

    /// <summary>The backtest with these parameters on the check period.</summary>
    public BacktestRunId? CheckRunId { get; private set; }

    public PerformanceMetrics? CheckMetrics { get; private set; }

    public string? CheckFailureReason { get; private set; }

    internal void Start(BacktestRunId runId)
    {
        EnsureStatus(TrialStatus.Pending, "start");
        Status = TrialStatus.Running;
        RunId = runId;
    }

    internal void Complete(PerformanceMetrics metrics)
    {
        EnsureStatus(TrialStatus.Running, "complete");
        Status = TrialStatus.Completed;
        Metrics = metrics;
    }

    internal void Fail(string reason)
    {
        EnsureStatus(TrialStatus.Running, "fail");
        Status = TrialStatus.Failed;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? "Unknown error" : reason.Trim();
    }

    internal void Cancel()
    {
        if (!IsFinished)
            Status = TrialStatus.Cancelled;
    }

    /// <param name="include">Whether the trial takes part in the new check.</param>
    internal void ResetCheck(bool include)
    {
        CheckStatus = include ? TrialStatus.Pending : null;
        CheckRunId = null;
        CheckMetrics = null;
        CheckFailureReason = null;
    }

    internal void StartCheck()
    {
        EnsureCheckStatus(TrialStatus.Pending, "start the check of");
        CheckStatus = TrialStatus.Running;
    }

    internal void CompleteCheck(BacktestRunId runId, PerformanceMetrics metrics)
    {
        EnsureCheckStatus(TrialStatus.Running, "complete the check of");
        CheckStatus = TrialStatus.Completed;
        CheckRunId = runId;
        CheckMetrics = metrics;
    }

    internal void FailCheck(string reason)
    {
        EnsureCheckStatus(TrialStatus.Running, "fail the check of");
        CheckStatus = TrialStatus.Failed;
        CheckFailureReason = string.IsNullOrWhiteSpace(reason) ? "Unknown error" : reason.Trim();
    }

    internal void CancelCheck()
    {
        if (CheckStatus is TrialStatus.Pending or TrialStatus.Running)
            CheckStatus = TrialStatus.Cancelled;
    }

    private void EnsureCheckStatus(TrialStatus expected, string action)
    {
        if (CheckStatus != expected)
            throw new DomainException($"Cannot {action} trial {Index}: its check is {CheckStatus?.ToString() ?? "not started"}.");
    }

    private void EnsureStatus(TrialStatus expected, string action)
    {
        if (Status != expected)
            throw new DomainException($"Cannot {action} trial {Index}: it is {Status}.");
    }
}
