using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.RunBacktest;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Domain.Backtesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Stockhog.Application.Tests.Backtesting;

public class RunBacktestCommandHandlerTests
{
    private readonly FakeBacktestEngine _engine = new();
    private readonly CountingResultReader _results = new();
    private readonly RecordingEventPublisher _events = new();
    private readonly InMemoryDatabase _runs = new();
    private readonly InMemoryUnitOfWork _unitOfWork;
    private readonly FakeTimeProvider _clock = new(TestData.Now);

    public RunBacktestCommandHandlerTests()
    {
        _unitOfWork = new InMemoryUnitOfWork(_runs, _events);
    }

    [Fact]
    public async Task Run_is_saved_as_running_before_the_engine_starts_and_saved_again_when_done()
    {
        var statusWhileEngineRuns = new List<BacktestStatus>();
        _engine.Behavior = (_, _, _) =>
        {
            statusWhileEngineRuns.AddRange(_unitOfWork.SavedStatuses);
            return Task.CompletedTask;
        };

        await CreateHandler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal([BacktestStatus.Running], statusWhileEngineRuns);
        Assert.Equal([BacktestStatus.Running, BacktestStatus.Completed], _unitOfWork.SavedStatuses);
        Assert.Equal(BacktestStatus.Completed, Assert.Single(_runs.Runs).Status);
    }

    [Fact]
    public async Task Successful_run_returns_its_id_and_publishes_completion()
    {
        var result = await CreateHandler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("20261015-120000000_AAPL_SmaCrossStrategy", result.Value.RunId);
        Assert.Equal("AAPL", result.Value.Symbol);

        var request = Assert.Single(_engine.Requests);
        Assert.Equal("26", request.Parameters.Values[FakeStrategyCatalog.FastPeriod]);
        Assert.Equal(100_000m, request.InitialCapital.Amount);
        // Parameters not given in the command are stored with their declared default.
        Assert.Equal("1", request.Parameters.Values["risk-percent"]);

        var completed = Assert.IsType<BacktestRunCompleted>(Assert.Single(_events.Published));
        Assert.Equal(result.Value.RunId, completed.RunId.Value);
    }

    [Fact]
    public async Task Engine_progress_is_forwarded_to_the_caller()
    {
        var reported = new List<int>();
        var command = Command() with { Progress = new SyncProgress<BacktestProgress>(p => reported.Add(p.Percent)) };

        await CreateHandler().HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.Equal([50, 100], reported);
    }

    [Fact]
    public async Task Engine_error_becomes_a_failure_result_and_failed_event()
    {
        _engine.Behavior = (_, _, _) => throw new BacktestEngineException("no data");

        var result = await CreateHandler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("Backtest.EngineFailed", result.Error.Code);
        var failed = Assert.IsType<BacktestRunFailed>(Assert.Single(_events.Published));
        Assert.Equal("no data", failed.Reason);
    }

    [Fact]
    public async Task Cancellation_propagates_and_publishes_cancelled_event()
    {
        using var cts = new CancellationTokenSource();
        _engine.Behavior = (_, progress, ct) =>
        {
            progress?.Report(new BacktestProgress(30));
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateHandler().HandleAsync(Command(), cts.Token));

        var cancelled = Assert.IsType<BacktestRunCancelled>(Assert.Single(_events.Published));
        Assert.Equal(30, cancelled.Progress);
        Assert.Equal([BacktestStatus.Running, BacktestStatus.Cancelled], _unitOfWork.SavedStatuses);
    }

    [Fact]
    public async Task Unexpected_exception_still_records_a_failed_run()
    {
        _engine.Behavior = (_, _, _) => throw new IOException("disk full");

        await Assert.ThrowsAsync<IOException>(() => CreateHandler().HandleAsync(Command(), TestContext.Current.CancellationToken));

        Assert.IsType<BacktestRunFailed>(Assert.Single(_events.Published));
        Assert.Equal(BacktestStatus.Failed, Assert.Single(_runs.Runs).Status);
    }

    [Fact]
    public async Task Completed_run_stores_metrics_calculated_from_its_result()
    {
        _results.ReportFor = runId => new BacktestReport(
            runId,
            new Dictionary<string, string>(),
            new TradeStatistics(1, 2.5m, 1, 0),
            [new(new DateTime(2026, 1, 2), 100_000m), new(new DateTime(2026, 1, 5), 95_000m), new(new DateTime(2026, 1, 6), 110_000m)],
            [new(1, "AAPL", Domain.Trading.TradeDirection.Long, 10, new DateTime(2026, 1, 2), 100, new DateTime(2026, 1, 6), 110, "Exit", 100, 0)]);

        var result = await CreateHandler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        var metrics = Assert.Single(_runs.Runs).Metrics;
        Assert.NotNull(metrics);
        Assert.Equal((10m, 5m, 1, 100m, 2.5m), (metrics.NetProfitPercent, metrics.MaxDrawdownPercent, metrics.TotalTrades, metrics.WinRatePercent, metrics.ProfitFactor));
        Assert.Equal(metrics, result.Value.Metrics);
    }

