using System.ComponentModel;
using Stockhog.Application.Instruments.RefreshInstruments;

namespace Stockhog.Application.Screening;

/// <summary>One split-adjusted daily bar; screening only, never written to the engine's data folder.</summary>
public readonly record struct DailyBar(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

/// <summary>What the screener ranks the matching symbols by, highest first.</summary>
public enum ScreenerSort
{
    DollarVolume,
    Return6M,
    Return1M,
    AtrPercent
}

/// <summary>
/// Filters applied to every listed instrument. Prices are in USD; null bounds are not checked.
/// </summary>
/// <param name="MinAverageDollarVolume">Average of close × volume over the last 20 sessions.</param>
/// <param name="MinAtrPercent">Average true range of the last 14 sessions as a percentage of the last close.</param>
/// <param name="MinTradingDays">Sessions with a bar in the last year: young listings and thin data are left out.</param>
/// <param name="Exchanges">Alpaca exchange codes to include; see <see cref="RefreshInstrumentsCommand.ListedExchanges"/>.</param>
/// <param name="MaxResults">How many of the best matches are kept, by <paramref name="SortBy"/>.</param>
public sealed record ScreenerCriteria(
    decimal? MinPrice,
    decimal? MaxPrice,
    decimal MinAverageDollarVolume,
    decimal? MinAtrPercent,
    decimal? MaxAtrPercent,
    int MinTradingDays,
    IReadOnlyList<string> Exchanges,
    ScreenerSort SortBy,
    int MaxResults)
{
    public const int MaxResultsLimit = 500;
    public const int MinTradingDaysLimit = 21;
    public const int MaxTradingDaysLimit = 250;

    public static IReadOnlyList<string> AllExchanges { get; } = ["NYSE", "NASDAQ", "ARCA", "AMEX", "BATS"];

    /// <summary>Large, heavily traded stocks: the default universe for testing strategies.</summary>
    public static ScreenerCriteria LiquidLargeCaps { get; } =
        new(10m, null, 50_000_000m, null, null, 200, AllExchanges, ScreenerSort.DollarVolume, 100);

    /// <summary>Tradable mid-sized names: lower liquidity bar, still a year of history.</summary>
    public static ScreenerCriteria LiquidMidCaps { get; } =
        new(5m, 500m, 10_000_000m, null, null, 200, AllExchanges, ScreenerSort.DollarVolume, 150);

    /// <summary>The strongest six-month performers among liquid stocks, for trend-following strategies.</summary>
    public static ScreenerCriteria Momentum { get; } =
        new(10m, null, 20_000_000m, null, null, 200, AllExchanges, ScreenerSort.Return6M, 100);

    /// <summary>Liquid stocks that move a lot each day, for breakout and mean-reversion strategies.</summary>
    public static ScreenerCriteria Volatile { get; } =
        new(5m, null, 20_000_000m, 3m, null, 120, AllExchanges, ScreenerSort.AtrPercent, 100);
}

/// <summary>A named starting point for the criteria, e.g. on the screener page.</summary>
public sealed record ScreenerPreset(string Key, ScreenerCriteria Criteria)
{
    public static IReadOnlyList<ScreenerPreset> All { get; } =
    [
        new("LiquidLargeCaps", ScreenerCriteria.LiquidLargeCaps),
        new("LiquidMidCaps", ScreenerCriteria.LiquidMidCaps),
        new("Momentum", ScreenerCriteria.Momentum),
        new("Volatile", ScreenerCriteria.Volatile)
    ];
}

/// <summary>
/// What the screener measured for one symbol, from its daily bars up to <see cref="LastDate"/>.
/// Returns are percentages; null when the history is too short to tell.
/// </summary>
/// <param name="FromHighPercent">Last close relative to the highest high of the last year: 0 at the high, negative below.</param>
public sealed record SymbolMetrics(
    string Symbol,
    DateOnly LastDate,
    decimal LastClose,
    decimal AverageDollarVolume,
    decimal AtrPercent,
    decimal? Return1MPercent,
    decimal? Return6MPercent,
    decimal FromHighPercent,
    int TradingDays);

/// <summary>A symbol that passed the filters, with its place in the ranking (1 is best).</summary>
public sealed record ScreenedSymbol(int Rank, string Name, string Exchange, SymbolMetrics Metrics)
{
    public string Symbol => Metrics.Symbol;
}

/// <param name="UniverseSize">Listed instruments on the chosen exchanges.</param>
/// <param name="WithData">Instruments that had enough recent daily bars to be measured.</param>
/// <param name="AsOf">The latest session in the data.</param>
public sealed record ScreenerRunSummary(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateOnly AsOf,
    ScreenerCriteria Criteria,
    int UniverseSize,
    int WithData,
    int Matched);

/// <summary>A finished screener run: an immutable snapshot, later the universe for strategy scans.</summary>
[ImmutableObject(true)]
public sealed record ScreenerResult(ScreenerRunSummary Summary, IReadOnlyList<ScreenedSymbol> Symbols);
