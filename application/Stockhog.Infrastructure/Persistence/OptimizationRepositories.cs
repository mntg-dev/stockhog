using Stockhog.Application.Optimization;
using Stockhog.Domain.Optimization;
using Microsoft.EntityFrameworkCore;

namespace Stockhog.Infrastructure.Persistence;

internal sealed class OptimizationRunRepository(TradingDbContext db) : IOptimizationRunRepository
{
    public void Add(OptimizationRun run) => db.OptimizationRuns.Add(run);

    public Task<OptimizationRun?> GetAsync(OptimizationRunId id, CancellationToken cancellationToken = default) =>
        db.OptimizationRuns.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<OptimizationRun>> ListUnfinishedAsync(CancellationToken cancellationToken = default) =>
        await db.OptimizationRuns
            .Where(r => r.Status == OptimizationStatus.Queued || r.Status == OptimizationStatus.Running)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>Read side: untracked aggregates; owned trials are always loaded with their optimization.</summary>
internal sealed class OptimizationReader(TradingDbContext db) : IOptimizationReader
{
    public async Task<IReadOnlyList<OptimizationRun>> ListAsync(int take, CancellationToken cancellationToken = default) =>
        await db.OptimizationRuns
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<OptimizationRun?> FindAsync(OptimizationRunId id, CancellationToken cancellationToken = default) =>
        db.OptimizationRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
}
