using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Domain.Optimization;

public enum OptimizationStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// A grid search: the same strategy, symbol and period backtested once per parameter combination (trial),
/// then the best trial picked by the objective.
/// <code>
/// Queued --Start--> Running --last trial finished--> Completed (at least one trial completed)
///   |                  |                         '--> Failed    (every trial failed)
///   |--Fail/Cancel-----'--Fail/Cancel-------------> Failed | Cancelled
/// </code>
/// Results of completed trials are kept in every final state, and the best of them is selected.
/// The aggregate is not thread-safe: callers running trials in parallel must serialize calls.
/// </summary>
public sealed class OptimizationRun : AggregateRoot<OptimizationRunId>
{
    public const int MaxParallelTrialsLimit = 8;

    /// <summary>Most trials a check on another period backtests again.</summary>
    public const int MaxCheckedTrials = 20;

    private readonly List<OptimizationTrial> _trials = [];

    private OptimizationRun(
        OptimizationRunId id,
        Symbol symbol,
        StrategyName strategy,
        DateRange period,
        Money initialCapital,
        ParameterGrid grid,
        OptimizationObjective objective,
        int maxParallelTrials,
        DateTimeOffset createdAt) : base(id)
    {
        Symbol = symbol;
        Strategy = strategy;
        Period = period;
        InitialCapital = initialCapital;
        Grid = grid;
        Objective = objective;
        MaxParallelTrials = maxParallelTrials;
        CreatedAt = createdAt;
        Status = OptimizationStatus.Queued;
    }

#pragma warning disable CS8618 // Persistence materializes runs through this constructor and then sets every property.
    private OptimizationRun() : base(default!)
    {
    }
#pragma warning restore CS8618

    public Symbol Symbol { get; }
    public StrategyName Strategy { get; }
    public DateRange Period { get; }
    public Money InitialCapital { get; }
    public ParameterGrid Grid { get; }
    public OptimizationObjective Objective { get; }

    /// <summary>How many trials (engine processes) run at the same time.</summary>
    public int MaxParallelTrials { get; }

    public OptimizationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public int? BestTrialIndex { get; private set; }

    /// <summary>In grid order; a trial's index is its position.</summary>
    public IReadOnlyList<OptimizationTrial> Trials => _trials;

    public OptimizationTrial? BestTrial => BestTrialIndex is { } index ? _trials[index] : null;

    public int FinishedTrials => _trials.Count(t => t.IsFinished);

    public bool IsFinished => Status is OptimizationStatus.Completed or OptimizationStatus.Failed or OptimizationStatus.Cancelled;

    public TimeSpan? Duration => StartedAt is { } started && FinishedAt is { } finished ? finished - started : null;

    private DateOnly? _checkStart;
    private DateOnly? _checkEnd;

    /// <summary>
    /// The period of the latest check: the best trials backtested again on days the optimization did not see.
    /// Parameters that only fit the noise of the optimization period usually do much worse here.
    /// </summary>
    public DateRange? CheckPeriod => _checkStart is { } start && _checkEnd is { } end ? DateRange.Create(start, end) : null;

    /// <summary>
    /// Begins a check of the <paramref name="top"/> best completed trials on <paramref name="period"/>, replacing any
    /// earlier check.
    /// </summary>
    /// <returns>Indexes of the trials to backtest, best first.</returns>
    public IReadOnlyList<int> StartCheck(DateRange period, int top)
    {
        if (!IsFinished || BestTrial is null)
            throw new DomainException($"Optimization {Id} has no finished trials to check yet.");
        if (top is < 1 or > MaxCheckedTrials)
            throw new DomainException($"Between 1 and {MaxCheckedTrials} trials can be checked.");
        if (period.Start <= Period.End && period.End >= Period.Start)
            throw new DomainException($"The check period {period} overlaps the optimization period {Period}; use days the optimization did not see.");

        var checkedTrials = _trials
            .Where(t => t.Metrics is not null)
            .OrderByDescending(t => t.Metrics!.Score(Objective))
            .ThenBy(t => t.Index)
            .Take(top)
            .Select(t => t.Index)
            .ToList();

        foreach (var trial in _trials)
            trial.ResetCheck(checkedTrials.Contains(trial.Index));

        _checkStart = period.Start;
        _checkEnd = period.End;
        return checkedTrials;
    }

    public void StartTrialCheck(int index) => Trial(index).StartCheck();

    public void CompleteTrialCheck(int index, BacktestRunId runId, PerformanceMetrics metrics) => Trial(index).CompleteCheck(runId, metrics);

    public void FailTrialCheck(int index, string reason) => Trial(index).FailCheck(reason);

    /// <summary>Marks the trials the check has not reached as cancelled; finished ones keep their results.</summary>
    public void CancelCheck()
    {
        foreach (var trial in _trials)
            trial.CancelCheck();
    }

