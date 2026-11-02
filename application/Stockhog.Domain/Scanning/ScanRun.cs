using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Domain.Scanning;

public enum ScanStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum CandidateStatus
{
    Pending,
    Running,
    Evaluated,
    Failed,
    Cancelled
}

/// <summary>One (symbol, strategy) pair of a scan. Changed only through its <see cref="ScanRun"/>; the id is its position.</summary>
public sealed class ScanCandidate : Entity<int>
{
    internal ScanCandidate(int index, Symbol symbol, StrategyName strategy) : base(index)
    {
        Symbol = symbol;
        Strategy = strategy;
        Status = CandidateStatus.Pending;
    }

#pragma warning disable CS8618 // Persistence materializes candidates through this constructor and then sets every property.
    private ScanCandidate() : base(0)
    {
    }
#pragma warning restore CS8618

    public int Index => Id;
    public Symbol Symbol { get; }
    public StrategyName Strategy { get; }
    public CandidateStatus Status { get; private set; }
    public BacktestRunId? RunId { get; private set; }
    public CandidateEvaluation? Evaluation { get; private set; }
    public string? FailureReason { get; private set; }

    public bool IsFinished => Status is CandidateStatus.Evaluated or CandidateStatus.Failed or CandidateStatus.Cancelled;

    public bool Passed => Evaluation?.Passed == true;

    internal void Start()
    {
        EnsureStatus(CandidateStatus.Pending, "start");
        Status = CandidateStatus.Running;
    }

    /// <param name="runId">The backtest the evaluation is based on.</param>
    internal void Complete(BacktestRunId runId, CandidateEvaluation evaluation)
    {
        EnsureStatus(CandidateStatus.Running, "complete");
        Status = CandidateStatus.Evaluated;
        RunId = runId;
        Evaluation = evaluation;
    }

    /// <summary>A candidate can fail before it starts, e.g. when its symbol has no data.</summary>
    internal void Fail(string reason)
    {
        if (IsFinished)
            throw new DomainException($"Candidate {Index} is already {Status}.");
        Status = CandidateStatus.Failed;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? "Unknown error" : reason.Trim();
    }

    internal void Cancel()
    {
        if (!IsFinished)
            Status = CandidateStatus.Cancelled;
    }

    private void EnsureStatus(CandidateStatus expected, string action)
    {
        if (Status != expected)
            throw new DomainException($"Cannot {action} candidate {Index}: it is {Status}.");
    }
}

/// <summary>
/// Tests every chosen strategy on every chosen symbol over one period, with each strategy's default parameters, and
/// checks which pairs hold up: enough trades, profitable in both halves, a tolerable Monte Carlo drawdown.
/// <code>
/// Queued --Start--> Running --last candidate finished--> Completed (at least one evaluated)
///   |                  |                              '--> Failed    (none evaluated)
///   |--Fail/Cancel-----'--Fail/Cancel------------------> Failed | Cancelled
/// </code>
/// Not thread-safe: callers evaluating candidates in parallel must serialize calls.
/// </summary>
public sealed class ScanRun : AggregateRoot<ScanRunId>
{
    public const int MaxSymbols = 100;
    public const int MaxStrategies = 10;
    public const int MaxParallelLimit = 8;

    private readonly List<ScanCandidate> _candidates = [];

    private ScanRun(
        ScanRunId id,
        DateRange period,
        Money initialCapital,
        ScanThresholds thresholds,
        int maxParallel,
        Guid? screenerRunId,
        DateTimeOffset createdAt) : base(id)
    {
        Period = period;
        InitialCapital = initialCapital;
        Thresholds = thresholds;
        MaxParallel = maxParallel;
        ScreenerRunId = screenerRunId;
        CreatedAt = createdAt;
        Status = ScanStatus.Queued;
    }

#pragma warning disable CS8618 // Persistence materializes runs through this constructor and then sets every property.
    private ScanRun() : base(default!)
    {
    }
#pragma warning restore CS8618

    public DateRange Period { get; }
    public Money InitialCapital { get; }
    public ScanThresholds Thresholds { get; }

    /// <summary>How many backtests (engine processes) run at the same time.</summary>
    public int MaxParallel { get; }

    /// <summary>The screener run the symbols came from, if any.</summary>
    public Guid? ScreenerRunId { get; }

    public ScanStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? FailureReason { get; private set; }

    /// <summary>Symbol by symbol, strategies in the order given.</summary>
    public IReadOnlyList<ScanCandidate> Candidates => _candidates;

    public IReadOnlyList<Symbol> Symbols => _candidates.Select(c => c.Symbol).Distinct().ToList();

