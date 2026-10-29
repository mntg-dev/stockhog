using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Domain.Backtesting;

namespace Stockhog.Application.Backtesting.RunBacktest;

/// <param name="Parameters">Strategy inputs by name (see <see cref="Strategies.StrategyDescriptor"/>); missing ones use their defaults.</param>
public sealed record RunBacktestCommand(
    string StrategyName,
    string Symbol,
    DateOnly Start,
    DateOnly End,
    decimal InitialCapital,
    IReadOnlyDictionary<string, string> Parameters) : ICommand<BacktestRunResult>
{
    /// <summary>Optional live progress for the caller; not part of the command's data.</summary>
    public IProgress<BacktestProgress>? Progress { get; init; }

    /// <summary>Set when the run is a trial of an optimization: links the run to it and gives it a unique, derived id.</summary>
    public OptimizationTrialReference? Trial { get; init; }
}

public sealed record OptimizationTrialReference(string OptimizationId, int Index);

/// <summary>
/// Identifies a completed run; pass it to <c>GetBacktestReportQuery</c> to load the report.
/// </summary>
/// <param name="Metrics">The run's key figures, or <c>null</c> if its result could not be read.</param>
public sealed record BacktestRunResult(string RunId, string Symbol, string StrategyName, PerformanceMetrics? Metrics = null);
