using Stockhog.Domain.MarketData;
using Stockhog.Infrastructure.Configuration;
using Stockhog.Infrastructure.MarketData;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Lean.Data;

/// <summary>
/// The Lean data folder as an <see cref="IMarketDataStore"/>.
/// </summary>
internal sealed class LeanMarketDataStore(
    LeanEquityDataWriter writer,
    LeanReferenceDataClient referenceData,
    IOptions<StorageOptions> storage) : IMarketDataStore
{
    public Task EnsureReferenceDataAsync(CancellationToken cancellationToken) =>
        referenceData.EnsureReferenceDataAsync(cancellationToken);

    public IBarSink CreateSink(string symbol, Timeframe timeframe) =>
        writer.CreateSink(storage.Value.DataFolder, symbol, timeframe);
}
