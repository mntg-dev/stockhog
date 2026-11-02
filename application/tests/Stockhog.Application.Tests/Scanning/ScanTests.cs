using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.Scanning;
using Stockhog.Application.Scanning.StartScan;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.Scanning;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Tests.Scanning;

public class ScanTests
{
    private const string Good = "SmaCrossStrategy";
    private const string Uneven = "RsiPullbackStrategy";

    private static readonly DateOnly Start = new(2024, 10, 14);
    private static readonly DateOnly End = new(2026, 10, 14);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Evaluates_every_pair_and_fails_the_symbols_without_data()
    {
        await using var host = CreateHost();

        var started = await host.Dispatcher.SendAsync(Command(["AAPL", "NODATA"]), Ct);
        Assert.True(started.IsSuccess, started.Error?.Message);
        Assert.Equal(4, started.Value.Candidates);

        var outcome = Assert.Single(await host.Jobs.RunAllAsync(host.Dispatcher));
        Assert.True(outcome.IsSuccess, outcome.Message);
        Assert.Equal("1 of 2 evaluated candidates passed (4 in total).", outcome.Message);

        var details = (await host.Dispatcher.SendAsync(new GetScanQuery(started.Value.ScanId), Ct)).Value;
        Assert.Equal(ScanStatus.Completed, details.Summary.Status);
        Assert.Equal(
            ["AAPL/SmaCrossStrategy", "AAPL/RsiPullbackStrategy", "NODATA/SmaCrossStrategy", "NODATA/RsiPullbackStrategy"],
            details.Candidates.Select(c => $"{c.Symbol}/{c.StrategyName}"));

        var passed = details.Candidates[0];
        Assert.True(passed.Passed);
        Assert.Equal((CandidateStatus.Evaluated, ScanRejection.None), (passed.Status, passed.Rejections));
        Assert.Equal((20m, 10m, 9.09m), (passed.NetProfitPercent, passed.FirstHalfReturnPercent, passed.SecondHalfReturnPercent));
        Assert.Equal(8, passed.TotalTrades);
        Assert.NotNull(passed.BuyAndHoldReturnPercent);
        Assert.NotNull(passed.RunId);

        var uneven = details.Candidates[1];
        Assert.Equal(ScanRejection.LosingHalf, uneven.Rejections);
        Assert.Equal(-5m, uneven.FirstHalfReturnPercent);

        Assert.All(details.Candidates.Skip(2), c =>
        {
            Assert.Equal(CandidateStatus.Failed, c.Status);
            Assert.StartsWith("No market data", c.FailureReason);
        });

        // Minute data for both symbols over the period; backtests only where it came back.
        Assert.Equal(["AAPL", "NODATA"], host.Downloader.Requests.Select(r => r.Symbol.Value).Order());
        Assert.Equal(2, host.Engine.Requests.Count);
        Assert.All(host.Engine.Requests, r => Assert.Equal("AAPL", r.Symbol.Value));
    }

    [Fact]
    public async Task Without_daily_bars_the_scan_goes_on_without_buy_and_hold()
    {
        await using var host = CreateHost();
        host.DailyBars.IsConfigured = false;

        var started = await host.Dispatcher.SendAsync(Command(["AAPL"]), Ct);
        await host.Jobs.RunAllAsync(host.Dispatcher);

        var details = (await host.Dispatcher.SendAsync(new GetScanQuery(started.Value.ScanId), Ct)).Value;
        Assert.Equal(ScanStatus.Completed, details.Summary.Status);
        Assert.All(details.Candidates, c => Assert.Null(c.BuyAndHoldReturnPercent));
    }

