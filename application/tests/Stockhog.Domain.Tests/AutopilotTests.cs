using Stockhog.Domain.Autopilot;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Domain.Tests;

public class AutopilotTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 14, 0, 0, TimeSpan.Zero);
    private static readonly StrategyName Rsi = StrategyName.Create("RsiPullbackStrategy");

    [Fact]
    public void A_new_autopilot_is_off_and_cannot_be_switched_to_live_yet()
    {
        var autopilot = Autopilot.Autopilot.Create();

        Assert.Equal(AutopilotMode.Off, autopilot.Mode);
        Assert.Throws<DomainException>(() => autopilot.SetMode(AutopilotMode.Live));
        autopilot.SetMode(AutopilotMode.Shadow);
        Assert.Equal(AutopilotMode.Shadow, autopilot.Mode);
    }

    [Fact]
    public void Switching_to_paper_forgets_the_simulated_positions()
    {
        var autopilot = Autopilot.Autopilot.Create();
        autopilot.SetMode(AutopilotMode.Shadow);
        var id = autopilot.Add(Symbol.Create("SPY"), Rsi, 2m, T0)!.Id;
        autopilot.Enter(id, 10, 200m, 190m, T0);

        var discarded = autopilot.SetMode(AutopilotMode.Paper);

        var a = autopilot.Assignments[0];
        Assert.Equal((1, 0m, 0, (AutopilotMode?)null), (discarded, a.Quantity, a.ClosedTrades, a.PositionMode));
    }

    [Fact]
    public void Paper_positions_wait_for_their_fill_and_keep_the_autopilot_out_of_shadow_mode()
    {
        var autopilot = Autopilot.Autopilot.Create();
        autopilot.SetMode(AutopilotMode.Paper);
        var id = autopilot.Add(Symbol.Create("SPY"), Rsi, 2m, T0)!.Id;

        Assert.Throws<DomainException>(() => autopilot.Enter(id, 10, 200m, 190m, T0));   // a paper entry needs its order
        autopilot.Enter(id, 10, 200m, 190m, T0, "ord-1");
        Assert.True(autopilot.Assignments[0].IsEntryPending);

        autopilot.ConfirmEntry(id, 10, 201.5m);
        var a = autopilot.Assignments[0];
        Assert.Equal((10m, 201.5m, AutopilotMode.Paper, false), (a.Quantity, a.EntryPrice, a.PositionMode, a.IsEntryPending));

        Assert.Throws<DomainException>(() => autopilot.SetMode(AutopilotMode.Shadow));
        autopilot.SetMode(AutopilotMode.Off);
        Assert.Equal(1, autopilot.PaperPositions);
    }

    [Fact]
    public void An_entry_that_never_filled_is_dropped_without_counting_a_trade()
    {
        var autopilot = Autopilot.Autopilot.Create();
        autopilot.SetMode(AutopilotMode.Paper);
        var id = autopilot.Add(Symbol.Create("SPY"), Rsi, 2m, T0)!.Id;
        autopilot.Enter(id, 10, 200m, 190m, T0, "ord-1");

        autopilot.DiscardEntry(id);

        var a = autopilot.Assignments[0];
        Assert.Equal((0m, 0, (string?)null), (a.Quantity, a.ClosedTrades, a.EntryOrderId));
    }

    [Theory]
    [InlineData(AutopilotMode.Paper, true)]
    [InlineData(AutopilotMode.Shadow, false)]
    [InlineData(AutopilotMode.Off, false)]
    public void Only_paper_trading_is_switched_off_by_a_restart(AutopilotMode mode, bool suspended)
    {
        var autopilot = Autopilot.Autopilot.Create();
        autopilot.SetMode(mode);

        Assert.Equal(suspended, autopilot.SuspendAfterRestart());
        Assert.Equal(suspended ? AutopilotMode.Off : mode, autopilot.Mode);
    }

    [Fact]
    public void A_pair_is_followed_once_and_gets_the_next_id()
    {
        var autopilot = Autopilot.Autopilot.Create();

        var spy = autopilot.Add(Symbol.Create("SPY"), Rsi, 2m, T0);
        var duplicate = autopilot.Add(Symbol.Create("SPY"), Rsi, 3m, T0);
        var aapl = autopilot.Add(Symbol.Create("AAPL"), Rsi, null, T0);

        Assert.Equal((1, 2), (spy!.Id, aapl!.Id));
        Assert.Null(duplicate);
        Assert.Equal(2, autopilot.Assignments.Count);
    }

    [Fact]
    public void A_round_trip_books_its_profit_and_frees_the_capital()
    {
        var autopilot = Autopilot.Autopilot.Create();
        var id = autopilot.Add(Symbol.Create("SPY"), Rsi, 2m, T0)!.Id;

        autopilot.Enter(id, 10, 200m, 190m, T0);
        var inUse = autopilot.CapitalInUse;
        var profit = autopilot.Exit(id, 212.5m);

        var a = autopilot.Assignments[0];
        Assert.Equal((2_000m, 125m), (inUse, profit));
        Assert.Equal((0m, 125m, 1, 1, 0, 0m), (a.Quantity, a.RealizedProfit, a.ClosedTrades, a.WinningTrades, a.ConsecutiveLosses, autopilot.CapitalInUse));
        Assert.Null(a.EntryPrice);
    }

    [Fact]
    public void Losses_in_a_row_pause_the_pair_until_it_is_resumed()
    {
        var autopilot = Autopilot.Autopilot.Create();
        autopilot.Configure(2, 50m);
        var id = autopilot.Add(Symbol.Create("SPY"), Rsi, 2m, T0)!.Id;

        autopilot.Enter(id, 10, 200m, 190m, T0);
        autopilot.Exit(id, 195m);
        Assert.True(autopilot.Assignments[0].IsActive);
        autopilot.Enter(id, 10, 200m, 190m, T0);
        autopilot.Exit(id, 199m);

        Assert.False(autopilot.Assignments[0].IsActive);
        Assert.Equal("2 losing trades in a row.", autopilot.Assignments[0].PausedReason);

        autopilot.Resume(id);
        Assert.Equal((true, 0), (autopilot.Assignments[0].IsActive, autopilot.Assignments[0].ConsecutiveLosses));
    }

    [Fact]
    public void A_pair_holding_a_position_cannot_be_removed()
    {
        var autopilot = Autopilot.Autopilot.Create();
        var id = autopilot.Add(Symbol.Create("SPY"), Rsi, 2m, T0)!.Id;
        autopilot.Enter(id, 10, 200m, 190m, T0);

        Assert.Throws<DomainException>(() => autopilot.Remove(id));
        Assert.Throws<DomainException>(() => autopilot.Enter(id, 5, 200m, 190m, T0));

        autopilot.Exit(id, 200m);
        autopilot.Remove(id);
        Assert.Empty(autopilot.Assignments);
    }

    [Theory]
    [InlineData(10, 200, 200)]   // stop not below the price
    [InlineData(0, 200, 190)]
    [InlineData(10, 0, 190)]
    public void An_entry_needs_a_quantity_a_price_and_a_stop_below_it(decimal quantity, decimal price, decimal stop)
    {
        var autopilot = Autopilot.Autopilot.Create();
        var id = autopilot.Add(Symbol.Create("SPY"), Rsi, 2m, T0)!.Id;

        Assert.Throws<DomainException>(() => autopilot.Enter(id, quantity, price, stop, T0));
    }
}
