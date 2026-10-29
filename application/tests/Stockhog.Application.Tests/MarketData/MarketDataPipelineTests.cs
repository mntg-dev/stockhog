using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.MarketData.DownloadMarketData;
using Stockhog.Application.MarketData.GetMarketDataCoverage;
using Stockhog.Application.MarketData.QueueMarketDataDownloads;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Application.Tests.MarketData;

/// <summary>
/// Market data features through the real pipeline. The fake clock is 2026-10-15 08:00 in New York,
/// so the last complete session is 2026-10-14.
/// </summary>
public class MarketDataPipelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_download_fetches_the_period_and_covers_it_up_to_yesterday()
    {
        await using var host = new ApplicationTestHost();

        var result = await host.Dispatcher.SendAsync(Download("aapl", Day(1), Day(15)), Ct);

        Assert.Equal(new MarketDataUpdateResult("AAPL", Timeframe.Minute, 1, 150), result.Value);
        Assert.Equal([Range(1, 15)], host.Downloader.Requests.Select(r => r.Period));
        var set = Assert.Single(host.Database.MarketDataSets);
        Assert.Equal([Range(1, 14)], set.Coverage);
    }

    [Fact]
    public async Task Only_missing_days_are_downloaded()
    {
        await using var host = new ApplicationTestHost();
        await host.Dispatcher.SendAsync(Download("AAPL", Day(5), Day(10)), Ct);
        host.Downloader.Requests.Clear();

        var result = await host.Dispatcher.SendAsync(Download("AAPL", Day(1), Day(14)), Ct);

        Assert.Equal([Range(1, 4), Range(11, 14)], host.Downloader.Requests.Select(r => r.Period));
        Assert.Equal(2, result.Value.RangesDownloaded);
        Assert.Equal([Range(1, 14)], host.Database.MarketDataSets.Single().Coverage);
    }

    [Fact]
    public async Task Covered_period_is_not_downloaded_again()
    {
        await using var host = new ApplicationTestHost();
        await host.Dispatcher.SendAsync(Download("AAPL", Day(1), Day(10)), Ct);

        var result = await host.Dispatcher.SendAsync(Download("AAPL", Day(3), Day(8)), Ct);

        Assert.True(result.Value.WasUpToDate);
        Assert.Single(host.Downloader.Requests);
    }

    [Fact]
    public async Task Today_is_downloaded_again_because_its_bars_are_still_forming()
    {
        await using var host = new ApplicationTestHost();
        await host.Dispatcher.SendAsync(Download("AAPL", Day(10), Day(15)), Ct);
        host.Downloader.Requests.Clear();

        await host.Dispatcher.SendAsync(Download("AAPL", Day(10), Day(15)), Ct);

        Assert.Equal([Range(15, 15)], host.Downloader.Requests.Select(r => r.Period));
    }

    [Fact]
    public async Task Force_refresh_downloads_the_whole_period()
    {
        await using var host = new ApplicationTestHost();
        await host.Dispatcher.SendAsync(Download("AAPL", Day(1), Day(10)), Ct);
        host.Downloader.Requests.Clear();

        await host.Dispatcher.SendAsync(Download("AAPL", Day(1), Day(10)) with { ForceRefresh = true }, Ct);

        Assert.Equal([Range(1, 10)], host.Downloader.Requests.Select(r => r.Period));
    }

    [Fact]
    public async Task Symbol_without_any_data_is_reported_and_not_stored()
    {
        await using var host = new ApplicationTestHost();
        host.Downloader.BarsFor = _ => 0;

        var result = await host.Dispatcher.SendAsync(Download("ZZZZ", Day(1), Day(10)), Ct);

        Assert.Equal("MarketData.NoData", result.Error?.Code);
        Assert.Empty(host.Database.MarketDataSets);
    }

    [Fact]
    public async Task Progress_over_several_gaps_only_increases_and_ends_at_100()
    {
        await using var host = new ApplicationTestHost();
        await host.Dispatcher.SendAsync(Download("AAPL", Day(5), Day(6)), Ct);
        var reported = new List<int>();

        await host.Dispatcher.SendAsync(
            Download("AAPL", Day(1), Day(10)) with { Progress = new SyncProgress<int>(reported.Add) }, Ct);

        Assert.Equal(reported.Order(), reported);
        Assert.Equal(100, reported[^1]);
    }

    [Fact]
    public async Task Queue_creates_one_job_per_symbol_plus_the_benchmark()
    {
        await using var host = new ApplicationTestHost();

        var queued = await host.Dispatcher.SendAsync(
            new QueueMarketDataDownloadsCommand(["aapl", "MSFT", "AAPL"], Timeframe.Minute, Day(1), Day(10)), Ct);

        Assert.Equal(3, queued.Value.JobIds.Count);
        Assert.Equal(["AAPL:Minute", "MSFT:Minute", "SPY:Daily"], host.Jobs.Pending.Select(j => j.Key));

        var outcomes = await host.Jobs.RunAllAsync(host.Dispatcher);

        Assert.All(outcomes, o => Assert.True(o.IsSuccess, o.Message));
        var coverage = await host.Dispatcher.SendAsync(new GetMarketDataCoverageQuery(), Ct);
        Assert.Equal(["AAPL", "MSFT", "SPY"], coverage.Value.Select(c => c.Symbol).Order());
    }

    [Fact]
    public async Task Symbols_with_a_download_in_progress_are_not_queued_twice()
    {
        await using var host = new ApplicationTestHost();
        var command = new QueueMarketDataDownloadsCommand(["AAPL"], Timeframe.Minute, Day(1), Day(10), IncludeBenchmark: false);

        await host.Dispatcher.SendAsync(command, Ct);
        var second = await host.Dispatcher.SendAsync(command, Ct);

        Assert.Empty(second.Value.JobIds);
        Assert.Equal(["AAPL"], second.Value.AlreadyQueued);
        Assert.Single(host.Jobs.Pending);
    }

    [Fact]
    public async Task Failed_download_becomes_a_failed_job_outcome()
    {
        await using var host = new ApplicationTestHost();
        host.Downloader.BarsFor = _ => 0;
        await host.Dispatcher.SendAsync(
            new QueueMarketDataDownloadsCommand(["ZZZZ"], Timeframe.Daily, Day(1), Day(10), IncludeBenchmark: false), Ct);

        var outcome = Assert.Single(await host.Jobs.RunAllAsync(host.Dispatcher));

        Assert.False(outcome.IsSuccess);
        Assert.Contains("No Daily data for ZZZZ", outcome.Message);
    }

    [Fact]
    public async Task Invalid_symbols_are_rejected_before_anything_is_queued()
    {
        await using var host = new ApplicationTestHost();

        var result = await host.Dispatcher.SendAsync(
            new QueueMarketDataDownloadsCommand(["AAPL", "not a symbol"], Timeframe.Minute, Day(1), Day(10)), Ct);

        Assert.Equal(ErrorType.Validation, result.Error?.Type);
        Assert.Empty(host.Jobs.Pending);
    }

    private static DownloadMarketDataCommand Download(string symbol, DateOnly start, DateOnly end) =>
        new(symbol, Timeframe.Minute, start, end);

    private static DateOnly Day(int day) => new(2026, 10, day);

    private static DateRange Range(int startDay, int endDay) => DateRange.Create(Day(startDay), Day(endDay));
}
