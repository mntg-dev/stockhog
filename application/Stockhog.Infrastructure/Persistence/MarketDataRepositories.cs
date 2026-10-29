using Stockhog.Application.MarketData;
using Stockhog.Domain.MarketData;
using Microsoft.EntityFrameworkCore;

namespace Stockhog.Infrastructure.Persistence;

internal sealed class MarketDataSetRepository(TradingDbContext db) : IMarketDataSetRepository
{
    public void Add(MarketDataSet set) => db.MarketDataSets.Add(set);

    public Task<MarketDataSet?> GetAsync(MarketDataSetId id, CancellationToken cancellationToken = default) =>
        db.MarketDataSets.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
}

internal sealed class MarketDataCoverageReader(TradingDbContext db) : IMarketDataCoverageReader
{
    public async Task<IReadOnlyList<MarketDataCoverage>> ListAsync(CancellationToken cancellationToken = default)
    {
        // Few rows, and the JSON column is converted in memory anyway, so the sets are loaded without tracking.
        var sets = await db.MarketDataSets.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        return sets
            .OrderBy(s => s.Symbol.Value)
            .ThenBy(s => s.Timeframe)
            .Select(s => new MarketDataCoverage(s.Symbol.Value, s.Timeframe, s.Coverage, s.LastDownloadedAt))
            .ToList();
    }
}
