using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Stockhog.Application.Trading;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Trading;

/// <summary>Initial risk limits; they can be changed for the session from the UI.</summary>
public sealed class RiskOptions
{
    public const string SectionName = "Trading:Risk";

    public bool TradingEnabled { get; set; } = true;

    [Range(1, 10_000_000)]
    public decimal MaxOrderNotional { get; set; } = RiskLimits.Default.MaxOrderNotional;

    [Range(1, 100_000_000)]
    public decimal MaxPositionNotional { get; set; } = RiskLimits.Default.MaxPositionNotional;

    [Range(1, 10_000_000)]
    public decimal MaxDailyLoss { get; set; } = RiskLimits.Default.MaxDailyLoss;

    [Range(1, 100)]
    public int MaxOpenOrders { get; set; } = RiskLimits.Default.MaxOpenOrders;

    public RiskLimits ToLimits() => new(TradingEnabled, MaxOrderNotional, MaxPositionNotional, MaxDailyLoss, MaxOpenOrders);
}

/// <summary>The limits are replaced as a whole (an immutable record), so readers never see a half-updated set.</summary>
internal sealed class RiskLimitsStore(IOptions<RiskOptions> options) : IRiskLimitsStore
{
    private RiskLimits _current = options.Value.ToLimits();

    public RiskLimits Current => Volatile.Read(ref _current);

    public void Update(RiskLimits limits) => Volatile.Write(ref _current, limits);
}

/// <summary>
/// Latest quote per symbol. Written by the stream reader thread, read by the UI and the order checks;
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keeps both sides lock-free.
/// </summary>
public sealed class QuoteCache : IQuoteCache
{
    private readonly ConcurrentDictionary<string, Quote> _quotes = new(StringComparer.Ordinal);

    public bool TryGet(Symbol symbol, out Quote quote) => _quotes.TryGetValue(symbol.Value, out quote!);

    public IReadOnlyList<Quote> Snapshot() => _quotes.Values.OrderBy(q => q.Symbol, StringComparer.Ordinal).ToList();

    /// <summary>Merges a partial update (a trade or a quote) into the latest values.</summary>
    public void Update(string symbol, decimal? bid, decimal? ask, decimal? last, DateTimeOffset time) =>
        _quotes.AddOrUpdate(
            symbol,
            _ => new Quote(symbol, bid, ask, last, time),
            (_, current) => time < current.Time
                ? current
                : new Quote(symbol, bid ?? current.Bid, ask ?? current.Ask, last ?? current.Last, time));

    public void Remove(string symbol) => _quotes.TryRemove(symbol, out _);
}
