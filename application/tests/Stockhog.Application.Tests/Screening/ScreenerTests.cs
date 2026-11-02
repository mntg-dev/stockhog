using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Instruments;
using Stockhog.Application.Screening;
using Stockhog.Application.Screening.QueueScreener;
using Stockhog.Application.Screening.RunScreener;
using Stockhog.Application.Tests.TestSupport;

namespace Stockhog.Application.Tests.Screening;

public class ScreenerTests
{
    // TestData.Now is 2026-10-15 08:00 in New York, so the last completed session is the 14th.
    private static readonly DateOnly LastSession = new(2026, 10, 14);

    private static readonly ScreenerCriteria NasdaqAndNyse =
        ScreenerCriteria.LiquidLargeCaps with { Exchanges = ["NASDAQ", "NYSE"] };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Measures_averages_range_returns_and_distance_from_the_high()
    {
        var flat = ScreenerCalculator.Measure("FLAT", FakeDailyBarSource.Series(30, LastSession, 100m))!;

        Assert.Equal(LastSession, flat.LastDate);
        Assert.Equal(100m, flat.LastClose);
        Assert.Equal(100_000_000m, flat.AverageDollarVolume);
        Assert.Equal(2m, flat.AtrPercent);                 // high 101, low 99
        Assert.Equal(0m, flat.Return1MPercent);
        Assert.Null(flat.Return6MPercent);                  // 30 bars are not six months
        Assert.Equal(-0.99m, flat.FromHighPercent);         // 100 vs the 101 high
        Assert.Equal(30, flat.TradingDays);

        var rising = ScreenerCalculator.Measure("UP", FakeDailyBarSource.Series(200, LastSession, 50m, dailyChange: 1m))!;
        Assert.Equal(249m, rising.LastClose);
        Assert.Equal(102.44m, rising.Return6MPercent);      // 249 vs 123 six months (126 sessions) earlier
    }

    [Fact]
    public void Too_short_a_history_is_not_measured()
    {
        Assert.Null(ScreenerCalculator.Measure("NEW", FakeDailyBarSource.Series(ScreenerCalculator.AverageWindow, LastSession, 10m)));
    }

    [Fact]
    public async Task Keeps_ranks_and_saves_the_symbols_that_pass_the_filters()
    {
        await using var host = new ApplicationTestHost();
        host.DailyBars.MaxSymbolsPerRequest = 3;
        await GivenUniverseAsync(host);

        var result = await host.Dispatcher.SendAsync(new RunScreenerCommand(NasdaqAndNyse), Ct);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var summary = result.Value;
        Assert.Equal(LastSession, summary.AsOf);
        Assert.Equal(7, summary.UniverseSize);   // SPY is on ARCA, which was not chosen
        Assert.Equal(6, summary.WithData);       // NODATA returned no bars
        Assert.Equal(2, summary.Matched);

        var saved = Assert.Single(host.ScreenerResults.Results);
        Assert.Equal(["MSFT", "AAPL"], saved.Symbols.Select(s => s.Symbol));
        Assert.Equal([1, 2], saved.Symbols.Select(s => s.Rank));
        Assert.Equal("Microsoft Corporation", saved.Symbols[0].Name);

        // Seven symbols in batches of at most three, each over the same year up to the last completed session.
        var requests = host.DailyBars.Requests.ToList();
        Assert.Equal(3, requests.Count);
        Assert.All(requests, r => Assert.True(r.Symbols.Count <= 3));
        Assert.All(requests, r => Assert.Equal((LastSession.AddDays(-RunScreenerCommand.LookbackDays), LastSession), (r.Start, r.End)));
        Assert.DoesNotContain(requests, r => r.Symbols.Contains("SPY"));
    }

    [Fact]
    public async Task Keeps_only_the_requested_number_of_best_matches()
    {
        await using var host = new ApplicationTestHost();
        await GivenUniverseAsync(host);

        var result = await host.Dispatcher.SendAsync(new RunScreenerCommand(NasdaqAndNyse with { MaxResults = 1 }), Ct);

        Assert.Equal(1, result.Value.Matched);
        Assert.Equal("MSFT", Assert.Single(host.ScreenerResults.Results[0].Symbols).Symbol);
    }

    [Fact]
    public async Task Sorting_by_return_puts_symbols_without_enough_history_last()
    {
        await using var host = new ApplicationTestHost();
        await host.Instruments.ReplaceAllAsync(
        [
            new("SLOW", "Slow riser", "NYSE"),
            new("FAST", "Fast riser", "NYSE"),
            new("NEW", "Recent listing", "NYSE")
        ], TestData.Now, Ct);
        host.DailyBars.Bars["SLOW"] = FakeDailyBarSource.Series(250, LastSession, 100m, dailyChange: 0.1m);
        host.DailyBars.Bars["FAST"] = FakeDailyBarSource.Series(250, LastSession, 100m, dailyChange: 0.5m);
        host.DailyBars.Bars["NEW"] = FakeDailyBarSource.Series(60, LastSession, 100m, dailyChange: 2m);

        var criteria = ScreenerCriteria.Momentum with { MinTradingDays = 21, MinAverageDollarVolume = 0 };
        await host.Dispatcher.SendAsync(new RunScreenerCommand(criteria), Ct);

        Assert.Equal(["FAST", "SLOW", "NEW"], host.ScreenerResults.Results[0].Symbols.Select(s => s.Symbol));
    }

