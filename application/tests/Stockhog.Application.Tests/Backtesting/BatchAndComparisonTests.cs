using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.CompareBacktests;
using Stockhog.Application.Backtesting.QueueBacktests;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.Tests.TestSupport;

namespace Stockhog.Application.Tests.Backtesting;

public class QueueBacktestsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_job_per_distinct_symbol_and_each_job_runs_a_backtest()
    {
        await using var host = new ApplicationTestHost();

        var queued = await host.Dispatcher.SendAsync(Command(["msft", "AAPL", "MSFT"]), Ct);

        Assert.Equal(2, queued.Value.JobIds.Count);
        Assert.Equal(["backtest:MSFT:SmaCrossStrategy", "backtest:AAPL:SmaCrossStrategy"], host.Jobs.Pending.Select(j => j.Key));

        var outcomes = await host.Jobs.RunAllAsync(host.Dispatcher);

        Assert.All(outcomes, o => Assert.True(o.IsSuccess, o.Message));
        Assert.Equal(["MSFT", "AAPL"], host.Engine.Requests.Select(r => r.Symbol.Value));
        Assert.All(host.Database.Runs, r => Assert.Equal(Domain.Backtesting.BacktestStatus.Completed, r.Status));
    }

    [Fact]
    public async Task A_symbol_with_an_active_run_of_the_same_strategy_is_skipped()
    {
        await using var host = new ApplicationTestHost();
        await host.Dispatcher.SendAsync(Command(["AAPL"]), Ct);

        var second = await host.Dispatcher.SendAsync(Command(["AAPL", "MSFT"]), Ct);

        Assert.Single(second.Value.JobIds);
        Assert.Equal(["AAPL"], second.Value.AlreadyQueued);
    }

    [Fact]
    public async Task Invalid_parameters_are_rejected_before_anything_is_queued()
    {
        await using var host = new ApplicationTestHost();

        var result = await host.Dispatcher.SendAsync(
            Command(["AAPL", "MSFT"], new Dictionary<string, string> { [FakeStrategyCatalog.FastPeriod] = "200", [FakeStrategyCatalog.SlowPeriod] = "100" }), Ct);

        Assert.Equal(ErrorType.Validation, result.Error?.Type);
        Assert.Contains("Slow EMA period must be greater than Fast EMA period.", result.Error!.Details);
        Assert.Empty(host.Jobs.Pending);
    }

    [Fact]
    public async Task Failed_backtest_becomes_a_failed_job()
    {
        await using var host = new ApplicationTestHost();
        host.Engine.Behavior = (_, _, _) => throw new BacktestEngineException("no data");
        await host.Dispatcher.SendAsync(Command(["AAPL"]), Ct);

        var outcome = Assert.Single(await host.Jobs.RunAllAsync(host.Dispatcher));

        Assert.False(outcome.IsSuccess);
        Assert.Contains("no data", outcome.Message);
    }

    private static QueueBacktestsCommand Command(IReadOnlyList<string> symbols, IReadOnlyDictionary<string, string>? parameters = null) =>
        new("SmaCrossStrategy", symbols, new DateOnly(2026, 1, 2), new DateOnly(2026, 9, 30), 100_000m, parameters ?? new Dictionary<string, string>());
}

public class CompareBacktestsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Equity_is_normalized_to_cumulative_return_and_reports_are_cached()
    {
        await using var host = new ApplicationTestHost();
        host.Reader.ReportFor = runId => runId == "run-a"
            ? Report(runId, 100_000m, 110_000m, 99_000m)
            : Report(runId, 1_000_000m, 1_050_000m);

        var query = new CompareBacktestsQuery([new("aapl", "run-a"), new("MSFT", "run-b")]);
        var first = await host.Dispatcher.SendAsync(query, Ct);
        await host.Dispatcher.SendAsync(query, Ct);

        Assert.Equal([0m, 10m, -1m], first.Value.Runs[0].CumulativeReturn.Select(p => p.ReturnPercent));
        Assert.Equal([0m, 5m], first.Value.Runs[1].CumulativeReturn.Select(p => p.ReturnPercent));
        Assert.Equal(("AAPL", "12.5%", 2.5m, 40), (first.Value.Runs[0].Symbol, first.Value.Runs[0].NetProfit, first.Value.Runs[0].ProfitFactor, first.Value.Runs[0].TotalTrades));
        Assert.Equal(2, host.Reader.ReadCalls); // the second comparison came from the report cache
    }

    [Fact]
    public async Task A_missing_report_fails_the_comparison()
    {
        await using var host = new ApplicationTestHost();
        host.Reader.ReportFor = runId => runId == "gone" ? null : TestData.Report(runId);

        var result = await host.Dispatcher.SendAsync(new CompareBacktestsQuery([new("AAPL", "run-a"), new("AAPL", "gone")]), Ct);

        Assert.Equal(BacktestErrors.ReportNotFound("gone"), result.Error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public async Task Between_two_and_eight_runs_can_be_compared(int count)
    {
        await using var host = new ApplicationTestHost();
        var runs = Enumerable.Range(0, count).Select(i => new BacktestRunReference("AAPL", $"run-{i}")).ToList();

        var result = await host.Dispatcher.SendAsync(new CompareBacktestsQuery(runs), Ct);

        Assert.Equal(ErrorType.Validation, result.Error?.Type);
    }

    private static BacktestReport Report(string runId, params decimal[] equity) => new(
        runId,
        new Dictionary<string, string> { ["Net Profit"] = "12.5%", ["Sharpe Ratio"] = "1.1" },
        new TradeStatistics(40, 2.5m, 4, 3),
        equity.Select((value, day) => new EquityPoint(new DateTime(2026, 1, 2).AddDays(day), value)).ToList(),
        []);
}

public class StrategyCatalogTests
{
    [Fact]
    public async Task Strategies_query_returns_the_catalog()
    {
        await using var host = new ApplicationTestHost();

        var strategies = await host.Dispatcher.SendAsync(new GetStrategiesQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(["SmaCrossStrategy"], strategies.Value.Select(s => s.Name));
    }

    [Fact]
    public void Missing_values_get_defaults_formatted_by_type()
    {
        var values = FakeStrategyCatalog.SmaCross.WithDefaults(new Dictionary<string, string> { [FakeStrategyCatalog.FastPeriod] = " 12 " });

        Assert.Equal("12", values[FakeStrategyCatalog.FastPeriod]);
        Assert.Equal("130", values[FakeStrategyCatalog.SlowPeriod]);
        Assert.Equal("1", values["risk-percent"]);
    }

    [Fact]
    public void Decimal_comma_is_accepted()
    {
        Assert.Empty(FakeStrategyCatalog.SmaCross.Validate(new Dictionary<string, string> { ["risk-percent"] = "1,5" }));
    }
}
