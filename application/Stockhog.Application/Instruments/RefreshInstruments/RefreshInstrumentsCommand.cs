using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Domain.MarketData;
using Microsoft.Extensions.Caching.Hybrid;

namespace Stockhog.Application.Instruments.RefreshInstruments;

/// <summary>
/// Downloads the instrument list from the broker and replaces the stored one. Keeps only instruments on the main US
/// exchanges (OTC tickers rarely have usable data) whose symbols the app accepts.
/// </summary>
public sealed record RefreshInstrumentsCommand : ICommand<RefreshedInstruments>
{
    /// <summary>Exchange codes as Alpaca reports them.</summary>
    public static readonly IReadOnlySet<string> ListedExchanges =
        new HashSet<string>(["NYSE", "NASDAQ", "ARCA", "AMEX", "BATS"], StringComparer.OrdinalIgnoreCase);
}

/// <param name="Skipped">Instruments left out: other exchanges, or symbols the app does not accept.</param>
public sealed record RefreshedInstruments(int Count, int Skipped)
{
    public string Describe() => $"{Count} instruments stored, {Skipped} skipped.";
}

internal static class InstrumentErrors
{
    public static readonly Error SourceNotConfigured = Error.Failure(
        "Instruments.SourceNotConfigured",
        "The instrument list needs Alpaca keys (Alpaca:ApiKey/ApiSecret or Alpaca:TradingApiKey/TradingApiSecret).");

    public static readonly Error EmptyList = Error.Failure(
        "Instruments.EmptyList",
        "The broker returned no listed instruments; the stored list was kept.");
}

internal sealed class RefreshInstrumentsCommandHandler(IInstrumentSource source, IInstrumentStore store, HybridCache cache, TimeProvider clock)
    : ICommandHandler<RefreshInstrumentsCommand, RefreshedInstruments>
{
    public async Task<Result<RefreshedInstruments>> HandleAsync(RefreshInstrumentsCommand command, CancellationToken cancellationToken)
    {
        if (!source.IsConfigured)
            return InstrumentErrors.SourceNotConfigured;

        var downloaded = await source.GetTradableInstrumentsAsync(cancellationToken).ConfigureAwait(false);

        var kept = downloaded
            .Where(i => RefreshInstrumentsCommand.ListedExchanges.Contains(i.Exchange) && Symbol.IsValid(i.Symbol))
            .Select(i => i with { Symbol = Symbol.Create(i.Symbol).Value, Name = i.Name.Trim() })
            .DistinctBy(i => i.Symbol)
            .OrderBy(i => i.Symbol, StringComparer.Ordinal)
            .ToList();

        // An empty answer is more likely an API problem than a market without stocks: keep what we have.
        if (kept.Count == 0)
            return InstrumentErrors.EmptyList;

        await store.ReplaceAllAsync(kept, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await cache.RemoveByTagAsync(InstrumentCacheKeys.Tag, cancellationToken).ConfigureAwait(false);

        return new RefreshedInstruments(kept.Count, downloaded.Count - kept.Count);
    }
}
