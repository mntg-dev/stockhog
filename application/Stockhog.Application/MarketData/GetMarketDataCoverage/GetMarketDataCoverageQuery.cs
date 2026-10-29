using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;

namespace Stockhog.Application.MarketData.GetMarketDataCoverage;

/// <summary>
/// Which days of which symbol and timeframe are stored locally.
/// </summary>
public sealed record GetMarketDataCoverageQuery : IQuery<IReadOnlyList<MarketDataCoverage>>;

internal sealed class GetMarketDataCoverageQueryHandler(IMarketDataCoverageReader reader)
    : IQueryHandler<GetMarketDataCoverageQuery, IReadOnlyList<MarketDataCoverage>>
{
    public async Task<Result<IReadOnlyList<MarketDataCoverage>>> HandleAsync(GetMarketDataCoverageQuery query, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<MarketDataCoverage>>.Success(await reader.ListAsync(cancellationToken));
}
