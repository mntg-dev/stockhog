using Stockhog.Domain.MarketData;

namespace Stockhog.Infrastructure.MarketData;

// Seams between infrastructure adapters. The downloader in this project combines a source of historical bars
// (implemented by Stockhog.Infrastructure.Alpaca) with local storage (implemented by Stockhog.Infrastructure.Lean),
// so neither adapter project depends on the other.

/// <summary>
/// A provider of historical bars, read page by page.
/// </summary>
public interface IHistoricalBarSource
{
    /// <summary>Yields pages of bars, oldest first, as they arrive.</summary>
    /// <param name="coverage">Receives the fraction (0..1) of the requested range fetched so far.</param>
    IAsyncEnumerable<IReadOnlyList<MarketBar>> StreamPagesAsync(
        string symbol,
        Timeframe timeframe,
        DateTime startUtc,
        DateTime endUtc,
        IProgress<double>? coverage,
        CancellationToken cancellationToken);
}

/// <summary>
/// Local storage that backtests read market data from.
/// </summary>
public interface IMarketDataStore
{
    /// <summary>Makes sure the reference files the engine needs (market hours, symbol properties, ...) exist.</summary>
    Task EnsureReferenceDataAsync(CancellationToken cancellationToken);

    IBarSink CreateSink(string symbol, Timeframe timeframe);
}

/// <summary>
/// Receives bars incrementally; <see cref="CompleteAsync"/> writes whatever is still buffered.
/// </summary>
public interface IBarSink
{
    Task AddAsync(IReadOnlyList<MarketBar> bars, CancellationToken cancellationToken);

    /// <returns>The number of files written.</returns>
    Task<int> CompleteAsync(CancellationToken cancellationToken);
}
