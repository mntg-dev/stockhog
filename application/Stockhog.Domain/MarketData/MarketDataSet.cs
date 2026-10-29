using Stockhog.Domain.Common;

namespace Stockhog.Domain.MarketData;

/// <summary>
/// Locally stored bars of one symbol and timeframe. Tracks which days have been downloaded,
/// so a new request only fetches the gaps.
/// </summary>
/// <remarks>
/// Coverage is a sorted list of non-overlapping, non-adjacent day ranges. A day only counts as covered once the
/// market has closed it: today's bars are still forming, so they are fetched again by the next download.
/// </remarks>
public sealed class MarketDataSet : AggregateRoot<MarketDataSetId>
{
    // Replaced (never mutated) on change: the list instance is immutable for persistence change tracking.
    private IReadOnlyList<DateRange> _coverage = [];

    private MarketDataSet(MarketDataSetId id, Symbol symbol, Timeframe timeframe) : base(id)
    {
        Symbol = symbol;
        Timeframe = timeframe;
    }

#pragma warning disable CS8618 // Persistence materializes sets through this constructor and then sets every property.
    private MarketDataSet() : base(default!)
    {
    }
#pragma warning restore CS8618

    public Symbol Symbol { get; }

    public Timeframe Timeframe { get; }

    public IReadOnlyList<DateRange> Coverage => _coverage;

    public DateTimeOffset? LastDownloadedAt { get; private set; }

    public static MarketDataSet Create(Symbol symbol, Timeframe timeframe) =>
        new(MarketDataSetId.For(symbol, timeframe), symbol, timeframe);

    /// <summary>
    /// Parts of <paramref name="requested"/> that are not covered yet, in order.
    /// </summary>
    public IReadOnlyList<DateRange> MissingRanges(DateRange requested)
    {
        var missing = new List<DateRange>();
        var cursor = requested.Start;

        foreach (var covered in _coverage)
        {
            if (covered.End < cursor)
                continue;
            if (covered.Start > requested.End)
                break;

            if (covered.Start > cursor)
                missing.Add(DateRange.Create(cursor, covered.Start.AddDays(-1)));

            if (covered.End >= requested.End)
                return missing;

            cursor = covered.End.AddDays(1);
        }

        missing.Add(DateRange.Create(cursor, requested.End));
        return missing;
    }

    /// <summary>
    /// Records a finished download. Only days up to <paramref name="completeThrough"/> become covered.
    /// </summary>
    /// <param name="completeThrough">Last day whose bars can no longer change (normally yesterday in exchange time).</param>
    public void RecordDownload(DateRange downloaded, DateOnly completeThrough, int barCount, DateTimeOffset at)
    {
        if (barCount < 0)
            throw new DomainException("Bar count cannot be negative.");

        LastDownloadedAt = at;

        if (downloaded.Start <= completeThrough)
        {
            var end = downloaded.End <= completeThrough ? downloaded.End : completeThrough;
            _coverage = Merge(_coverage, DateRange.Create(downloaded.Start, end));
        }

        Raise(new MarketDataDownloaded(Id, Symbol, Timeframe, downloaded, barCount, at));
    }

    private static IReadOnlyList<DateRange> Merge(IReadOnlyList<DateRange> coverage, DateRange added)
    {
        var merged = new List<DateRange>(coverage.Count + 1);
        var start = added.Start;
        var end = added.End;
        var inserted = false;

        foreach (var range in coverage)
        {
            if (range.End.AddDays(1) < start)
            {
                merged.Add(range); // entirely before, not touching
            }
            else if (range.Start > end.AddDays(1))
            {
                if (!inserted)
                {
                    merged.Add(DateRange.Create(start, end));
                    inserted = true;
                }

                merged.Add(range); // entirely after, not touching
            }
            else
            {
                // Overlapping or adjacent: absorb into the range being inserted.
                start = range.Start < start ? range.Start : start;
                end = range.End > end ? range.End : end;
            }
        }

        if (!inserted)
            merged.Add(DateRange.Create(start, end));

        return merged.AsReadOnly();
    }
}

/// <summary>
/// <c>{SYMBOL}:{Timeframe}</c>, e.g. <c>AAPL:Minute</c>.
/// </summary>
public sealed record MarketDataSetId
{
    private MarketDataSetId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static MarketDataSetId For(Symbol symbol, Timeframe timeframe) => new($"{symbol.Value}:{timeframe}");

    public static MarketDataSetId From(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(':')
            ? new MarketDataSetId(value)
            : throw new DomainException($"'{value}' is not a valid market data set id.");

    public override string ToString() => Value;
}

public sealed record MarketDataDownloaded(
    MarketDataSetId SetId,
    Symbol Symbol,
    Timeframe Timeframe,
    DateRange Period,
    int BarCount,
    DateTimeOffset OccurredAt) : IDomainEvent;
