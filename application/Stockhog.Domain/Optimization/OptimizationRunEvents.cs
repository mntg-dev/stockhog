using Stockhog.Domain.Common;

namespace Stockhog.Domain.Optimization;

/// <param name="BestTrialIndex">The best completed trial by the run's objective.</param>
public sealed record OptimizationRunCompleted(OptimizationRunId OptimizationId, int BestTrialIndex, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OptimizationRunFailed(OptimizationRunId OptimizationId, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OptimizationRunCancelled(OptimizationRunId OptimizationId, int FinishedTrials, DateTimeOffset OccurredAt) : IDomainEvent;