    [Fact]
    public async Task Run_without_a_readable_result_completes_without_metrics()
    {
        _results.ReportFor = _ => null;
        var missing = await CreateHandler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        _results.ReportFor = _ => throw new IOException("result file locked");
        var unreadable = await CreateHandler().HandleAsync(Command(fast: 30), TestContext.Current.CancellationToken);

        Assert.True(missing.IsSuccess && unreadable.IsSuccess);
        Assert.All(_runs.Runs, run => Assert.Equal((BacktestStatus.Completed, (PerformanceMetrics?)null), (run.Status, run.Metrics)));
    }

    private RunBacktestCommandHandler CreateHandler() =>
        new(_engine, _results, _runs, _unitOfWork, new FakeStrategyCatalog(), _clock, NullLogger<RunBacktestCommandHandler>.Instance);

    internal static RunBacktestCommand Command(int fast = 26, int slow = 130) => new(
        "SmaCrossStrategy",
        "aapl",
        new DateOnly(2026, 1, 2),
        new DateOnly(2026, 9, 30),
        100_000m,
        new Dictionary<string, string>
        {
            [FakeStrategyCatalog.FastPeriod] = fast.ToString(),
            [FakeStrategyCatalog.SlowPeriod] = slow.ToString()
        });
}

public class RunBacktestCommandValidatorTests
{
    private readonly RunBacktestCommandValidator _validator = new(new FakeTimeProvider(TestData.Now), new FakeStrategyCatalog());

    [Fact]
    public void Valid_command_passes()
    {
        Assert.True(_validator.Validate(RunBacktestCommandHandlerTests.Command()).IsValid);
    }

    [Theory]
    [InlineData(130, 26, "Slow EMA period must be greater than Fast EMA period.")]
    [InlineData(50, 50, "Slow EMA period must be greater than Fast EMA period.")]
    [InlineData(0, 10, "Fast EMA period must be at least 2.")]
    [InlineData(10, 9000, "Slow EMA period must be at most 2000.")]
    public void Parameters_are_checked_against_the_strategy_declaration(int fast, int slow, string expected)
    {
        var result = _validator.Validate(RunBacktestCommandHandlerTests.Command(fast, slow));

        Assert.Contains(expected, result.Errors.Select(e => e.ErrorMessage));
    }

    [Fact]
    public void Unknown_strategy_and_unknown_parameters_are_rejected()
    {
        var unknownStrategy = RunBacktestCommandHandlerTests.Command() with { StrategyName = "NoSuchStrategy" };
        var unknownParameter = RunBacktestCommandHandlerTests.Command() with
        {
            Parameters = new Dictionary<string, string> { ["fast-period"] = "10", ["typo-period"] = "5" }
        };

        Assert.Contains("Unknown strategy 'NoSuchStrategy'.", _validator.Validate(unknownStrategy).Errors.Select(e => e.ErrorMessage));
        Assert.Contains("EMA Cross has no parameter 'typo-period'.", _validator.Validate(unknownParameter).Errors.Select(e => e.ErrorMessage));
    }

    [Theory]
    [InlineData("12.5", "Fast EMA period must be a whole number.")]
    [InlineData("abc", "Fast EMA period: 'abc' is not a number.")]
    public void Values_must_match_the_parameter_type(string fast, string expected)
    {
        var command = RunBacktestCommandHandlerTests.Command() with
        {
            Parameters = new Dictionary<string, string> { [FakeStrategyCatalog.FastPeriod] = fast }
        };

        Assert.Contains(expected, _validator.Validate(command).Errors.Select(e => e.ErrorMessage));
    }

    [Fact]
    public void Collects_every_broken_rule()
    {
        var command = RunBacktestCommandHandlerTests.Command() with
        {
            Symbol = "not a symbol",
            Start = new DateOnly(2026, 12, 1),
            End = new DateOnly(2026, 11, 1),
            InitialCapital = 0
        };

        var messages = _validator.Validate(command).Errors.Select(e => e.ErrorMessage).ToList();

        Assert.Contains("'not a symbol' is not a valid symbol (e.g. AAPL).", messages);
        Assert.Contains("End date must not be before start date.", messages);
        Assert.Contains("Initial capital must be positive.", messages);
    }

    [Fact]
    public void End_date_must_not_be_in_the_future()
    {
        var command = RunBacktestCommandHandlerTests.Command() with { End = new DateOnly(2026, 10, 16) };

        Assert.Contains(_validator.Validate(command).Errors, e => e.ErrorMessage.Contains("must not be in the future"));
    }
}