    [Theory]
    [InlineData("State Street SPDR S&P 500 ETF Trust", true)]
    [InlineData("Invesco QQQ Trust, Series 1", true)]
    [InlineData("iShares Russell 2000 ETF", true)]
    [InlineData("Direxion Daily Semiconductor Bull 3X Shares", true)]
    [InlineData("ProShares UltraPro QQQ", true)]
    [InlineData("MicroSectors FANG+ Index 3X Leveraged ETN", true)]
    [InlineData("United States Oil Fund, LP", true)]
    [InlineData("SPDR Gold Shares", true)]
    [InlineData("Apple Inc. Common Stock", false)]
    [InlineData("Vornado Realty Trust Common Stock", false)]
    [InlineData("Taiwan Semiconductor Manufacturing Company Ltd.", false)]
    [InlineData("Fundamental Global Inc.", false)]
    [InlineData("3M Company", false)]
    public void Funds_are_told_from_companies_by_name(string name, bool isFund)
    {
        Assert.Equal(isFund, FundNames.LooksLikeFund(name));
    }

    [Fact]
    public async Task Stocks_only_leaves_funds_out_of_the_universe()
    {
        await using var host = new ApplicationTestHost();
        await host.Instruments.ReplaceAllAsync(
        [
            new("AAPL", "Apple Inc. Common Stock", "NASDAQ"),
            new("SPY", "State Street SPDR S&P 500 ETF Trust", "ARCA"),
            new("SOXL", "Direxion Daily Semiconductor Bull 3X Shares", "ARCA")
        ], TestData.Now, Ct);
        foreach (var symbol in new[] { "AAPL", "SPY", "SOXL" })
            host.DailyBars.Bars[symbol] = FakeDailyBarSource.Series(250, LastSession, 200m);

        var stocks = await host.Dispatcher.SendAsync(new RunScreenerCommand(ScreenerCriteria.LiquidLargeCaps), Ct);
        var everything = await host.Dispatcher.SendAsync(new RunScreenerCommand(ScreenerCriteria.LiquidLargeCaps with { StocksOnly = false }), Ct);

        Assert.Equal((1, 1), (stocks.Value.UniverseSize, stocks.Value.Matched));
        Assert.Equal("AAPL", Assert.Single(host.ScreenerResults.Results[0].Symbols).Symbol);
        Assert.Equal((3, 3), (everything.Value.UniverseSize, everything.Value.Matched));
        Assert.DoesNotContain(host.DailyBars.Requests.First().Symbols, s => s is "SPY" or "SOXL");
    }

    [Fact]
    public async Task Latest_result_is_returned_with_its_symbols()
    {
        await using var host = new ApplicationTestHost();
        await GivenUniverseAsync(host);

        var none = await host.Dispatcher.SendAsync(new GetScreenerResultQuery(), Ct);
        await host.Dispatcher.SendAsync(new RunScreenerCommand(NasdaqAndNyse), Ct);
        var latest = await host.Dispatcher.SendAsync(new GetScreenerResultQuery(), Ct);
        var runs = await host.Dispatcher.SendAsync(new GetScreenerRunsQuery(), Ct);

        Assert.Equal(ErrorType.NotFound, none.Error?.Type);
        Assert.Equal(2, latest.Value.Symbols.Count);
        Assert.Equal(latest.Value.Summary.Id, Assert.Single(runs.Value).Id);
    }

    public static TheoryData<ScreenerCriteria> InvalidCriteria => new()
    {
        NasdaqAndNyse with { MinPrice = 50, MaxPrice = 10 },
        NasdaqAndNyse with { MinAtrPercent = 5, MaxAtrPercent = 2 },
        NasdaqAndNyse with { MaxResults = 0 },
        NasdaqAndNyse with { MaxResults = ScreenerCriteria.MaxResultsLimit + 1 },
        NasdaqAndNyse with { MinTradingDays = 5 },
        NasdaqAndNyse with { Exchanges = [] },
        NasdaqAndNyse with { Exchanges = ["OTC"] },
        NasdaqAndNyse with { MinAverageDollarVolume = -1 }
    };

