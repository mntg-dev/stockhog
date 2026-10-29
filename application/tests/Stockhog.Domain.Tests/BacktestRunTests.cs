using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Domain.Tests;

public class BacktestRunTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_run_is_queued_without_events()
    {
        var run = CreateRun();

        Assert.Equal(BacktestStatus.Queued, run.Status);
        Assert.Equal(0, run.Progress);
        Assert.Empty(run.DomainEvents);
    }

    [Fact]
    public void Completing_a_running_backtest_raises_an_event()
    {
        var run = CreateRun();
        run.Start(T0);
        run.Complete(T0.AddSeconds(5));

        Assert.Equal(BacktestStatus.Completed, run.Status);
        Assert.Equal(100, run.Progress);
        Assert.Equal(TimeSpan.FromSeconds(5), run.Duration);

        var completed = Assert.IsType<BacktestRunCompleted>(Assert.Single(run.DomainEvents));
        Assert.Equal(run.Id, completed.RunId);
        Assert.Equal(Symbol.Create("AAPL"), completed.Symbol);
    }

    [Fact]
    public void Progress_never_decreases_and_is_clamped()
    {
        var run = CreateRun();
        run.Start(T0);

        run.ReportProgress(40);
        run.ReportProgress(20);
        Assert.Equal(40, run.Progress);

        run.ReportProgress(250);
        Assert.Equal(100, run.Progress);
    }

    [Fact]
    public void Progress_is_ignored_unless_running()
    {
        var run = CreateRun();
        run.ReportProgress(50);
        Assert.Equal(0, run.Progress);

        run.Start(T0);
        run.ReportProgress(30);
        run.Cancel(T0.AddSeconds(1));
        run.ReportProgress(90);

        Assert.Equal(30, run.Progress);
    }

    [Fact]
    public void Cancelling_records_progress_at_the_time_of_cancellation()
    {
        var run = CreateRun();
        run.Start(T0);
        run.ReportProgress(43);
        run.Cancel(T0.AddSeconds(1));

        var cancelled = Assert.IsType<BacktestRunCancelled>(Assert.Single(run.DomainEvents));
        Assert.Equal(43, cancelled.Progress);
    }

    [Fact]
    public void A_queued_run_can_fail_before_starting()
    {
        var run = CreateRun();
        run.Fail("  runner not found ", T0);

        Assert.Equal(BacktestStatus.Failed, run.Status);
        Assert.Equal("runner not found", run.FailureReason);
        Assert.Null(run.Duration);
        Assert.IsType<BacktestRunFailed>(Assert.Single(run.DomainEvents));
    }

    [Fact]
    public void Finished_runs_reject_further_transitions()
    {
        var run = CreateRun();
        run.Start(T0);
        run.Complete(T0);

        Assert.Throws<DomainException>(() => run.Cancel(T0));
        Assert.Throws<DomainException>(() => run.Fail("x", T0));
        Assert.Throws<DomainException>(() => run.Complete(T0));
        Assert.Throws<DomainException>(() => run.Start(T0));
    }

    [Fact]
    public void Only_a_running_backtest_can_complete()
    {
        Assert.Throws<DomainException>(() => CreateRun().Complete(T0));
    }

    [Fact]
    public void Initial_capital_must_be_positive()
    {
        Assert.Throws<DomainException>(() => CreateRun(capital: 0));
    }

    [Fact]
    public void Dequeuing_events_clears_them()
    {
        var run = CreateRun();
        run.Start(T0);
        run.Complete(T0);

        Assert.Single(run.DequeueDomainEvents());
        Assert.Empty(run.DomainEvents);
    }

    private static BacktestRun CreateRun(decimal capital = 100_000)
    {
        var symbol = Symbol.Create("AAPL");
        var strategy = StrategyName.Create("SmaCrossStrategy");
        return BacktestRun.Create(
            BacktestRunId.New(symbol, strategy, T0),
            symbol,
            strategy,
            DateRange.Create(new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30)),
            Money.Usd(capital),
            StrategyParameters.Create([new("fast-period", "26"), new("slow-period", "130")]),
            T0);
    }
}
