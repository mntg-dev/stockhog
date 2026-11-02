namespace Stockhog.Domain.Signals;

/// <summary>What a strategy wants to do with a symbol as of the latest session.</summary>
public enum SignalKind
{
    /// <summary>No position and no recent exit.</summary>
    Flat,

    /// <summary>Opened a long position within the last few sessions: a candidate to buy now.</summary>
    Entry,

    /// <summary>In a long position opened earlier; too late to follow, but worth keeping if already held.</summary>
    Holding,

    /// <summary>Closed its position within the last few sessions: sell if still held.</summary>
    Exit,

    /// <summary>In a short position. The app trades long only, so this is not acted on.</summary>
    Short
}

/// <summary>
/// The latest decision of one strategy on one symbol, read from a backtest that ran up to the latest session.
/// Times are in exchange (New York) time.
/// </summary>
/// <param name="AsOf">The last session the strategy saw.</param>
/// <param name="SessionsSinceEntry">Sessions from the entry to <paramref name="AsOf"/>; 0 when it entered on that day.</param>
/// <param name="RunId">The backtest the signal was read from.</param>
public sealed record StrategySignal(
    SignalKind Kind,
    DateOnly AsOf,
    decimal LastPrice,
    decimal Quantity,
    decimal? EntryPrice,
    DateTime? EntryTime,
    string? EntryTag,
    decimal? StopPrice,
    DateTime? ExitTime,
    string? ExitTag,
    int? SessionsSinceEntry,
    string RunId)
{
    /// <summary>An entry this many sessions old or newer still counts as a fresh signal.</summary>
    public const int DefaultFreshSessions = 1;

    /// <summary>
    /// Classifies the end state of a run: a position opened or closed within <paramref name="freshSessions"/> sessions
    /// of <paramref name="asOf"/> is a fresh entry or exit; older positions are holdings.
    /// </summary>
    public static StrategySignal Classify(
        DateOnly asOf,
        decimal lastPrice,
        decimal quantity,
        decimal averagePrice,
        DateTime? entryTime,
        string? entryTag,
        decimal? stopPrice,
        DateTime? exitTime,
        string? exitTag,
        string runId,
        int freshSessions = DefaultFreshSessions)
    {
        int? sinceEntry = quantity != 0 && entryTime is { } entered ? Sessions.Between(DateOnly.FromDateTime(entered), asOf) : null;
        var kind = quantity switch
        {
            < 0 => SignalKind.Short,
            > 0 => sinceEntry is { } sessions && sessions <= freshSessions ? SignalKind.Entry : SignalKind.Holding,
            _ => exitTime is { } exited && Sessions.Between(DateOnly.FromDateTime(exited), asOf) <= freshSessions ? SignalKind.Exit : SignalKind.Flat
        };

        return new StrategySignal(
            kind,
            asOf,
            lastPrice,
            quantity,
            quantity != 0 ? averagePrice : null,
            quantity != 0 ? entryTime : null,
            quantity != 0 ? entryTag : null,
            quantity != 0 ? stopPrice : null,
            exitTime,
            exitTag,
            sinceEntry,
            runId);
    }

    /// <summary>
    /// Risk per share if bought at <see cref="LastPrice"/> and stopped out at <see cref="StopPrice"/>; null without a stop.
    /// </summary>
    public decimal? RiskPerShare => StopPrice is { } stop && stop < LastPrice ? LastPrice - stop : null;
}

/// <summary>Counts US trading sessions as weekdays; exchange holidays are not known here, which errs towards "older".</summary>
public static class Sessions
{
    /// <returns>Weekdays after <paramref name="from"/> up to and including <paramref name="to"/>; 0 when they are the same day or <paramref name="from"/> is later.</returns>
    public static int Between(DateOnly from, DateOnly to)
    {
        var count = 0;
        for (var day = from.AddDays(1); day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                count++;
        }

        return count;
    }
}
