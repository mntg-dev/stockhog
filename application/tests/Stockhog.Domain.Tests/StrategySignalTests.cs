using Stockhog.Domain.Signals;

namespace Stockhog.Domain.Tests;

public class StrategySignalTests
{
    // A Friday: the latest session when signals are checked over the weekend or on Monday.
    private static readonly DateOnly Friday = new(2026, 10, 30);

    [Theory]
    [InlineData("2026-10-30", 0)]   // same day
    [InlineData("2026-10-29", 1)]   // Thursday
    [InlineData("2026-10-26", 4)]   // Monday
    [InlineData("2026-10-23", 5)]   // the Friday before: the weekend does not count
    [InlineData("2026-11-02", 0)]   // later than the session
    public void Sessions_count_weekdays_after_the_start_up_to_the_end(string from, int expected)
    {
        Assert.Equal(expected, Sessions.Between(DateOnly.Parse(from), Friday));
    }

    [Fact]
    public void A_long_opened_on_the_last_sessions_is_a_fresh_entry()
    {
        var signal = Classify(quantity: 120, entry: new DateTime(2026, 10, 29, 9, 31, 0), stop: 95m);

        Assert.Equal(SignalKind.Entry, signal.Kind);
        Assert.Equal(1, signal.SessionsSinceEntry);
        Assert.Equal((101m, 95m, "RSI dip"), (signal.EntryPrice, signal.StopPrice, signal.EntryTag));
        Assert.Equal(5m, signal.RiskPerShare);      // last price 100 down to the 95 stop
    }

    [Fact]
    public void An_older_long_is_a_holding()
    {
        var signal = Classify(quantity: 120, entry: new DateTime(2026, 10, 20, 9, 31, 0));

        Assert.Equal(SignalKind.Holding, signal.Kind);
        Assert.Equal(8, signal.SessionsSinceEntry);
    }

    [Fact]
    public void A_recent_exit_is_an_exit_and_an_old_one_is_flat()
    {
        var exited = Classify(quantity: 0, exit: new DateTime(2026, 10, 30, 9, 31, 0));
        var flat = Classify(quantity: 0, exit: new DateTime(2026, 10, 1, 9, 31, 0));
        var never = Classify(quantity: 0);

        Assert.Equal((SignalKind.Exit, SignalKind.Flat, SignalKind.Flat), (exited.Kind, flat.Kind, never.Kind));
        Assert.Null(exited.EntryPrice);
        Assert.Null(exited.StopPrice);
    }

    [Fact]
    public void A_short_position_is_reported_as_such()
    {
        Assert.Equal(SignalKind.Short, Classify(quantity: -50, entry: new DateTime(2026, 10, 30, 9, 31, 0)).Kind);
    }

    [Fact]
    public void A_stop_above_the_price_gives_no_risk_per_share()
    {
        Assert.Null(Classify(quantity: 10, entry: new DateTime(2026, 10, 30, 9, 31, 0), stop: 105m).RiskPerShare);
    }

    private static StrategySignal Classify(decimal quantity, DateTime? entry = null, decimal? stop = null, DateTime? exit = null) =>
        StrategySignal.Classify(Friday, 100m, quantity, 101m, entry, "RSI dip", stop, exit, "Close above SMA", "run-1");
}
