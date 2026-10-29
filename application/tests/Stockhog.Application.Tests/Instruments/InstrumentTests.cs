using Stockhog.Application.Instruments;
using Stockhog.Application.Instruments.QueueInstrumentRefresh;
using Stockhog.Application.Instruments.RefreshInstruments;
using Stockhog.Application.Tests.TestSupport;

namespace Stockhog.Application.Tests.Instruments;

public class InstrumentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Refresh_keeps_listed_instruments_with_valid_symbols()
    {
        await using var host = new ApplicationTestHost();
        host.InstrumentSource.Instruments.AddRange(
        [
            new("msft", " Microsoft Corporation ", "NASDAQ"),
            new("AAPL", "Apple Inc.", "NASDAQ"),
            new("SPY", "SPDR S&P 500 ETF", "ARCA"),
            new("OTCX", "Some OTC company", "OTC"),
            new("TOOLONGSYMBOL", "Invalid symbol", "NYSE"),
            new("AAPL", "Apple duplicate", "NASDAQ")
        ]);

        var result = await host.Dispatcher.SendAsync(new RefreshInstrumentsCommand(), Ct);

        Assert.Equal(new RefreshedInstruments(3, 3), result.Value);
        Assert.Equal(["AAPL", "MSFT", "SPY"], host.Instruments.Instruments.Select(i => i.Symbol));
        Assert.Equal("Microsoft Corporation", host.Instruments.Instruments[1].Name);
        Assert.Equal(TestData.Now, host.Instruments.RefreshedAt);
    }

    [Fact]
    public async Task Refresh_replaces_the_cached_list()
    {
        await using var host = new ApplicationTestHost();
        host.InstrumentSource.Instruments.Add(new("AAPL", "Apple Inc.", "NASDAQ"));
        await host.Dispatcher.SendAsync(new RefreshInstrumentsCommand(), Ct);
        Assert.Single((await host.Dispatcher.SendAsync(new GetInstrumentsQuery(), Ct)).Value);

        host.InstrumentSource.Instruments.Add(new("MSFT", "Microsoft Corporation", "NASDAQ"));
        await host.Dispatcher.SendAsync(new RefreshInstrumentsCommand(), Ct);

        var instruments = await host.Dispatcher.SendAsync(new GetInstrumentsQuery(), Ct);
        Assert.Equal(["AAPL", "MSFT"], instruments.Value.Select(i => i.Symbol));
    }

    [Fact]
    public async Task Empty_answer_keeps_the_stored_list()
    {
        await using var host = new ApplicationTestHost();
        await host.Instruments.ReplaceAllAsync([new("AAPL", "Apple Inc.", "NASDAQ")], TestData.Now.AddDays(-1), Ct);

        var result = await host.Dispatcher.SendAsync(new RefreshInstrumentsCommand(), Ct);

        Assert.True(result.IsFailure);
        Assert.Single(host.Instruments.Instruments);
    }

    [Fact]
    public async Task Refresh_without_credentials_fails_without_calling_the_source()
    {
        await using var host = new ApplicationTestHost();
        host.InstrumentSource.IsConfigured = false;

        var refresh = await host.Dispatcher.SendAsync(new RefreshInstrumentsCommand(), Ct);
        var queue = await host.Dispatcher.SendAsync(new QueueInstrumentRefreshCommand(), Ct);

        Assert.Equal("Instruments.SourceNotConfigured", refresh.Error?.Code);
        Assert.Equal("Instruments.SourceNotConfigured", queue.Error?.Code);
        Assert.Equal(0, host.InstrumentSource.Calls);
        Assert.Empty(host.Jobs.Pending);
    }

    [Fact]
    public async Task Queued_refresh_runs_as_a_job_and_is_not_queued_twice()
    {
        await using var host = new ApplicationTestHost();
        host.InstrumentSource.Instruments.Add(new("AAPL", "Apple Inc.", "NASDAQ"));

        var first = await host.Dispatcher.SendAsync(new QueueInstrumentRefreshCommand(), Ct);
        var second = await host.Dispatcher.SendAsync(new QueueInstrumentRefreshCommand(), Ct);
        var outcomes = await host.Jobs.RunAllAsync(host.Dispatcher);

        Assert.True(first.Value.WasQueued);
        Assert.False(second.Value.WasQueued);
        Assert.Equal(first.Value.JobId, second.Value.JobId);
        Assert.True(Assert.Single(outcomes).IsSuccess);
        Assert.Single(host.Instruments.Instruments);
    }

    [Theory]
    [InlineData(0, true)]   // never downloaded
    [InlineData(8, true)]   // older than a week
    [InlineData(6, false)]  // still fresh
    public async Task Startup_refresh_is_queued_only_when_the_list_is_missing_or_stale(int ageInDays, bool expectQueued)
    {
        await using var host = new ApplicationTestHost();
        if (ageInDays > 0)
            await host.Instruments.ReplaceAllAsync([new("AAPL", "Apple Inc.", "NASDAQ")], TestData.Now.AddDays(-ageInDays), Ct);

        var result = await host.Dispatcher.SendAsync(new QueueInstrumentRefreshCommand(OnlyIfStale: true), Ct);

        Assert.Equal(expectQueued, result.Value.WasQueued);
        Assert.Equal(expectQueued ? 1 : 0, host.Jobs.Pending.Count);
    }

    [Fact]
    public async Task Startup_refresh_is_skipped_quietly_without_credentials()
    {
        await using var host = new ApplicationTestHost();
        host.InstrumentSource.IsConfigured = false;

        var result = await host.Dispatcher.SendAsync(new QueueInstrumentRefreshCommand(OnlyIfStale: true), Ct);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.WasQueued);
        Assert.Empty(host.Jobs.Pending);
    }
}