    public static TheoryData<StartScanCommand> InvalidCommands => new()
    {
        Command(["AAPL"]) with { Strategies = ["NoSuchStrategy"] },
        Command(["AAPL"]) with { Strategies = [] },
        Command([]),
        Command(Enumerable.Range(0, ScanRun.MaxSymbols + 1).Select(i => $"S{i}").ToList()),
        Command(["AAPL"]) with { End = Start.AddDays(30) },          // too short to split into halves
        Command(["AAPL"]) with { End = new DateOnly(2026, 12, 1) },  // in the future
        Command(["AAPL"]) with { MaxParallel = 0 },
        Command(["AAPL"]) with { MaxDrawdownPercent = 0 }
    };

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public async Task Invalid_scans_are_rejected_before_anything_is_queued(StartScanCommand command)
    {
        await using var host = CreateHost();

        var result = await host.Dispatcher.SendAsync(command, Ct);

        Assert.Equal(ErrorType.Validation, result.Error?.Type);
        Assert.Empty(host.Jobs.Pending);
        Assert.Empty(host.Scans.Scans);
    }

    [Fact]
    public async Task Scans_left_unfinished_by_a_previous_session_are_failed_at_startup()
    {
        await using var host = CreateHost();
        await host.Dispatcher.SendAsync(Command(["AAPL"]), Ct);

        var failed = await host.Dispatcher.SendAsync(new FailInterruptedScansCommand(), Ct);
        var scans = await host.Dispatcher.SendAsync(new GetScansQuery(), Ct);

        Assert.Equal(1, failed.Value);
        Assert.Equal(ScanStatus.Failed, Assert.Single(scans.Value).Status);
    }

    [Fact]
    public void Monte_carlo_drawdown_is_never_shallower_than_the_run_itself()
    {
        var metrics = new PerformanceMetrics(10m, 30m, 1m, 2, 50m, 1.5m);
        var trades = new[] { Trade(1, 5_000m), Trade(2, -2_000m) };

        Assert.Equal(30m, ScanEvaluator.MonteCarloDrawdown(trades, metrics, 100_000m));
        Assert.Equal(30m, ScanEvaluator.MonteCarloDrawdown([], metrics, 100_000m));
    }

    private static ApplicationTestHost CreateHost()
    {
        var host = new ApplicationTestHost();
        host.Strategies.Extra.Add(new StrategyDescriptor(Uneven, "RSI pullback", null, []));
        host.Downloader.BarsFor = request => request.Symbol.Value == "NODATA" ? 0 : 1_000;
        host.Reader.ReportFor = runId => runId.Contains(Good, StringComparison.Ordinal)
            ? Report(runId, middle: 110_000m, end: 120_000m)
            : Report(runId, middle: 95_000m, end: 104_000m);
        host.DailyBars.Bars["AAPL"] = FakeDailyBarSource.Series(500, End, 100m, dailyChange: 0.1m);
        return host;
    }

    private static StartScanCommand Command(IReadOnlyList<string> symbols) =>
        new(symbols, [Good, Uneven], Start, End, 100_000m, MinTrades: 6, MaxDrawdownPercent: 25m, RequireBothHalvesProfitable: true, MaxParallel: 2);

    /// <summary>Equity from 100,000 through <paramref name="middle"/> halfway to <paramref name="end"/>, with eight trades.</summary>
    private static BacktestReport Report(string runId, decimal middle, decimal end)
    {
        var halfway = Start.AddDays(DateRange.Create(Start, End).Days / 2).ToDateTime(TimeOnly.MinValue);
        var equity = new List<EquityPoint>
        {
            new(Start.ToDateTime(TimeOnly.MinValue), 100_000m),
            new(halfway.AddDays(-1), (100_000m + middle) / 2),
            new(halfway, middle),
            new(End.ToDateTime(TimeOnly.MinValue), end)
        };
        var trades = Enumerable.Range(1, 8).Select(i => Trade(i, i == 4 ? -1_000m : 3_000m)).ToList();
        return new BacktestReport(runId, new Dictionary<string, string>(), new TradeStatistics(trades.Count, 3m, 3, 1), equity, trades);
    }

    private static ClosedTrade Trade(int number, decimal profit) =>
        new(number, "AAPL", TradeDirection.Long, 10m, new DateTime(2025, 1, number), 100m, new DateTime(2025, 1, number + 1), 100m + profit / 10m, "Exit", profit, 0m);
}
