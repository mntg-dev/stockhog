namespace Stockhog.Infrastructure.MarketData;

/// <summary>
/// US equity data is stored by Lean in exchange time (New York).
/// </summary>
public static class ExchangeTime
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static DateTime FromUtc(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), NewYork);

    /// <summary>Midnight of the current New York day, as UTC: the start of today's session data.</summary>
    public static DateTime StartOfDayUtc(DateTimeOffset now) =>
        TimeZoneInfo.ConvertTimeToUtc(FromUtc(now.UtcDateTime).Date, NewYork);
}
