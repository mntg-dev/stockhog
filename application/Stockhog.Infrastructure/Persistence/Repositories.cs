using Stockhog.Application.Abstractions.Events;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Backtesting;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Microsoft.EntityFrameworkCore;

namespace Stockhog.Infrastructure.Persistence;

internal sealed class UnitOfWork(TradingDbContext db, IDomainEventPublisher eventPublisher) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Remember which aggregates raised events, commit, and only then publish:
        // handlers must never react to changes that were rolled back.
        var aggregates = db.ChangeTracker.Entries<IHasDomainEvents>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var names = string.Join(", ", ex.Entries.Select(e => e.Metadata.ClrType.Name).Distinct());
            throw new ConcurrencyConflictException($"{names} changed while this change was being made.", ex);
        }

        var events = aggregates.SelectMany(aggregate => aggregate.DequeueDomainEvents()).ToList();
        if (events.Count > 0)
            await eventPublisher.PublishAsync(events, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class BacktestRunRepository(TradingDbContext db) : IBacktestRunRepository
{
    public void Add(BacktestRun run) => db.BacktestRuns.Add(run);

    public Task<BacktestRun?> GetAsync(BacktestRunId id, CancellationToken cancellationToken = default) =>
        db.BacktestRuns.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<BacktestRun>> ListUnfinishedAsync(CancellationToken cancellationToken = default) =>
        await db.BacktestRuns
            .Where(r => r.Status == BacktestStatus.Queued || r.Status == BacktestStatus.Running)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<BacktestRun>> ListCompletedWithoutMetricsAsync(int take, CancellationToken cancellationToken = default) =>
        await CompletedWithoutMetrics()
            .OrderBy(r => r.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<int> CountCompletedWithoutMetricsAsync(CancellationToken cancellationToken = default) =>
        CompletedWithoutMetrics().CountAsync(cancellationToken);

    private IQueryable<BacktestRun> CompletedWithoutMetrics() =>
        db.BacktestRuns.Where(r => r.Status == BacktestStatus.Completed && r.Metrics == null);
}

/// <summary>
/// Read side: a no-tracking projection of only the columns the history view needs.
/// </summary>
internal sealed class BacktestHistoryReader(TradingDbContext db) : IBacktestHistoryReader
{
    public async Task<IReadOnlyList<BacktestRunSummary>> ListAsync(Symbol? symbol, int take, bool includeTrials = false, CancellationToken cancellationToken = default)
    {
        var query = db.BacktestRuns.AsNoTracking();
        if (symbol is not null)
            query = query.Where(r => r.Symbol == symbol);
        if (!includeTrials)
            query = query.Where(r => r.OptimizationId == null);

        var rows = await query
            .OrderByDescending(r => r.CreatedAt)
            .Take(take)
            .Select(r => new
            {
                r.Id,
                r.Symbol,
                r.Strategy,
                r.Period.Start,
                r.Period.End,
                r.InitialCapital.Amount,
                r.Status,
                r.Progress,
                r.CreatedAt,
                r.StartedAt,
                r.FinishedAt,
                r.FailureReason,
                r.OptimizationId
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(r => new BacktestRunSummary(
                r.Id.Value,
                r.Symbol.Value,
                r.Strategy.Value,
                r.Start,
                r.End,
                r.Amount,
                r.Status,
                r.Progress,
                r.CreatedAt,
                r.StartedAt is { } started && r.FinishedAt is { } finished ? finished - started : null,
                r.FailureReason,
                r.OptimizationId?.Value))
            .ToList();
    }

    public async Task<IReadOnlyList<RankedRun>> ListRankedAsync(CancellationToken cancellationToken = default)
    {
        var rows = await db.BacktestRuns.AsNoTracking()
            .Where(r => r.Status == BacktestStatus.Completed && r.OptimizationId == null && r.Metrics != null)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new
            {
                r.Id,
                r.Symbol,
                r.Strategy,
                r.Period.Start,
                r.Period.End,
                r.InitialCapital.Amount,
                r.CreatedAt,
                r.Metrics
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(r => RankedRun.Create(r.Id.Value, r.Symbol.Value, r.Strategy.Value, r.Start, r.End, r.Amount, r.CreatedAt, r.Metrics!))
            .ToList();
    }
}
