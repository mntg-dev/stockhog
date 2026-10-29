using Stockhog.Domain.Backtesting;

namespace Stockhog.Application.Backtesting;

/// <summary>
/// Write side for <see cref="BacktestRun"/> aggregates. Changes are stored by <c>IUnitOfWork.SaveChangesAsync</c>.
/// </summary>
public interface IBacktestRunRepository
{
    void Add(BacktestRun run);

    Task<BacktestRun?> GetAsync(BacktestRunId id, CancellationToken cancellationToken = default);

    /// <summary>Runs that are still queued or running.</summary>
    Task<IReadOnlyList<BacktestRun>> ListUnfinishedAsync(CancellationToken cancellationToken = default);

    /// <summary>Completed runs stored without metrics (by older versions of the app), oldest first.</summary>
    Task<IReadOnlyList<BacktestRun>> ListCompletedWithoutMetricsAsync(int take, CancellationToken cancellationToken = default);

    Task<int> CountCompletedWithoutMetricsAsync(CancellationToken cancellationToken = default);
}
