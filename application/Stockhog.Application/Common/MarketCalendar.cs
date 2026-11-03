namespace Stockhog.Application.Common;

/// <summary>A day the US stock market trades, with its regular hours in New York time.</summary>
public sealed record MarketSession(DateOnly Date, TimeOnly Open, TimeOnly Close);

/// <summary>The exchange calendar: holidays and early closes.</summary>
public interface IMarketCalendar
{
    /// <returns>The sessions between the dates (inclusive), or null when the calendar cannot be read.</returns>
    Task<IReadOnlyList<MarketSession>?> GetSessionsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>Where today stands in the trading week, in New York time.</summary>
/// <param name="Session">Today's session, or null when the market does not open today.</param>
/// <param name="PreviousSession">The last session before today: the close today's decisions build on.</param>
/// <param name="FromCalendar">False when the calendar could not be read and weekdays with regular hours were assumed.</param>
public sealed record MarketDay(DateTime NewYorkNow, MarketSession? Session, DateOnly PreviousSession, bool FromCalendar)
{
    public DateOnly Today => DateOnly.FromDateTime(NewYorkNow);

    /// <summary>Whether the market is open now, at least <paramref name="margin"/> after the open and before the close.</summary>
    public bool IsOpen(TimeSpan margin)
    {
        if (Session is not { } session)
            return false;
        var time = TimeOnly.FromDateTime(NewYorkNow);
        return time >= session.Open.Add(margin) && time < session.Close.Add(-margin);
    }
}

/// <summary>Reads today's place in the calendar; falls back to weekdays from 9:30 to 16:00 without it.</summary>
internal sealed class MarketDays(IMarketCalendar calendar, TimeProvider clock)
{
    private static readonly TimeOnly RegularOpen = new(9, 30);
    private static readonly TimeOnly RegularClose = new(16, 0);

    public async Task<MarketDay> TodayAsync(CancellationToken cancellationToken)
    {
        var now = ExchangeCalendar.NewYorkNow(clock);
        var today = DateOnly.FromDateTime(now);

        // Two weeks back always reach a session, even after the longest run of holidays and a weekend.
        var sessions = await calendar.GetSessionsAsync(today.AddDays(-14), today, cancellationToken);
        if (sessions is not null && sessions.Any(s => s.Date < today))
        {
            return new MarketDay(
                now,
                sessions.FirstOrDefault(s => s.Date == today),
                sessions.Where(s => s.Date < today).Max(s => s.Date),
                FromCalendar: true);
        }

        var weekday = today.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        return new MarketDay(now, weekday ? new MarketSession(today, RegularOpen, RegularClose) : null, ExchangeCalendar.PreviousSession(today), FromCalendar: false);
    }
}
