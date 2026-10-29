using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Analytics;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Tests.Analytics;

public class PerformanceCalculatorTests
{
    private static readonly DateTime D0 = new(2026, 1, 5);

    [Fact]
    public void Max_drawdown_is_the_deepest_fall_from_a_running_peak()
    {
        Assert.Equal(25m, PerformanceCalculator.MaxDrawdownPercent(Equity(100, 120, 90, 130, 117)));
        Assert.Equal(0m, PerformanceCalculator.MaxDrawdownPercent(Equity(100, 101, 102)));
        Assert.Equal(0m, PerformanceCalculator.MaxDrawdownPercent([]));
    }

    [Fact]
    public void Drawdown_periods_are_listed_deepest_first_with_recovery()
    {
        var periods = PerformanceCalculator.DrawdownPeriods(Equity(100, 120, 90, 100, 130, 117, 120), top: 5);

        Assert.Equal(2, periods.Count);
        Assert.Equal(25m, periods[0].DepthPercent);
        Assert.Equal(D0.AddDays(1), periods[0].PeakTime);
        Assert.Equal(D0.AddDays(2), periods[0].TroughTime);
        Assert.Equal(D0.AddDays(4), periods[0].RecoveredAt);
        Assert.Equal(TimeSpan.FromDays(2), periods[0].Recovery);

        Assert.Equal(10m, periods[1].DepthPercent);
        Assert.False(periods[1].IsRecovered);
        Assert.Single(PerformanceCalculator.DrawdownPeriods(Equity(100, 120, 90, 100, 130, 117, 120), top: 1));
    }

    [Fact]
    public void Sharpe_ratio_uses_one_point_per_day_and_follows_the_sign_of_returns()
    {
        var rising = Equity(100, 101, 103, 102, 105, 107);
        var falling = Equity(100, 99, 97, 98, 95, 93);

        Assert.True(PerformanceCalculator.SharpeRatio(rising) > 0);
        Assert.True(PerformanceCalculator.SharpeRatio(falling) < 0);
        Assert.Equal(0m, PerformanceCalculator.SharpeRatio(Equity(100, 100, 100, 100)));

        // Extra intraday points do not change the daily closes.
        var intraday = rising.SelectMany(p => new[] { p with { Time = p.Time.AddHours(10), Value = 1 }, p with { Time = p.Time.AddHours(16) } }).ToList();
        Assert.Equal(PerformanceCalculator.SharpeRatio(rising), PerformanceCalculator.SharpeRatio(intraday));
    }

    [Fact]
    public void Metrics_combine_equity_and_trades()
    {
        var report = TestData.Report("r") with
        {
            Equity = Equity(100_000, 90_000, 110_000),
            ClosedTrades = [Trade(1, 500), Trade(2, -200), Trade(3, 300), Trade(4, 0)],
            TradeStatistics = new TradeStatistics(4, 4m, 2, 1)
        };

        var metrics = PerformanceCalculator.Calculate(report);

        Assert.Equal(10m, metrics.NetProfitPercent);
        Assert.Equal(10m, metrics.MaxDrawdownPercent);
        Assert.Equal(4, metrics.TotalTrades);
        Assert.Equal(50m, metrics.WinRatePercent);
        Assert.Equal(4m, metrics.ProfitFactor);
    }

    internal static List<EquityPoint> Equity(params decimal[] values) =>
        values.Select((v, i) => new EquityPoint(D0.AddDays(i), v)).ToList();

    internal static ClosedTrade Trade(int number, decimal profit) =>
        new(number, "AAPL", TradeDirection.Long, 10, D0, 100, D0.AddDays(1), 100 + profit / 10, "", profit, 1);
}

public class MonteCarloSimulatorTests
{
    private static readonly double[] Profits = [500, -300, 800, -1200, 400, 650, -250, 900, -700, 300, 150, -400];

    [Fact]
    public void Parallel_result_is_identical_to_the_sequential_one()
    {
        var settings = new MonteCarloSettings(5_000, MonteCarloMethod.Bootstrap, Seed: 7);

        var parallel = MonteCarloSimulator.Run(10_000, Profits, settings, TestContext.Current.CancellationToken);
        var sequential = MonteCarloSimulator.RunSequential(10_000, Profits, settings);

        Assert.Equal(sequential.FinalReturnPercent, parallel.FinalReturnPercent);
        Assert.Equal(sequential.MaxDrawdownPercent, parallel.MaxDrawdownPercent);
        Assert.Equal(sequential.AverageReturnPercent, parallel.AverageReturnPercent);
        Assert.Equal(sequential.ProbabilityOfLossPercent, parallel.ProbabilityOfLossPercent);
        Assert.Equal(sequential.DrawdownHistogram, parallel.DrawdownHistogram);
        Assert.Equal(sequential.SamplePaths.Count, parallel.SamplePaths.Count);
        for (var i = 0; i < sequential.SamplePaths.Count; i++)
            Assert.Equal(sequential.SamplePaths[i], parallel.SamplePaths[i]);
    }