    public IReadOnlyList<StrategyName> Strategies => _candidates.Select(c => c.Strategy).Distinct().ToList();

    public int FinishedCandidates => _candidates.Count(c => c.IsFinished);

    public int PassedCandidates => _candidates.Count(c => c.Passed);

    public bool IsFinished => Status is ScanStatus.Completed or ScanStatus.Failed or ScanStatus.Cancelled;

    public static ScanRun Create(
        ScanRunId id,
        IReadOnlyList<Symbol> symbols,
        IReadOnlyList<StrategyName> strategies,
        DateRange period,
        Money initialCapital,
        ScanThresholds thresholds,
        int maxParallel,
        Guid? screenerRunId,
        DateTimeOffset createdAt)
    {
        var distinctSymbols = symbols.Distinct().ToList();
        var distinctStrategies = strategies.Distinct().ToList();
        if (distinctSymbols.Count is < 1 or > MaxSymbols)
            throw new DomainException($"A scan needs between 1 and {MaxSymbols} symbols.");
        if (distinctStrategies.Count is < 1 or > MaxStrategies)
            throw new DomainException($"A scan needs between 1 and {MaxStrategies} strategies.");
        if (initialCapital.Amount <= 0)
            throw new DomainException("Initial capital must be positive.");
        if (maxParallel is < 1 or > MaxParallelLimit)
            throw new DomainException($"Between 1 and {MaxParallelLimit} backtests can run in parallel.");

        var run = new ScanRun(id, period, initialCapital, thresholds, maxParallel, screenerRunId, createdAt);
        foreach (var symbol in distinctSymbols)
        foreach (var strategy in distinctStrategies)
            run._candidates.Add(new ScanCandidate(run._candidates.Count, symbol, strategy));

        return run;
    }

    public void Start(DateTimeOffset at)
    {
        if (Status != ScanStatus.Queued)
            throw new DomainException($"Cannot start scan {Id}: it is {Status}.");

        Status = ScanStatus.Running;
        StartedAt = at;
    }

    public void StartCandidate(int index)
    {
        EnsureRunning("start a candidate of");
        Candidate(index).Start();
    }

    public void CompleteCandidate(int index, BacktestRunId runId, CandidateEvaluation evaluation, DateTimeOffset at)
    {
        EnsureRunning("complete a candidate of");
        Candidate(index).Complete(runId, evaluation);
        FinishIfAllCandidatesFinished(at);
    }

    public void FailCandidate(int index, string reason, DateTimeOffset at)
    {
        EnsureRunning("fail a candidate of");
        Candidate(index).Fail(reason);
        FinishIfAllCandidatesFinished(at);
    }

    public void Cancel(DateTimeOffset at)
    {
        EnsureNotFinished("cancel");
        foreach (var candidate in _candidates)
            candidate.Cancel();
        Finish(ScanStatus.Cancelled, null, at);
    }

    public void Fail(string reason, DateTimeOffset at)
    {
        EnsureNotFinished("fail");
        foreach (var candidate in _candidates)
            candidate.Cancel();
        Finish(ScanStatus.Failed, string.IsNullOrWhiteSpace(reason) ? "Unknown error" : reason.Trim(), at);
    }

    private void FinishIfAllCandidatesFinished(DateTimeOffset at)
    {
        if (_candidates.Any(c => !c.IsFinished))
            return;

        if (_candidates.Any(c => c.Status == CandidateStatus.Evaluated))
            Finish(ScanStatus.Completed, null, at);
        else
            Finish(ScanStatus.Failed, "No candidate could be evaluated.", at);
    }

    private void Finish(ScanStatus status, string? reason, DateTimeOffset at)
    {
        Status = status;
        FailureReason = reason;
        FinishedAt = at;
        Raise(new ScanRunFinished(Id, status, PassedCandidates, at));
    }

    private ScanCandidate Candidate(int index) =>
        index >= 0 && index < _candidates.Count ? _candidates[index] : throw new DomainException($"Scan {Id} has no candidate {index}.");

    private void EnsureRunning(string action)
    {
        if (Status != ScanStatus.Running)
            throw new DomainException($"Cannot {action} scan {Id}: it is {Status}.");
    }

    private void EnsureNotFinished(string action)
    {
        if (IsFinished)
            throw new DomainException($"Cannot {action} scan {Id}: it is already {Status}.");
    }
}

public sealed record ScanRunFinished(ScanRunId ScanId, ScanStatus Status, int PassedCandidates, DateTimeOffset OccurredAt) : IDomainEvent;
