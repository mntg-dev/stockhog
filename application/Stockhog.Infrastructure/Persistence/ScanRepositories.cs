using Stockhog.Application.Scanning;
using Stockhog.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace Stockhog.Infrastructure.Persistence;

internal sealed class ScanRunRepository(TradingDbContext db) : IScanRunRepository
{
    public void Add(ScanRun run) => db.ScanRuns.Add(run);

    public Task<ScanRun?> GetAsync(ScanRunId id, CancellationToken cancellationToken = default) =>
        db.ScanRuns.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ScanRun>> ListUnfinishedAsync(CancellationToken cancellationToken = default) =>
        await db.ScanRuns
            .Where(r => r.Status == ScanStatus.Queued || r.Status == ScanStatus.Running)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>Read side: untracked aggregates; owned candidates are always loaded with their scan.</summary>
internal sealed class ScanReader(TradingDbContext db) : IScanReader
{
    public async Task<IReadOnlyList<ScanRun>> ListAsync(int take, CancellationToken cancellationToken = default) =>
        await db.ScanRuns
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<ScanRun?> FindAsync(ScanRunId id, CancellationToken cancellationToken = default) =>
        db.ScanRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
}