    [Theory]
    [MemberData(nameof(InvalidCriteria))]
    public async Task Invalid_criteria_are_rejected_before_any_request(ScreenerCriteria criteria)
    {
        await using var host = new ApplicationTestHost();
        await GivenUniverseAsync(host);

        var run = await host.Dispatcher.SendAsync(new RunScreenerCommand(criteria), Ct);
        var queue = await host.Dispatcher.SendAsync(new QueueScreenerCommand(criteria), Ct);

        Assert.Equal(ErrorType.Validation, run.Error?.Type);
        Assert.Equal(ErrorType.Validation, queue.Error?.Type);
        Assert.Empty(host.DailyBars.Requests);
        Assert.Empty(host.Jobs.Pending);
    }

    [Fact]
    public async Task Without_credentials_nothing_is_requested_or_queued()
    {
        await using var host = new ApplicationTestHost();
        await GivenUniverseAsync(host);
        host.DailyBars.IsConfigured = false;

        var run = await host.Dispatcher.SendAsync(new RunScreenerCommand(NasdaqAndNyse), Ct);
        var queue = await host.Dispatcher.SendAsync(new QueueScreenerCommand(NasdaqAndNyse), Ct);

        Assert.Equal("Screener.SourceNotConfigured", run.Error?.Code);
        Assert.Equal("Screener.SourceNotConfigured", queue.Error?.Code);
        Assert.Empty(host.DailyBars.Requests);
        Assert.Empty(host.Jobs.Pending);
    }

    [Fact]
    public async Task An_empty_instrument_list_or_no_data_saves_nothing()
    {
        await using var host = new ApplicationTestHost();

        var noInstruments = await host.Dispatcher.SendAsync(new RunScreenerCommand(NasdaqAndNyse), Ct);
        await host.Instruments.ReplaceAllAsync([new("AAPL", "Apple Inc.", "NASDAQ")], TestData.Now, Ct);
        var noData = await host.Dispatcher.SendAsync(new RunScreenerCommand(NasdaqAndNyse), Ct);

        Assert.Equal("Screener.NoInstruments", noInstruments.Error?.Code);
        Assert.Equal("Screener.NoData", noData.Error?.Code);
        Assert.Empty(host.ScreenerResults.Results);
    }

    [Fact]
    public async Task Queued_run_is_a_single_job_that_reports_the_match_count()
    {
        await using var host = new ApplicationTestHost();
        await GivenUniverseAsync(host);

        var first = await host.Dispatcher.SendAsync(new QueueScreenerCommand(NasdaqAndNyse), Ct);
        var second = await host.Dispatcher.SendAsync(new QueueScreenerCommand(NasdaqAndNyse), Ct);
        Assert.Equal(ScreenerKeys.RunJobKey, Assert.Single(host.Jobs.Pending).Key);
        var outcomes = await host.Jobs.RunAllAsync(host.Dispatcher);

        Assert.True(first.Value.WasQueued);
        Assert.False(second.Value.WasQueued);
        Assert.Equal(first.Value.JobId, second.Value.JobId);
        var outcome = Assert.Single(outcomes);
        Assert.True(outcome.IsSuccess, outcome.Message);
        Assert.StartsWith("2 of 6 instruments match", outcome.Message);
    }

    /// <summary>
    /// Two liquid large caps that pass, and one symbol failing each of the other filters.
    /// </summary>
    private static async Task GivenUniverseAsync(ApplicationTestHost host)
    {
        await host.Instruments.ReplaceAllAsync(
        [
            new("AAPL", "Apple Inc.", "NASDAQ"),
            new("MSFT", "Microsoft Corporation", "NASDAQ"),
            new("PENNY", "Cheap stock", "NYSE"),
            new("THIN", "Thinly traded", "NYSE"),
            new("STALE", "Halted company", "NYSE"),
            new("NODATA", "No bars at all", "NYSE"),
            new("YOUNG", "Recent listing", "NYSE"),
            new("SPY", "SPDR S&P 500 ETF", "ARCA")
        ], TestData.Now, Ct);

        var bars = host.DailyBars.Bars;
        bars["AAPL"] = FakeDailyBarSource.Series(250, LastSession, 200m);                      // $200M a day
        bars["MSFT"] = FakeDailyBarSource.Series(250, LastSession, 400m);                      // $400M a day
        bars["PENNY"] = FakeDailyBarSource.Series(250, LastSession, 3m, volume: 100_000_000);  // liquid but under $10
        bars["THIN"] = FakeDailyBarSource.Series(250, LastSession, 50m, volume: 10_000);       // $0.5M a day
        bars["STALE"] = FakeDailyBarSource.Series(250, LastSession.AddDays(-13), 100m);        // no bars for two weeks
        bars["YOUNG"] = FakeDailyBarSource.Series(100, LastSession, 100m);                     // under 200 sessions
        bars["SPY"] = FakeDailyBarSource.Series(250, LastSession, 500m);
    }
}
