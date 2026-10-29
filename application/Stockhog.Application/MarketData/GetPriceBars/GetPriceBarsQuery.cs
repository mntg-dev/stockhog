using System.ComponentModel;
using Stockhog.Application.Abstractions.Events;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Common;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using FluentValidation;
using Microsoft.Extensions.Caching.Hybrid;

namespace Stockhog.Application.MarketData.GetPriceBars;

/// <summary>
/// Locally stored bars of a symbol for charts, aggregated to the requested resolution.
/// Cached per symbol until a new download of that symbol completes.
/// </summary>
public sealed record GetPriceBarsQuery(string Symbol, BarResolution Resolution, DateOnly Start, DateOnly End)
    : IQuery<PriceSeries>, ICacheableQuery
{
    private string NormalizedSymbol => Symbol.Trim().ToUpperInvariant();

    public string CacheKey => $"prices:{NormalizedSymbol}:{Resolution}:{Start:yyyyMMdd}:{End:yyyyMMdd}";

    public IReadOnlyCollection<string> CacheTags => [PriceCacheTag(NormalizedSymbol)];

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(30);

    internal static string PriceCacheTag(string symbol) => $"prices:{symbol}";
}

public enum BarResolution
{
    Hourly,
    Daily
}

/// <summary>Bars in exchange time, oldest first. Immutable, so the cache hands out the instance itself.</summary>
[ImmutableObject(true)]
public sealed record PriceSeries(string Symbol, BarResolution Resolution, IReadOnlyList<PriceBarData> Bars);

public readonly record struct PriceBarData(DateTime Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

/// <summary>
/// Reads bars from local storage, aggregating finer data when the requested resolution is not stored.
/// </summary>
public interface IPriceBarReader
{
    Task<IReadOnlyList<PriceBarData>> ReadAsync(Symbol symbol, BarResolution resolution, DateRange period, CancellationToken cancellationToken = default);
}

internal sealed class GetPriceBarsQueryValidator : AbstractValidator<GetPriceBarsQuery>
{
    public GetPriceBarsQueryValidator()
    {
        RuleFor(q => q.Symbol).MustBeSymbol();
        RuleFor(q => q.Resolution).IsInEnum();
        RuleFor(q => q.End).GreaterThanOrEqualTo(q => q.Start).WithMessage("End date must not be before start date.");
    }
}

internal sealed class GetPriceBarsQueryHandler(IPriceBarReader reader) : IQueryHandler<GetPriceBarsQuery, PriceSeries>
{
    public async Task<Result<PriceSeries>> HandleAsync(GetPriceBarsQuery query, CancellationToken cancellationToken)
    {
        var symbol = Symbol.Create(query.Symbol);
        var bars = await reader.ReadAsync(symbol, query.Resolution, DateRange.Create(query.Start, query.End), cancellationToken);
        return new PriceSeries(symbol.Value, query.Resolution, bars);
    }
}

/// <summary>New data for a symbol makes its cached bars stale.</summary>
internal sealed class InvalidatePricesWhenMarketDataDownloaded(HybridCache cache) : IDomainEventHandler<MarketDataDownloaded>
{
    public async Task HandleAsync(MarketDataDownloaded domainEvent, CancellationToken cancellationToken) =>
        await cache.RemoveByTagAsync(GetPriceBarsQuery.PriceCacheTag(domainEvent.Symbol.Value), cancellationToken);
}
