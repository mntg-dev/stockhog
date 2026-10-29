using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.BackfillMetrics;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Application.Tests.Backtesting;

public class BackfillMetricsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Completed_runs_without_metrics_get_them_and_runs_without_a_result_are_skipped()
    {
        await using var host = new ApplicationTestHost();
        // More runs than one batch, with a missing result in the middle.
        var runs = Enumerable.Range(0, BackfillBacktestMetricsCommand.BatchSize + 20).Select(i => Completed(i)).ToList();
        var missing = runs[30].Id.Value;
        host.Database.Runs.AddRange(runs);
        host.Reader.ReportFor = runId => runId == missing ? null : Report(runId);

        var result = await host.Dispatcher.SendAsync(new BackfillBacktestMetricsCommand(), Ct);

        Assert.Equal(runs.Count - 1, result.Value);
        Assert.All(runs.Where(r => r.Id.Value != missing), r => Assert.Equal(10m, r.Metrics?.NetProfitPercent));
        Assert.Null(runs[30].Metrics);
        Assert.Equal(runs.Count, host.Reader.ReadCalls);
    }

    [Fact]
    public async Task Backfill_is_queued_only_when_some_run_needs_it()
    {
        await using var host = new ApplicationTestHost();

        var nothingToDo = await host.Dispatcher.SendAsync(new QueueMetricsBackfillCommand(), Ct);
        host.Database.Runs.Add(Completed(0));
        var queued = await host.Dispatcher.SendAsync(new QueueMetricsBackfillCommand(), Ct);
        var outcomes = await host.Jobs.RunAllAsync(host.Dispatcher);

        Assert.False(nothingToDo.Value);
        Assert.True(queued.Value);
        Assert.True(Assert.Single(outcomes).IsSuccess);
        Assert.NotNull(host.Database.Runs.Single().Metrics);
    }

    private static BacktestRun Completed(int index)
    {
        var symbol = Symbol.Create("AAPL");
        var strategy = StrategyName.Create("SmaCrossStrategy");
        var createdAt = TestData.Now.AddMinutes(index);
        var run = BacktestRun.Create(
            BacktestRunId.New(symbol, strategy, createdAt),
            symbol,
            strategy,
            DateRange.Create(new DateOnly(2026, 1, 2), new DateOnly(2026, 9, 30)),
            Money.Usd(100_000m),
            StrategyParameters.Create([new("fast-period", "26")]),
            createdAt);
        run.Start(createdAt);
        run.Complete(createdAt.AddSeconds(5));
        return run;
    }

    private static BacktestReport Report(string runId) => new(
        runId,
        new Dictionary<string, string>(),
        new TradeStatistics(0, 0, 0, 0),
        [new(new DateTime(2026, 1, 2), 100_000m), new(new DateTime(2026, 1, 5), 110_000m)],
        []);
}
