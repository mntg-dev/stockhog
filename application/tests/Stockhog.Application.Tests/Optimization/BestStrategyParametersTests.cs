using Stockhog.Application.Optimization.GetBestStrategyParameters;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Optimization;

namespace Stockhog.Application.Tests.Optimization;

public class BestStrategyParametersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Best_trial_of_each_optimization_of_the_strategy_newest_first()
    {
        await using var host = new ApplicationTestHost();
        host.Database.Optimizations.AddRange(
        [
            Optimization("AAPL", "SmaCrossStrategy", minutes: 0, sharpeByTrial: [0.5m, 1.4m]),
            Optimization("MSFT", "SmaCrossStrategy", minutes: 10, sharpeByTrial: [0.9m, 0.2m]),
            Optimization("AAPL", "RsiMeanReversionStrategy", minutes: 20, sharpeByTrial: [2.0m, 1.0m]),
            Optimization("NVDA", "SmaCrossStrategy", minutes: 30, sharpeByTrial: [])  // no finished trial yet
        ]);

        var result = await host.Dispatcher.SendAsync(new GetBestStrategyParametersQuery("SmaCrossStrategy"), Ct);

        Assert.Equal(["MSFT", "AAPL"], result.Value.Select(r => r.Symbol));
        Assert.Equal((0.9m, "16"), (result.Value[0].Score, result.Value[0].Parameters["fast-period"]));
        Assert.Equal((1.4m, "26"), (result.Value[1].Score, result.Value[1].Parameters["fast-period"]));
        Assert.Equal(1.4m, result.Value[1].Metrics.SharpeRatio);
    }

    [Fact]
    public async Task Empty_strategy_name_is_rejected()
    {
        await using var host = new ApplicationTestHost();

        var result = await host.Dispatcher.SendAsync(new GetBestStrategyParametersQuery(""), Ct);

        Assert.True(result.IsFailure);
    }

    /// <summary>Two trials (fast period 16 and 26); trials listed in <paramref name="sharpeByTrial"/> complete with that Sharpe ratio.</summary>
    private static OptimizationRun Optimization(string ticker, string strategyName, int minutes, decimal[] sharpeByTrial)
    {
        var symbol = Symbol.Create(ticker);
        var strategy = StrategyName.Create(strategyName);
        var createdAt = TestData.Now.AddMinutes(minutes);
        var run = OptimizationRun.Create(
            OptimizationRunId.New(symbol, strategy, createdAt),
            symbol,
            strategy,
            DateRange.Create(new DateOnly(2026, 1, 2), new DateOnly(2026, 9, 30)),
            Money.Usd(100_000m),
            ParameterGrid.Create([ParameterRange.Create("fast-period", 16, 26, 10)]),
            OptimizationObjective.SharpeRatio,
            maxParallelTrials: 2,
            [
                StrategyParameters.Create([new("fast-period", "16"), new("slow-period", "130")]),
                StrategyParameters.Create([new("fast-period", "26"), new("slow-period", "130")])
            ],
            createdAt);

        run.Start(createdAt);
        for (var i = 0; i < sharpeByTrial.Length; i++)
        {
            run.StartTrial(i, BacktestRunId.ForTrial(run.Id, i));
            run.CompleteTrial(i, new PerformanceMetrics(10, 5, sharpeByTrial[i], 12, 50, 1.3m), createdAt.AddMinutes(1));
        }

        return run;
    }
}
