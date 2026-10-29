using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.FailInterruptedBacktests;
using Stockhog.Application.Backtesting.GetBacktestHistory;
using Stockhog.Application.Backtesting.GetBacktestReport;
using Stockhog.Application.Backtesting.GetLatestBacktestReport;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Application.Tests.Backtesting;

/// <summary>
/// Backtesting features through the real pipeline (see <see cref="ApplicationTestHost"/>).
/// </summary>
public class BacktestPipelineIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Completed_run_invalidates_the_cached_latest_report()
    {
        await using var host = new ApplicationTestHost();
        var latest = new GetLatestBacktestReportQuery("AAPL", "SmaCrossStrategy");

        await host.Dispatcher.SendAsync(latest, Ct);
        await host.Dispatcher.SendAsync(latest, Ct);
        Assert.Equal(1, host.Reader.LatestCalls);

        var run = await host.Dispatcher.SendAsync(RunBacktestCommandHandlerTests.Command(), Ct);
        Assert.True(run.IsSuccess);

        await host.Dispatcher.SendAsync(latest, Ct);
        Assert.Equal(2, host.Reader.LatestCalls);
    }

    [Fact]
    public async Task Run_report_is_cached_by_run_id()
    {
        await using var host = new ApplicationTestHost();

        var first = await host.Dispatcher.SendAsync(new GetBacktestReportQuery("aapl", "run-1"), Ct);
        var second = await host.Dispatcher.SendAsync(new GetBacktestReportQuery("AAPL", "run-1"), Ct);

        Assert.Same(first.Value, second.Value);
        Assert.Equal(1, host.Reader.ReadCalls);
    }

    [Fact]
    public async Task Invalid_command_never_reaches_the_engine()
    {
        await using var host = new ApplicationTestHost();

        var result = await host.Dispatcher.SendAsync(RunBacktestCommandHandlerTests.Command(fast: 200, slow: 100), Ct);

        Assert.Equal(ErrorType.Validation, result.Error?.Type);
        Assert.Empty(host.Engine.Requests);
    }

    [Fact]
    public async Task Missing_latest_report_is_a_not_found_error()
    {
        await using var host = new ApplicationTestHost();
        host.Reader.Latest = null;

        var result = await host.Dispatcher.SendAsync(new GetLatestBacktestReportQuery("MSFT", "SmaCrossStrategy"), Ct);

        Assert.Equal(BacktestErrors.NoResults("MSFT", "SmaCrossStrategy"), result.Error);
    }

    [Fact]
    public async Task Runs_appear_in_history_newest_first()
    {
        await using var host = new ApplicationTestHost();

        await host.Dispatcher.SendAsync(RunBacktestCommandHandlerTests.Command(), Ct);
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        host.Engine.Behavior = (_, _, _) => throw new BacktestEngineException("no data");
        await host.Dispatcher.SendAsync(RunBacktestCommandHandlerTests.Command(), Ct);

        var history = await host.Dispatcher.SendAsync(new GetBacktestHistoryQuery("AAPL"), Ct);

        Assert.Collection(history.Value,
            newest => Assert.Equal((BacktestStatus.Failed, "no data"), (newest.Status, newest.FailureReason)),
            oldest => Assert.Equal((BacktestStatus.Completed, true), (oldest.Status, oldest.HasReport)));
    }

    [Fact]
    public async Task Interrupted_runs_are_failed_and_saved()
    {
        await using var host = new ApplicationTestHost();

        // A run that was still running when the previous session ended.
        var symbol = Symbol.Create("MSFT");
        var strategy = StrategyName.Create("SmaCrossStrategy");
        var stale = BacktestRun.Create(BacktestRunId.New(symbol, strategy, TestData.Now), symbol, strategy,
            DateRange.Create(new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1)), Money.Usd(1000), StrategyParameters.Empty, TestData.Now);
        stale.Start(TestData.Now);
        host.Database.Add(stale);

        var result = await host.Dispatcher.SendAsync(new FailInterruptedBacktestsCommand(), Ct);

        Assert.Equal(1, result.Value);
        Assert.Equal(BacktestStatus.Failed, stale.Status);
        Assert.Equal(FailInterruptedBacktestsCommandHandler.Reason, stale.FailureReason);
        Assert.Empty(stale.DomainEvents); // saved and published by UnitOfWorkBehavior
    }

    [Fact]
    public async Task Failed_command_is_not_saved_by_the_unit_of_work_behavior()
    {
        await using var host = new ApplicationTestHost();
        host.Reader.Latest = null;

        // A query never saves; a validation failure never reaches the handler or the unit of work.
        await host.Dispatcher.SendAsync(new GetLatestBacktestReportQuery("AAPL", "SmaCrossStrategy"), Ct);
        await host.Dispatcher.SendAsync(RunBacktestCommandHandlerTests.Command(fast: 200, slow: 100), Ct);

        Assert.Empty(host.Database.Runs);
    }
}
