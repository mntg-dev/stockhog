using Stockhog.Application.Backtesting.GetRunRanking;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Application.Tests.Backtesting;

public class RunRankingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ranking_keeps_the_latest_run_per_symbol_and_strategy_best_profit_first()
    {
        await using var host = new ApplicationTestHost();
        host.Database.Runs.AddRange(
        [
            Completed("AAPL", "SmaCrossStrategy", minutes: 0, netProfit: 30),
            Completed("AAPL", "SmaCrossStrategy", minutes: 5, netProfit: 4),   // newer, replaces the 30% run
            Completed("MSFT", "SmaCrossStrategy", minutes: 1, netProfit: 12),
            Completed("MSFT", "RsiStrategy", minutes: 2, netProfit: -3),
            Completed("NVDA", "SmaCrossStrategy", minutes: 3, netProfit: null) // completed without metrics
        ]);

        var latest = await host.Dispatcher.SendAsync(new GetRunRankingQuery(), Ct);
        var all = await host.Dispatcher.SendAsync(new GetRunRankingQuery(LatestPerSymbol: false), Ct);
        var sma = await host.Dispatcher.SendAsync(new GetRunRankingQuery("SmaCrossStrategy", Symbols: ["msft", "aapl"]), Ct);

        Assert.Equal([("MSFT", 12m), ("AAPL", 4m), ("MSFT", -3m)], latest.Value.Select(r => (r.Symbol, r.NetProfitPercent)));
        Assert.Equal(4, all.Value.Count);
        Assert.Equal(["MSFT", "AAPL"], sma.Value.Select(r => r.Symbol));
    }

    [Fact]
    public async Task Return_over_drawdown_is_calculated_like_the_optimization_objective()
    {
        await using var host = new ApplicationTestHost();
        host.Database.Runs.Add(Completed("AAPL", "SmaCrossStrategy", minutes: 0, netProfit: 12, drawdown: 4));
        host.Database.Runs.Add(Completed("MSFT", "SmaCrossStrategy", minutes: 1, netProfit: 3, drawdown: 0.2m));

        var ranking = await host.Dispatcher.SendAsync(new GetRunRankingQuery(), Ct);

        // Drawdowns below 1% count as 1%, so a nearly flat run does not get an extreme score.
        Assert.Equal([3m, 3m], ranking.Value.Select(r => r.ReturnOverDrawdown));
    }

    private static BacktestRun Completed(string ticker, string strategyName, int minutes, decimal? netProfit, decimal drawdown = 5)
    {
        var symbol = Symbol.Create(ticker);
        var strategy = StrategyName.Create(strategyName);
        var createdAt = TestData.Now.AddMinutes(minutes);
        var run = BacktestRun.Create(
            BacktestRunId.New(symbol, strategy, createdAt),
            symbol,
            strategy,
            DateRange.Create(new DateOnly(2026, 1, 2), new DateOnly(2026, 9, 30)),
            Money.Usd(100_000m),
            StrategyParameters.Create([new("fast-period", "26")]),
            createdAt);
        run.Start(createdAt);
        run.Complete(createdAt.AddSeconds(5), netProfit is { } profit ? new PerformanceMetrics(profit, drawdown, 1.1m, 10, 50, 1.4m) : null);
        return run;
    }
}
