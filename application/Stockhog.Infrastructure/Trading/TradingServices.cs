using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Stockhog.Application.Trading;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;
using Microsoft.Extensions.Logging;
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

    [Range(1, 200)]
    public int MaxOpenPositions { get; set; } = RiskLimits.Default.MaxOpenPositions;

    [Range(1, 100)]
    public decimal MaxPositionPercent { get; set; } = RiskLimits.Default.MaxPositionPercent;

    [Range(1, 100)]
    public decimal MaxDrawdownPercent { get; set; } = RiskLimits.Default.MaxDrawdownPercent;

    [Range(1, 1000)]
    public int MaxOrdersPerDay { get; set; } = RiskLimits.Default.MaxOrdersPerDay;

    /// <summary>Close every position when the drawdown limit is hit, not only stop new ones. Off by default.</summary>
    public bool FlattenOnDrawdown { get; set; } = RiskLimits.Default.FlattenOnDrawdown;

    public RiskLimits ToLimits() => new(TradingEnabled, MaxOrderNotional, MaxPositionNotional, MaxDailyLoss, MaxOpenOrders,
        MaxOpenPositions, MaxPositionPercent, MaxDrawdownPercent, MaxOrdersPerDay, FlattenOnDrawdown);
}

/// <summary>
/// The limits are replaced as a whole (an immutable record), so readers never see a half-updated set. Changes made in
/// the app are saved to a file and win over configuration from the next start, the kill switch included: trading
/// that was switched off, by the user or by the drawdown guard, stays off after a restart.
/// </summary>
internal sealed class RiskLimitsStore : IRiskLimitsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stockhog", "risk-limits.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<RiskLimitsStore> _logger;
    private readonly Lock _write = new();
    private RiskLimits _current;

    public RiskLimitsStore(IOptions<RiskOptions> options, ILogger<RiskLimitsStore> logger, string? path = null)
    {
        _path = path ?? DefaultPath;
        _logger = logger;
        _current = Load() ?? options.Value.ToLimits();
    }

    public RiskLimits Current => Volatile.Read(ref _current);

    public void Update(RiskLimits limits)
    {
        Volatile.Write(ref _current, limits);
        lock (_write)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(limits, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The new limits still apply now; only keeping them for the next start failed.
                _logger.LogError(ex, "Could not save the risk limits to {Path}", _path);
            }
        }
    }

    /// <summary>Saved limits that fail the same checks as the options are ignored, and configuration applies.</summary>
    private RiskLimits? Load()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            var saved = JsonSerializer.Deserialize<RiskLimits>(File.ReadAllText(_path), Json);
            if (saved is not null && IsUsable(saved))
                return saved;
            _logger.LogWarning("Ignoring the saved risk limits in {Path}: they are out of range", _path);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Ignoring the saved risk limits in {Path}", _path);
        }

        return null;
    }

    private static bool IsUsable(RiskLimits l) =>
        l.MaxOrderNotional > 0 && l.MaxPositionNotional > 0 && l.MaxDailyLoss > 0
        && l.MaxOpenOrders is >= 1 and <= 100 && l.MaxOpenPositions is >= 1 and <= 200
        && l.MaxPositionPercent is >= 1 and <= 100 && l.MaxDrawdownPercent is >= 1 and <= 100
        && l.MaxOrdersPerDay is >= 1 and <= 1000;
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
