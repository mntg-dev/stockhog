using Stockhog.Application.Abstractions.Results;
using Stockhog.Domain.Optimization;

namespace Stockhog.Application.Optimization;

/// <summary>Write side: loads optimizations for changes, which the unit of work saves.</summary>
public interface IOptimizationRunRepository
{
    void Add(OptimizationRun run);

    Task<OptimizationRun?> GetAsync(OptimizationRunId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OptimizationRun>> ListUnfinishedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Read side: untracked optimizations with their trials, for building read models. Changes to them are never saved.
/// </summary>
public interface IOptimizationReader
{
    /// <returns>Newest first.</returns>
    Task<IReadOnlyList<OptimizationRun>> ListAsync(int take, CancellationToken cancellationToken = default);

    Task<OptimizationRun?> FindAsync(OptimizationRunId id, CancellationToken cancellationToken = default);
}

public static class OptimizationErrors
{
    public static Error NotFound(string id) =>
        Error.NotFound("Optimization.NotFound", $"Optimization {id} does not exist.");

    public static Error AlreadyFinished(string id, OptimizationStatus status) =>
        new("Optimization.AlreadyFinished", $"Optimization {id} is already {status}.", ErrorType.Conflict);

    public static Error NoValidCombination(string reason) =>
        Error.Validation([$"No parameter combination of the grid is valid for the strategy: {reason}"]);
}
