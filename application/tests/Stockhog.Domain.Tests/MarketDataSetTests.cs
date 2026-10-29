using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Domain.Tests;

public class MarketDataSetTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly CompleteThrough = Day(28);

    [Fact]
    public void Everything_is_missing_at_first()
    {
        var set = NewSet();

        Assert.Equal([Range(1, 10)], set.MissingRanges(Range(1, 10)));
    }

    [Fact]
    public void Missing_ranges_are_the_gaps_around_and_between_covered_days()
    {
        var set = NewSet();
        set.RecordDownload(Range(5, 8), CompleteThrough, 1, At);
        set.RecordDownload(Range(12, 15), CompleteThrough, 1, At);

        Assert.Equal([Range(1, 4), Range(9, 11), Range(16, 20)], set.MissingRanges(Range(1, 20)));
        Assert.Equal([Range(9, 11)], set.MissingRanges(Range(6, 13)));
        Assert.Empty(set.MissingRanges(Range(12, 14)));
    }

    [Fact]
    public void Adjacent_and_overlapping_downloads_merge_into_one_range()
    {
        var set = NewSet();
        set.RecordDownload(Range(10, 12), CompleteThrough, 1, At);
        set.RecordDownload(Range(1, 3), CompleteThrough, 1, At);
        set.RecordDownload(Range(4, 5), CompleteThrough, 1, At);   // adjacent to 1..3
        set.RecordDownload(Range(11, 20), CompleteThrough, 1, At); // overlaps 10..12

        Assert.Equal([Range(1, 5), Range(10, 20)], set.Coverage);

        set.RecordDownload(Range(6, 9), CompleteThrough, 1, At);   // bridges both

        Assert.Equal([Range(1, 20)], set.Coverage);
    }

    [Fact]
    public void Days_after_the_last_complete_session_are_not_covered()
    {
        var set = NewSet();
        set.RecordDownload(Range(20, 30), CompleteThrough, 100, At);

        Assert.Equal([Range(20, 28)], set.Coverage);
        Assert.Equal([Range(29, 30)], set.MissingRanges(Range(20, 30)));
    }

    [Fact]
    public void A_download_of_only_today_changes_no_coverage_but_is_recorded()
    {
        var set = NewSet();
        set.RecordDownload(Range(29, 29), CompleteThrough, 5, At);

        Assert.Empty(set.Coverage);
        Assert.Equal(At, set.LastDownloadedAt);
        var downloaded = Assert.IsType<MarketDataDownloaded>(Assert.Single(set.DomainEvents));
        Assert.Equal(5, downloaded.BarCount);
    }

    [Fact]
    public void Id_combines_symbol_and_timeframe()
    {
        Assert.Equal("AAPL:Minute", NewSet().Id.Value);
    }

    [Fact]
    public void Negative_bar_count_is_rejected()
    {
        Assert.Throws<DomainException>(() => NewSet().RecordDownload(Range(1, 2), CompleteThrough, -1, At));
    }

    private static MarketDataSet NewSet() => MarketDataSet.Create(Symbol.Create("aapl"), Timeframe.Minute);

    private static DateOnly Day(int day) => new(2026, 3, day);

    private static DateRange Range(int startDay, int endDay) => DateRange.Create(Day(startDay), Day(endDay));
}