    /// <param name="trialParameters">The full parameters of each trial, in grid order. Combinations the strategy
    /// rejects (e.g. a slow period not above the fast one) are left out by the caller.</param>
    public static OptimizationRun Create(
        OptimizationRunId id,
        Symbol symbol,
        StrategyName strategy,
        DateRange period,
        Money initialCapital,
        ParameterGrid grid,
        OptimizationObjective objective,
        int maxParallelTrials,
        IEnumerable<StrategyParameters> trialParameters,
        DateTimeOffset createdAt)
    {
        if (initialCapital.Amount <= 0)
            throw new DomainException("Initial capital must be positive.");
        if (maxParallelTrials is < 1 or > MaxParallelTrialsLimit)
            throw new DomainException($"Between 1 and {MaxParallelTrialsLimit} trials can run in parallel.");

        var run = new OptimizationRun(id, symbol, strategy, period, initialCapital, grid, objective, maxParallelTrials, createdAt);
        foreach (var parameters in trialParameters)
        {
            if (run._trials.Any(t => t.Parameters.Equals(parameters)))
                throw new DomainException($"Trial parameters {parameters} appear twice.");

            run._trials.Add(new OptimizationTrial(run._trials.Count, parameters));
        }

        if (run._trials.Count == 0)
            throw new DomainException("An optimization needs at least one trial.");
        if (run._trials.Count > grid.Combinations)
            throw new DomainException($"The grid has only {grid.Combinations} combinations, not {run._trials.Count}.");

        return run;
    }

    public void Start(DateTimeOffset at)
    {
        if (Status != OptimizationStatus.Queued)
            throw new DomainException($"Cannot start optimization {Id}: it is {Status}.");

        Status = OptimizationStatus.Running;
        StartedAt = at;
    }

    public void StartTrial(int index, BacktestRunId runId)
    {
        EnsureRunning("start a trial of");
        Trial(index).Start(runId);
    }

    public void CompleteTrial(int index, PerformanceMetrics metrics, DateTimeOffset at)
    {
        EnsureRunning("complete a trial of");
        Trial(index).Complete(metrics);
        FinishIfAllTrialsFinished(at);
    }

    public void FailTrial(int index, string reason, DateTimeOffset at)
    {
        EnsureRunning("fail a trial of");
        Trial(index).Fail(reason);
        FinishIfAllTrialsFinished(at);
    }

    public void Cancel(DateTimeOffset at)
    {
        EnsureNotFinished("cancel");
        CloseUnfinishedTrials(at);
        Status = OptimizationStatus.Cancelled;
        Raise(new OptimizationRunCancelled(Id, _trials.Count(t => t.Status is TrialStatus.Completed or TrialStatus.Failed), at));
    }

    public void Fail(string reason, DateTimeOffset at)
    {
        EnsureNotFinished("fail");
        CloseUnfinishedTrials(at);
        Status = OptimizationStatus.Failed;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? "Unknown error" : reason.Trim();
        Raise(new OptimizationRunFailed(Id, FailureReason, at));
    }

    private void FinishIfAllTrialsFinished(DateTimeOffset at)
    {
        if (_trials.Any(t => !t.IsFinished))
            return;

        FinishedAt = at;
        SelectBestTrial();
        if (BestTrialIndex is { } best)
        {
            Status = OptimizationStatus.Completed;
            Raise(new OptimizationRunCompleted(Id, best, at));
        }
        else
        {
            Status = OptimizationStatus.Failed;
            FailureReason = "Every trial failed.";
            Raise(new OptimizationRunFailed(Id, FailureReason, at));
        }
    }

    private void CloseUnfinishedTrials(DateTimeOffset at)
    {
        foreach (var trial in _trials)
            trial.Cancel();

        FinishedAt = at;
        SelectBestTrial();
    }

    /// <summary>Highest score wins; on a tie, the earlier trial.</summary>
    private void SelectBestTrial() =>
        BestTrialIndex = _trials
            .Where(t => t.Metrics is not null)
            .OrderByDescending(t => t.Metrics!.Score(Objective))
            .ThenBy(t => t.Index)
            .Select(t => (int?)t.Index)
            .FirstOrDefault();

    private OptimizationTrial Trial(int index) =>
        index >= 0 && index < _trials.Count ? _trials[index] : throw new DomainException($"Optimization {Id} has no trial {index}.");

    private void EnsureRunning(string action)
    {
        if (Status != OptimizationStatus.Running)
            throw new DomainException($"Cannot {action} optimization {Id}: it is {Status}.");
    }

    private void EnsureNotFinished(string action)
    {
        if (IsFinished)
            throw new DomainException($"Cannot {action} optimization {Id}: it is already {Status}.");
    }
}
