using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.Scanning;
using Stockhog.Application.Scanning.CheckSignals;
using Stockhog.Application.Scanning.StartScan;
using Stockhog.Application.Signals;
using Stockhog.Application.Signals.EvaluateSignal;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.Signals;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Tests.Scanning;

public class SignalTests
{
    // TestData.Now is 2026-10-15 08:00 in New York, so the last completed session is the 14th.
    private static readonly DateOnly LastSession = new(2026, 10, 14);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Runs_the_strategy_up_to_today_and_reads_the_position_it_ended_with()
    {
        await using var host = new ApplicationTestHost();
        host.States.StateFor = _ => Holding(entered: new DateTime(2026, 10, 14, 9, 31, 0));

        var signal = await host.Dispatcher.SendAsync(new EvaluateSignalCommand("SmaCrossStrategy", "AAPL"), Ct);

        Assert.True(signal.IsSuccess, signal.Error?.Message);
        Assert.Equal(SignalKind.Entry, signal.Value.Kind);
        Assert.Equal((LastSession, 0), (signal.Value.AsOf, signal.Value.SessionsSinceEntry));
        Assert.Equal((200m, 194m, 198m), (signal.Value.LastPrice, signal.Value.StopPrice, signal.Value.EntryPrice));

        // Minute data with room for the warm-up, then a run of a year that ends the day after the last session.
        var download = Assert.Single(host.Downloader.Requests);
        Assert.Equal(DateRange.Create(LastSession.AddDays(-365 - EvaluateSignalCommand.WarmUpDays), LastSession), download.Period);
        var run = Assert.Single(host.Engine.Requests);
        Assert.Equal(DateRange.Create(LastSession.AddDays(-365), LastSession.AddDays(1)), run.Period);
        Assert.Equal(run.RunId.Value, signal.Value.RunId);
    }

    [Fact]
    public async Task A_run_without_a_stored_state_gives_no_signal()
    {
        await using var host = new ApplicationTestHost();

        var signal = await host.Dispatcher.SendAsync(new EvaluateSignalCommand("SmaCrossStrategy", "AAPL"), Ct);

        Assert.Equal("Signal.NoState", signal.Error?.Code);
    }

    [Fact]
    public async Task Checks_the_signals_of_the_passed_candidates_and_stores_them_on_the_scan()
    {
        await using var host = new ApplicationTestHost();
        host.Strategies.Extra.Add(new StrategyDescriptor("RsiPullbackStrategy", "RSI pullback", null, []));
        host.Reader.ReportFor = runId => runId.Contains("SmaCross", StringComparison.Ordinal) ? Report(runId, 110_000m, 120_000m) : Report(runId, 95_000m, 104_000m);
        host.States.StateFor = _ => Holding(entered: new DateTime(2026, 10, 1, 9, 31, 0));

        var started = await host.Dispatcher.SendAsync(
            new StartScanCommand(["AAPL"], ["SmaCrossStrategy", "RsiPullbackStrategy"], LastSession.AddYears(-2), LastSession, 100_000m, 6, 25m, true, 2), Ct);
        await host.Jobs.RunAllAsync(host.Dispatcher);

        var queued = await host.Dispatcher.SendAsync(new CheckScanSignalsCommand(started.Value.ScanId), Ct);
        Assert.Equal(1, queued.Value.Candidates);    // only the pair that passed
        var outcome = Assert.Single(await host.Jobs.RunAllAsync(host.Dispatcher));
        Assert.True(outcome.IsSuccess, outcome.Message);
        Assert.Equal("0 entries, 0 exits, 1 holding, 0 flat.", outcome.Message);

        var details = (await host.Dispatcher.SendAsync(new GetScanQuery(started.Value.ScanId), Ct)).Value;
        Assert.Equal(SignalKind.Holding, details.Candidates[0].Signal?.Kind);
        Assert.Equal(TestData.Now, details.Candidates[0].SignalCheckedAt);
        Assert.Null(details.Candidates[1].Signal);
    }

    [Fact]
    public async Task A_scan_without_evaluated_candidates_has_nothing_to_check()
    {
        await using var host = new ApplicationTestHost();
        var started = await host.Dispatcher.SendAsync(
            new StartScanCommand(["AAPL"], ["SmaCrossStrategy"], LastSession.AddYears(-2), LastSession, 100_000m, 6, 25m, true, 2), Ct);

        var queued = await host.Dispatcher.SendAsync(new CheckScanSignalsCommand(started.Value.ScanId), Ct);

        Assert.Equal("Scan.NothingToCheck", queued.Error?.Code);
    }

    private static StrategyEndState Holding(DateTime entered) =>
        new(new DateTime(2026, 10, 15), new DateTime(2026, 10, 14, 16, 0, 0), 200m, 50m, 198m, entered, "Entry", 194m, null, null);

    private static BacktestReport Report(string runId, decimal middle, decimal end)
    {
        var start = LastSession.AddYears(-2);
        var halfway = start.AddDays(DateRange.Create(start, LastSession).Days / 2).ToDateTime(TimeOnly.MinValue);
        var equity = new List<EquityPoint>
        {
            new(start.ToDateTime(TimeOnly.MinValue), 100_000m),
            new(halfway, middle),
            new(LastSession.ToDateTime(TimeOnly.MinValue), end)
        };
        var trades = Enumerable.Range(1, 8)
            .Select(i => new ClosedTrade(i, "AAPL", TradeDirection.Long, 10m, new DateTime(2025, 1, i), 100m, new DateTime(2025, 1, i + 1), 110m, "Exit", i == 4 ? -1_000m : 3_000m, 0m))
            .ToList();
        return new BacktestReport(runId, new Dictionary<string, string>(), new TradeStatistics(trades.Count, 3m, 3, 1), equity, trades);
    }
}
