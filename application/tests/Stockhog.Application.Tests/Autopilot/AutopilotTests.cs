using Stockhog.Application.Autopilot;
using Stockhog.Application.Common;
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
    public async Task Paper_mode_needs_the_paper_account_and_live_mode_is_not_available()
    {
        await using var host = new ApplicationTestHost();

        host.Environment.Mode = TradingMode.Live;
        var onLive = await host.Dispatcher.SendAsync(new SetAutopilotModeCommand(AutopilotMode.Paper), Ct);
        host.Environment.Mode = TradingMode.Paper;
        var live = await host.Dispatcher.SendAsync(new SetAutopilotModeCommand(AutopilotMode.Live), Ct);
        var shadow = await host.Dispatcher.SendAsync(new SetAutopilotModeCommand(AutopilotMode.Shadow), Ct);
        var paper = await host.Dispatcher.SendAsync(new SetAutopilotModeCommand(AutopilotMode.Paper), Ct);

        Assert.Equal(("Autopilot.NotPaperAccount", "Autopilot.ModeNotAvailable"), (onLive.Error?.Code, live.Error?.Code));
        Assert.Equal((AutopilotMode.Shadow, AutopilotMode.Paper), (shadow.Value, paper.Value));
        Assert.Equal(AutopilotMode.Paper, host.Autopilot.Autopilot.Mode);
        Assert.Contains(host.AuditLog.Entries, e => e.Category == AuditCategory.Autopilot && e.Details.Contains("Shadow -> Paper"));
    }

    [Fact]
    public async Task A_paper_entry_buys_at_market_with_a_stop_at_the_broker_and_takes_in_the_fill_next_cycle()
    {
        await using var host = Paper(out var spy, ("SPY", 200m));

        var bought = await Apply(host, (spy, ProposalScans.Entry(200m, 190m)));

        var expected = PositionSizer.Size(100_000m, 50_000m, 200m, 190m, SizingRules.Default, host.RiskLimits.Current).Quantity;
        var order = Assert.Single(host.Database.Orders);
        Assert.Equal((OrderSide.Buy, OrderType.Market, expected, 190m), (order.Side, order.Type, order.Quantity, order.StopLossPrice));
        var assignment = host.Autopilot.Autopilot.Assignments[0];
        Assert.Equal((order.Id.Value, AutopilotMode.Paper), (assignment.EntryOrderId, assignment.PositionMode));
        var decision = Assert.Single(host.Autopilot.Decisions);
        Assert.Equal((AutopilotAction.Buy, true, order.Id.Value), (decision.Action, decision.Executed, decision.OrderId));
        Assert.Equal(1, bought.Value.Buys);
        Assert.Contains(host.AuditLog.Entries, e => e.Action == "Cycle" && e.Details.Contains("1 orders were sent to the paper account"));

        order.ApplyFill(expected, 201.5m, TestData.Now);
        host.Broker.Positions.Add(new BrokerPosition("SPY", expected, 201.5m, 202m, expected * 202m, 0, 0));
        var next = await Apply(host, (spy, ProposalScans.Signal(SignalKind.Holding, 202m, 190m)));

        Assert.Equal(1, next.Value.Holds);
        Assert.Equal((expected, 201.5m, false), (assignment.Quantity, assignment.EntryPrice, assignment.IsEntryPending));
    }

    [Fact]
    public async Task A_paper_exit_cancels_the_stop_leg_first_then_sells_at_market()
    {
        await using var host = Paper(out var spy, ("SPY", 210m));
        Held(host, spy, 10, 200m);
        host.Broker.Legs.Add(new BrokerOrder("leg-1", null, "SPY", OrderStatus.Submitted, 10, 0, null, TestData.Now, Side: OrderSide.Sell));
        host.Broker.FillMarketOrdersImmediately = true;
        host.Broker.FillPrice = 210m;

        var cycle = await Apply(host, (spy, ProposalScans.Signal(SignalKind.Exit, 210m, null)));

        Assert.Equal(1, cycle.Value.Sells);
        Assert.Equal(["leg-1"], host.Broker.Cancelled);
        var sell = Assert.Single(host.Database.Orders);
        Assert.Equal((OrderSide.Sell, OrderType.Market, 10m), (sell.Side, sell.Type, sell.Quantity));
        var assignment = host.Autopilot.Autopilot.Assignments[0];
        Assert.Equal((0m, 100m, 1), (assignment.Quantity, assignment.RealizedProfit, assignment.WinningTrades));
        Assert.Equal((true, 210m), (host.Autopilot.Decisions[0].Executed, host.Autopilot.Decisions[0].Price));
        var notice = Assert.Single(host.Notifier.Notices, n => n.Topic == Application.Notifications.NoticeTopic.AutopilotTraded);
        Assert.Equal([0, 1], notice.Args);
    }

    [Fact]
    public async Task A_paper_position_the_account_no_longer_holds_was_stopped_out_at_the_broker()
    {
        await using var host = Paper(out var spy, ("SPY", 185m));
        Held(host, spy, 10, 200m);
        host.Broker.Positions.Clear();

        var cycle = await Apply(host, (spy, ProposalScans.Entry(185m, 175m)));

        Assert.Equal((1, 0), (cycle.Value.Sells, cycle.Value.Buys));   // not bought again in the same cycle
        var decision = Assert.Single(host.Autopilot.Decisions);
        Assert.Equal((AutopilotAction.Sell, false, 190m, -100m), (decision.Action, decision.Executed, decision.Price, decision.Profit));
        Assert.Contains("stop at the broker", decision.Reason);
        Assert.Empty(host.Database.Orders);
    }

    [Fact]
    public async Task A_paper_entry_whose_order_ended_without_a_fill_is_dropped()
    {
        await using var host = Paper(out var spy, ("SPY", 200m));
        var order = Order.Create(ClientOrderId.New(TestData.Now), Symbol.Create("SPY"), OrderSide.Buy, OrderType.Market, 10, null, null,
            TimeInForce.Day, TestData.Now, 190m);
        order.Cancel(TestData.Now);
        host.Database.Add(order);
        host.Autopilot.Autopilot.Enter(spy, 10, 200m, 190m, TestData.Now, order.Id.Value);

        var cycle = await Apply(host, (spy, ProposalScans.Entry(200m, 190m)));

        Assert.Equal((0, 1), (cycle.Value.Buys, cycle.Value.Skips));
        var assignment = host.Autopilot.Autopilot.Assignments[0];
        Assert.Equal((0m, 0), (assignment.Quantity, assignment.ClosedTrades));
        Assert.Contains("ended Cancelled without a fill", host.Autopilot.Decisions[0].Reason);
        Assert.Single(host.Database.Orders);
    }

    [Fact]
    public async Task A_refused_paper_sell_keeps_the_position_and_puts_its_stop_back()
    {
        await using var host = Paper(out var spy, ("SPY", 210m));
        Held(host, spy, 10, 200m);
        host.Broker.SubmitBehavior = order => order is { Side: OrderSide.Sell, Type: OrderType.Market }
            ? throw new BrokerException("insufficient qty available", isRejection: true)
            : null;

        var cycle = await Apply(host, (spy, ProposalScans.Signal(SignalKind.Exit, 210m, null)));

        Assert.Equal((0, 1), (cycle.Value.Sells, cycle.Value.Skips));
        Assert.Equal(10m, host.Autopilot.Autopilot.Assignments[0].Quantity);
        Assert.Contains("The stop at 190.00 was placed again", host.Autopilot.Decisions[0].Reason);
        var stop = Assert.Single(host.Database.Orders, o => o.Type == OrderType.Stop);
        Assert.Equal((OrderSide.Sell, 10m, 190m, TimeInForce.Gtc), (stop.Side, stop.Quantity, stop.StopPrice, stop.TimeInForce));
    }

    [Fact]
    public async Task A_paper_cycle_is_refused_once_the_app_runs_against_the_live_account()
    {
        await using var host = Paper(out var spy, ("SPY", 200m));
        host.Environment.Mode = TradingMode.Live;

        var cycle = await Apply(host, (spy, ProposalScans.Entry(200m, 190m)));

        Assert.Equal("Autopilot.NotPaperAccount", cycle.Error?.Code);
        Assert.Empty(host.Database.Orders);
    }

    [Fact]
    public async Task Paper_trading_is_switched_off_after_a_restart()
    {
        await using var host = Paper(out var spy, ("SPY", 200m));
        Held(host, spy, 10, 200m);

        var suspended = await host.Dispatcher.SendAsync(new SuspendAutopilotAfterRestartCommand(), Ct);
        var again = await host.Dispatcher.SendAsync(new SuspendAutopilotAfterRestartCommand(), Ct);

        Assert.Equal((true, false), (suspended.Value, again.Value));
        Assert.Equal(AutopilotMode.Off, host.Autopilot.Autopilot.Mode);
        Assert.Contains(host.AuditLog.Entries, e => e.Action == "Suspended" && e.Details.Contains("1 open positions keep their stops"));
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

    [Fact]
    public async Task No_cycle_is_scheduled_on_an_exchange_holiday_and_paper_orders_wait_for_the_next_session()
    {
        await using var host = Paper(out var spy, ("SPY", 200m));
        host.Calendar.Sessions = [Regular(new DateOnly(2026, 10, 14))];   // the 15th is a holiday

        var scheduled = await host.Dispatcher.SendAsync(new QueueAutopilotCycleCommand(Scheduled: true), Ct);
        var manual = await host.Dispatcher.SendAsync(new QueueAutopilotCycleCommand(), Ct);
        var applied = await Apply(host, (spy, ProposalScans.Entry(200m, 190m)));

        Assert.Null(scheduled.Value.JobId);
        Assert.Equal(("Autopilot.MarketClosed", "Autopilot.MarketClosed"), (manual.Error?.Code, applied.Error?.Code));
        Assert.Contains("closed today (2026-10-15)", manual.Error!.Message);
        Assert.Empty(host.Database.Orders);
    }

    [Fact]
    public async Task An_early_close_ends_the_cycle_window_early()
    {
        await using var host = Shadow(out _, ("SPY", "SmaCrossStrategy", 3m));
        host.Calendar.Sessions = [Regular(new DateOnly(2026, 10, 14)), new MarketSession(new DateOnly(2026, 10, 15), new TimeOnly(9, 30), new TimeOnly(13, 0))];

        host.Clock.Advance(TimeSpan.FromHours(4.9));   // 12:54 in New York
        var before = await host.Dispatcher.SendAsync(new QueueAutopilotCycleCommand(Scheduled: true), Ct);
        host.Jobs.Pending.Clear();
        host.Clock.Advance(TimeSpan.FromMinutes(2));   // 12:56: within 5 minutes of the 13:00 close
        var after = await host.Dispatcher.SendAsync(new QueueAutopilotCycleCommand(Scheduled: true), Ct);

        Assert.NotNull(before.Value.JobId);
        Assert.Null(after.Value.JobId);
    }

    [Fact]
    public async Task After_holidays_the_last_session_before_them_counts_as_fresh_data()
    {
        await using var host = Shadow(out var spy, ("SPY", "RsiPullbackStrategy", 3m));
        host.Calendar.Sessions = [Regular(new DateOnly(2026, 10, 12)), Regular(new DateOnly(2026, 10, 15))];   // 13th and 14th closed

        var cycle = await Apply(host, (spy, ProposalScans.Signal(SignalKind.Entry, 200m, 190m, asOf: new DateOnly(2026, 10, 12))));

        Assert.Equal(1, cycle.Value.Buys);
    }

    private static MarketSession Regular(DateOnly date) => new(date, new TimeOnly(9, 30), new TimeOnly(16, 0));

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

    /// <summary>One RSI pullback pair per symbol, priced by the quote cache so orders have a reference price.</summary>
    private static ApplicationTestHost Paper(out int firstId, params (string Symbol, decimal Price)[] pairs)
    {
        var host = new ApplicationTestHost();
        host.Clock.Advance(TimeSpan.FromHours(2));   // 10:00 in New York: paper orders need the market open
        host.LatestPrices.Price = null;
        host.Autopilot.Autopilot.SetMode(AutopilotMode.Paper);
        foreach (var (symbol, price) in pairs)
        {
            host.Autopilot.Autopilot.Add(Symbol.Create(symbol), StrategyName.Create("RsiPullbackStrategy"), 3m, TestData.Now);
            host.Quotes.Quotes[symbol] = new Quote(symbol, null, null, price, TestData.Now);
        }

        firstId = host.Autopilot.Autopilot.Assignments[0].Id;
        return host;
    }

    /// <summary>A filled paper position with its stop 10 below the entry, held on the account.</summary>
    private static void Held(ApplicationTestHost host, int id, decimal quantity, decimal entry)
    {
        host.Autopilot.Autopilot.Enter(id, quantity, entry, entry - 10m, TestData.Now.AddDays(-5), "ord-entry");
        host.Autopilot.Autopilot.ConfirmEntry(id, quantity, entry);
        var symbol = host.Autopilot.Autopilot.Assignments.Single(a => a.Id == id).Symbol.Value;
        host.Broker.Positions.Add(new BrokerPosition(symbol, quantity, entry, entry, quantity * entry, 0, 0));
    }

    private static Task<Abstractions.Results.Result<AutopilotCycleSummary>> Apply(ApplicationTestHost host, params (int Id, StrategySignal? Signal)[] signals) =>
        host.Dispatcher.SendAsync(
            new ApplyAutopilotCycleCommand(Session, signals.Select(s => new AssignmentSignal(s.Id, s.Signal, s.Signal is null ? "no state" : null)).ToList()),
            Ct);
}
