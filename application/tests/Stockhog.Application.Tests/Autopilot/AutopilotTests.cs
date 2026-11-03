using Stockhog.Application.Autopilot;
using Stockhog.Application.Signals;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.Autopilot;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Proposals;
using Stockhog.Domain.Signals;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Tests.Autopilot;

public class AutopilotTests
{
    // TestData.Now is 2026-10-15 08:00 in New York: the cycle's session is the 15th and its signals are of the 14th.
    private static readonly DateOnly Session = new(2026, 10, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_off_and_shadow_can_be_chosen_for_now()
    {
        await using var host = new ApplicationTestHost();

        var paper = await host.Dispatcher.SendAsync(new SetAutopilotModeCommand(AutopilotMode.Paper), Ct);
        var live = await host.Dispatcher.SendAsync(new SetAutopilotModeCommand(AutopilotMode.Live), Ct);
        var shadow = await host.Dispatcher.SendAsync(new SetAutopilotModeCommand(AutopilotMode.Shadow), Ct);

        Assert.Equal(("Autopilot.ModeNotAvailable", "Autopilot.ModeNotAvailable"), (paper.Error?.Code, live.Error?.Code));
        Assert.Equal(AutopilotMode.Shadow, shadow.Value);
        Assert.Equal(AutopilotMode.Shadow, host.Autopilot.Autopilot.Mode);
        Assert.Contains(host.AuditLog.Entries, e => e.Category == AuditCategory.Autopilot && e.Details.Contains("Off -> Shadow"));
    }

    [Fact]
    public async Task Follows_the_passed_pairs_of_a_scan_except_the_intraday_strategy()
    {
        await using var host = new ApplicationTestHost();
        var scan = ProposalScans.Given(host,
            ("SPY", "RsiPullbackStrategy", 3.0m, ProposalScans.Entry(200m, 190m)),
            ("AAPL", "DonchianBreakoutStrategy", 2.0m, ProposalScans.Entry(150m, 140m)),
            ("MSFT", "OpeningRangeBreakoutStrategy", 4.0m, ProposalScans.Entry(400m, 390m)));

        var first = await host.Dispatcher.SendAsync(new AddToAutopilotCommand(scan.Id.Value), Ct);
        var again = await host.Dispatcher.SendAsync(new AddToAutopilotCommand(scan.Id.Value), Ct);

        Assert.Equal(new AddedToAutopilot(2, 0, 1), first.Value);
        Assert.Equal(new AddedToAutopilot(0, 2, 1), again.Value);
        Assert.Equal(["SPY", "AAPL"], host.Autopilot.Autopilot.Assignments.Select(a => a.Symbol.Value));
        Assert.Equal(3.0m, host.Autopilot.Autopilot.Assignments[0].Score);
    }

    [Fact]
    public async Task A_shadow_entry_opens_a_simulated_position_sized_by_risk_and_sends_no_order()
    {
        await using var host = Shadow(out var spy, ("SPY", "RsiPullbackStrategy", 3m));

        var cycle = await Apply(host, (spy, ProposalScans.Entry(200m, 190m)));

        Assert.True(cycle.IsSuccess, cycle.Error?.Message);
        Assert.Equal(new AutopilotCycleSummary(AutopilotMode.Shadow, 1, 0, 0, 0, 0), cycle.Value);

        var expected = PositionSizer.Size(100_000m, 50_000m, 200m, 190m, SizingRules.Default, host.RiskLimits.Current);
        var assignment = host.Autopilot.Autopilot.Assignments[0];
        Assert.Equal((expected.Quantity, 200m, 190m, TestData.Now), (assignment.Quantity, assignment.EntryPrice, assignment.StopPrice, assignment.EntryAt));

        var decision = Assert.Single(host.Autopilot.Decisions);
        Assert.Equal((AutopilotAction.Buy, AutopilotMode.Shadow, false, expected.Quantity), (decision.Action, decision.Mode, decision.Executed, decision.Quantity));
        Assert.Empty(host.Broker.Orders);
        Assert.Empty(host.Database.Orders);
        Assert.Equal(Session, host.Autopilot.Autopilot.LastCycleSession);
        Assert.Contains(host.AuditLog.Entries, e => e.Action == "Cycle" && e.Details.Contains("no orders were sent"));
    }

    [Fact]
    public async Task An_exit_signal_closes_the_simulated_position_and_too_many_losses_pause_the_pair()
    {
        await using var host = Shadow(out var spy, ("SPY", "RsiPullbackStrategy", 3m));
        host.Autopilot.Autopilot.Configure(1, 50m);
        host.Autopilot.Autopilot.Enter(spy, 10, 200m, 190m, TestData.Now.AddDays(-5));

        var cycle = await Apply(host, (spy, ProposalScans.Signal(SignalKind.Exit, 195m, null)));

        Assert.Equal(1, cycle.Value.Sells);
        var assignment = host.Autopilot.Autopilot.Assignments[0];
        Assert.Equal((0m, -50m, 1, 0), (assignment.Quantity, assignment.RealizedProfit, assignment.ClosedTrades, assignment.WinningTrades));
        Assert.False(assignment.IsActive);
        var decision = Assert.Single(host.Autopilot.Decisions);
        Assert.Equal((AutopilotAction.Sell, 10m, 195m, -50m), (decision.Action, decision.Quantity, decision.Price, decision.Profit));
        Assert.Contains("Paused", decision.Reason);
    }

    [Fact]
    public async Task A_position_is_kept_while_the_strategy_holds_and_sold_once_the_price_is_below_the_stop()
    {
        await using var host = Shadow(out var spy, ("SPY", "RsiPullbackStrategy", 3m));
        host.Autopilot.Autopilot.Enter(spy, 10, 200m, 190m, TestData.Now.AddDays(-5));

        var kept = await Apply(host, (spy, ProposalScans.Signal(SignalKind.Holding, 205m, 190m)));
        host.Quotes.Quotes["SPY"] = new Quote("SPY", null, null, 189m, TestData.Now);
        var stopped = await Apply(host, (spy, ProposalScans.Signal(SignalKind.Holding, 191m, 190m)));

        Assert.Equal((1, 0), (kept.Value.Holds, kept.Value.Sells));
        Assert.Equal(1, stopped.Value.Sells);
        Assert.Equal((0m, -110m), (host.Autopilot.Autopilot.Assignments[0].Quantity, host.Autopilot.Autopilot.Assignments[0].RealizedProfit));
        Assert.Contains("below the stop", host.Autopilot.Decisions[^1].Reason);
    }

    [Fact]
    public async Task Old_or_missing_signals_are_skipped_and_an_open_position_is_kept()
    {
        await using var host = Shadow(out var spy, ("SPY", "RsiPullbackStrategy", 3m), ("AAPL", "RsiPullbackStrategy", 2m));
        var aapl = host.Autopilot.Autopilot.Assignments[1].Id;
        host.Autopilot.Autopilot.Enter(spy, 10, 200m, 190m, TestData.Now.AddDays(-5));

        var cycle = await Apply(host,
            (spy, ProposalScans.Signal(SignalKind.Exit, 195m, null, asOf: new DateOnly(2026, 10, 12))),
            (aapl, null));

        Assert.Equal(2, cycle.Value.Skips);
        Assert.Equal(10m, host.Autopilot.Autopilot.Assignments[0].Quantity);
        Assert.Contains("The data ends on 2026-10-12", host.Autopilot.Decisions[0].Reason);
        Assert.Contains("could not be read: no state", host.Autopilot.Decisions[1].Reason);
    }

    [Fact]
    public async Task Entries_stop_at_the_autopilot_capital_limit_and_skip_symbols_already_held()
    {
        await using var host = Shadow(out var spy, ("SPY", "RsiPullbackStrategy", 3m), ("AAPL", "RsiPullbackStrategy", 2m), ("NVDA", "RsiPullbackStrategy", 1m));
        host.Autopilot.Autopilot.Configure(3, 0.1m);   // 100 USD of a 100k account
        host.Broker.Positions.Add(new BrokerPosition("NVDA", 10, 180, 200, 2_000, 200, 11));
        var ids = host.Autopilot.Autopilot.Assignments.Select(a => a.Id).ToList();

        var cycle = await Apply(host,
            (ids[0], ProposalScans.Entry(200m, 190m)),
            (ids[1], ProposalScans.Signal(SignalKind.Flat, 150m, null)),
            (ids[2], ProposalScans.Entry(200m, 190m)));

        Assert.Equal((0, 1, 2), (cycle.Value.Buys, cycle.Value.Waits, cycle.Value.Skips));
        var reasons = host.Autopilot.Decisions.ToDictionary(d => d.Symbol, d => d.Reason);
        Assert.Contains("the autopilot's 0.1% of equity", reasons["SPY"]);
        Assert.Equal("No entry signal.", reasons["AAPL"]);
        Assert.Equal("Already held in the account.", reasons["NVDA"]);
    }

    [Fact]
    public async Task A_scheduled_cycle_runs_once_per_session_during_market_hours()
    {
        await using var host = Shadow(out _, ("SPY", "SmaCrossStrategy", 3m));
        host.States.StateFor = _ => new StrategyEndState(new DateTime(2026, 10, 15), new DateTime(2026, 10, 14, 16, 0, 0), 200m, 50m, 198m,
            new DateTime(2026, 10, 14, 9, 31, 0), "Entry", 194m, null, null);

        var early = await host.Dispatcher.SendAsync(new QueueAutopilotCycleCommand(Scheduled: true), Ct);   // 08:00 in New York
        host.Clock.Advance(TimeSpan.FromHours(2));
        var due = await host.Dispatcher.SendAsync(new QueueAutopilotCycleCommand(Scheduled: true), Ct);
        var outcome = Assert.Single(await host.Jobs.RunAllAsync(host.Dispatcher));
        var again = await host.Dispatcher.SendAsync(new QueueAutopilotCycleCommand(Scheduled: true), Ct);

        Assert.Null(early.Value.JobId);
        Assert.NotNull(due.Value.JobId);
        Assert.True(outcome.IsSuccess, outcome.Message);
        Assert.Equal("Shadow cycle: 1 buys, 0 sells, 0 holds, 0 waiting, 0 skipped.", outcome.Message);
        Assert.Null(again.Value.JobId);
        Assert.True(host.Autopilot.Autopilot.Assignments[0].IsHolding);
        Assert.Empty(host.Broker.Orders);
    }

    [Fact]
    public async Task A_manual_cycle_needs_the_autopilot_on_and_something_to_follow()
    {
        await using var host = new ApplicationTestHost();

        var off = await host.Dispatcher.SendAsync(new QueueAutopilotCycleCommand(), Ct);
        host.Autopilot.Autopilot.SetMode(AutopilotMode.Shadow);
        var empty = await host.Dispatcher.SendAsync(new QueueAutopilotCycleCommand(), Ct);

        Assert.Equal(("Autopilot.Off", "Autopilot.NothingToFollow"), (off.Error?.Code, empty.Error?.Code));
    }

    private static ApplicationTestHost Shadow(out int firstId, params (string Symbol, string Strategy, decimal Score)[] pairs)
    {
        var host = new ApplicationTestHost();
        host.LatestPrices.Price = null;   // decide at the signal's close
        host.Autopilot.Autopilot.SetMode(AutopilotMode.Shadow);
        foreach (var (symbol, strategy, score) in pairs)
            host.Autopilot.Autopilot.Add(Symbol.Create(symbol), StrategyName.Create(strategy), score, TestData.Now);
        firstId = host.Autopilot.Autopilot.Assignments[0].Id;
        return host;
    }

    private static Task<Abstractions.Results.Result<AutopilotCycleSummary>> Apply(ApplicationTestHost host, params (int Id, StrategySignal? Signal)[] signals) =>
        host.Dispatcher.SendAsync(
            new ApplyAutopilotCycleCommand(Session, signals.Select(s => new AssignmentSignal(s.Id, s.Signal, s.Signal is null ? "no state" : null)).ToList()),
            Ct);
}