    [Fact]
    public void Same_seed_same_result_other_seed_other_result()
    {
        var first = MonteCarloSimulator.Run(10_000, Profits, new MonteCarloSettings(2_000, MonteCarloMethod.Shuffle, Seed: 1));
        var again = MonteCarloSimulator.Run(10_000, Profits, new MonteCarloSettings(2_000, MonteCarloMethod.Shuffle, Seed: 1));
        var other = MonteCarloSimulator.Run(10_000, Profits, new MonteCarloSettings(2_000, MonteCarloMethod.Shuffle, Seed: 2));

        Assert.Equal(first.MaxDrawdownPercent, again.MaxDrawdownPercent);
        Assert.NotEqual(first.SamplePaths[0], other.SamplePaths[0]);
    }

    [Fact]
    public void Shuffling_keeps_the_final_result_and_varies_the_drawdown()
    {
        var result = MonteCarloSimulator.Run(10_000, Profits, new MonteCarloSettings(3_000, MonteCarloMethod.Shuffle, Seed: 42));

        // Every trade exactly once: the sum, and so the final return, is always the historical one.
        Assert.Equal(result.OriginalReturnPercent, result.FinalReturnPercent.P5, 9);
        Assert.Equal(result.OriginalReturnPercent, result.FinalReturnPercent.P95, 9);
        Assert.True(result.MaxDrawdownPercent.P95 > result.MaxDrawdownPercent.P5);
        Assert.Equal(3_000, result.DrawdownHistogram.Sum(b => b.Count));
        Assert.Equal(Profits.Length, result.Trades);
    }

    [Fact]
    public void Bootstrap_of_only_winning_trades_never_loses_or_draws_down()
    {
        var result = MonteCarloSimulator.Run(1_000, [10, 20, 5], new MonteCarloSettings(1_000, MonteCarloMethod.Bootstrap, Seed: 3));

        Assert.Equal(0, result.ProbabilityOfLossPercent);
        Assert.Equal(0, result.MaxDrawdownPercent.P95);
        Assert.True(result.FinalReturnPercent.P5 >= 1.5 && result.FinalReturnPercent.P95 <= 6);
    }

    [Fact]
    public void Sample_paths_start_at_the_starting_equity_and_have_one_point_per_trade()
    {
        var result = MonteCarloSimulator.Run(10_000, Profits, new MonteCarloSettings(100, MonteCarloMethod.Shuffle, Seed: 42, SamplePaths: 10));

        Assert.Equal(10, result.SamplePaths.Count);
        Assert.All(result.SamplePaths, path =>
        {
            Assert.Equal(Profits.Length + 1, path.Count);
            Assert.Equal(10_000, path[0]);
        });
        Assert.Equal(10_000 + Profits.Sum(), result.OriginalPath[^1]);
    }

    [Fact]
    public void Percentiles_interpolate_between_ranks()
    {
        var percentiles = Percentiles.FromSorted([0, 10, 20, 30, 40]);

        Assert.Equal(new Percentiles(2, 10, 20, 30, 38), percentiles);
    }
}

public class AnalyticsQueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Monte_carlo_query_is_cached_per_run_and_settings()
    {
        await using var host = new ApplicationTestHost();
        host.Reader.ReportFor = ReportWithTrades;

        var first = await host.Dispatcher.SendAsync(new SimulateMonteCarloQuery("AAPL", "run-1", 1_000), Ct);
        var cached = await host.Dispatcher.SendAsync(new SimulateMonteCarloQuery("aapl", "run-1", 1_000), Ct);
        var otherSeed = await host.Dispatcher.SendAsync(new SimulateMonteCarloQuery("AAPL", "run-1", 1_000, Seed: 1), Ct);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Same(first.Value, cached.Value);
        Assert.NotSame(first.Value, otherSeed.Value);
        Assert.Equal(1_000, first.Value.Simulations);
    }

    [Fact]
    public async Task Monte_carlo_needs_at_least_two_trades_and_sane_settings()
    {
        await using var host = new ApplicationTestHost();

        var noTrades = await host.Dispatcher.SendAsync(new SimulateMonteCarloQuery("AAPL", "empty"), Ct);
        var tooMany = await host.Dispatcher.SendAsync(new SimulateMonteCarloQuery("AAPL", "run-1", 1_000_000), Ct);

        Assert.Equal("Analytics.NotEnoughTrades", noTrades.Error?.Code);
        Assert.Equal(ErrorType.Validation, tooMany.Error?.Type);
    }

    [Fact]
    public async Task Drawdown_analysis_lists_the_deepest_periods()
    {
        await using var host = new ApplicationTestHost();
        host.Reader.ReportFor = ReportWithTrades;

        var analysis = await host.Dispatcher.SendAsync(new GetDrawdownAnalysisQuery("AAPL", "run-1", Top: 3), Ct);

        Assert.True(analysis.IsSuccess, analysis.Error?.Message);
        Assert.Equal(25m, analysis.Value.MaxDrawdownPercent);
        Assert.Equal(2, analysis.Value.Periods.Count);
        Assert.Equal(4 * 100.0 / 7, analysis.Value.TimeUnderwaterPercent, 6); // 90, 100, 117 and 120 are below their peak
    }

    private static BacktestReport ReportWithTrades(string runId) => TestData.Report(runId) with
    {
        Equity = PerformanceCalculatorTests.Equity(100, 120, 90, 100, 130, 117, 120),
        ClosedTrades = [PerformanceCalculatorTests.Trade(1, 5), PerformanceCalculatorTests.Trade(2, -3), PerformanceCalculatorTests.Trade(3, 8)]
    };
}
