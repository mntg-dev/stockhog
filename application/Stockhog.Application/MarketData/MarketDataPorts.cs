using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Application.MarketData;

public interface IMarketDataSetRepository
{
    void Add(MarketDataSet set);

    Task<MarketDataSet?> GetAsync(MarketDataSetId id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read side: what is stored locally, for display.
/// </summary>
public interface IMarketDataCoverageReader
{
    Task<IReadOnlyList<MarketDataCoverage>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed record MarketDataCoverage(
    string Symbol,
    Timeframe Timeframe,
    IReadOnlyList<DateRange> Ranges,
    DateTimeOffset? LastDownloadedAt)
{
    public string RangesText => string.Join("; ", Ranges.Select(r => r.ToString()));

    public int CoveredDays => Ranges.Sum(r => r.Days);
}
