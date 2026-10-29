namespace Stockhog.Application.MarketData;

/// <summary>
/// Downloads historical bars from a market data provider and stores them in the local engine data folder.
/// </summary>
public interface IMarketDataDownloader
{
    /// <param name="progress">Receives values from 0 to 100.</param>
    Task<MarketDataDownloadResult> DownloadAsync(
        MarketDataRequest request,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
