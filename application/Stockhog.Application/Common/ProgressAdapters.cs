namespace Stockhog.Application.Common;

/// <summary>
/// Maps a 0..100 sub-task progress onto <c>[from, to]</c> of the overall progress.
/// Reports synchronously, so values arrive in order.
/// </summary>
internal sealed class ScaledProgress(IProgress<int> overall, int from, int to) : IProgress<int>
{
    public void Report(int value) => overall.Report(from + (int)Math.Round((to - from) * Math.Clamp(value, 0, 100) / 100.0));

    /// <summary>Progress for part <paramref name="index"/> of <paramref name="count"/> equal parts.</summary>
    public static IProgress<int>? ForPart(IProgress<int>? overall, int index, int count) =>
        overall is null ? null : new ScaledProgress(overall, index * 100 / count, (index + 1) * 100 / count);
}

internal sealed class DelegateProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>
/// Market sessions end in New York, so "complete" days are judged in exchange time.
/// </summary>
internal static class ExchangeCalendar
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>
    /// Yesterday in New York: the latest day whose bars can no longer change. Conservative, as it ignores the close time.
    /// </summary>
    public static DateOnly LastCompletedSession(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), NewYork).DateTime).AddDays(-1);

    /// <summary>
    /// The close (16:00 New York) of the first weekday after <paramref name="session"/>: when an order meant for the
    /// next session can no longer be placed. Exchange holidays are not known, which errs towards expiring early.
    /// </summary>
    public static DateTimeOffset NextSessionClose(DateOnly session)
    {
        var next = session.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            next = next.AddDays(1);

        var close = next.ToDateTime(new TimeOnly(16, 0));
        return new DateTimeOffset(close, NewYork.GetUtcOffset(close)).ToUniversalTime();
    }
}
