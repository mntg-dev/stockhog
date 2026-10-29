using System.Globalization;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.GetBacktestHistory;
using Stockhog.Application.Optimization;
using Stockhog.Application.Optimization.CheckOptimization;
using Stockhog.Application.Optimization.ExecuteOptimization;
using Stockhog.Application.Optimization.GetBestStrategyParameters;
using Stockhog.Application.Optimization.StartOptimization;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.Optimization;

namespace Stockhog.Application.Tests.Optimization;

public class StartOptimizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Skips_combinations_the_strategy_rejects_and_queues_one_job()
    {
        await using var host = new ApplicationTestHost();

        var started = await host.Dispatcher.SendAsync(OptimizationCommands.FastSlowGrid(), Ct);

        // fast 10..30 x slow 20..40: 9 points, 3 of them with slow <= fast.
        Assert.True(started.IsSuccess, started.Error?.Message);
        Assert.Equal(6, started.Value.Trials);
        Assert.Equal(3, started.Value.SkippedCombinations);

        var job = Assert.Single(host.Jobs.Pending);
        Assert.Equal($"optimization:{started.Value.OptimizationId}", job.Key);

        // Saved before the job was queued, with every parameter (defaults included) on each trial.
        var run = Assert.Single(host.Database.Optimizations);
        Assert.Equal(OptimizationStatus.Queued, run.Status);
        Assert.All(run.Trials, t => Assert.Equal("1", t.Parameters.Values["risk-percent"]));
        Assert.Equal("10/20", $"{run.Trials[0].Parameters.Values["fast-period"]}/{run.Trials[0].Parameters.Values["slow-period"]}");
    }

    [Theory]
    [InlineData("unknown", 1, 5, 1, "has no parameter 'unknown'")]
    [InlineData(FakeStrategyCatalog.FastPeriod, 1, 5, 1, "must be at least 2")]
    [InlineData(FakeStrategyCatalog.FastPeriod, 2, 600, 50, "must be at most 500")]
    [InlineData(FakeStrategyCatalog.FastPeriod, 2, 10, 0.5, "whole numbers")]
    [InlineData(FakeStrategyCatalog.FastPeriod, 10, 5, 1, "'to' must not be below 'from'")]
    [InlineData(FakeStrategyCatalog.FastPeriod, 2, 400, 1, "at most 50 are allowed")]
    public async Task Invalid_ranges_are_rejected_before_anything_is_saved(string name, decimal from, decimal to, decimal step, string expected)
    {
        await using var host = new ApplicationTestHost();

        var result = await host.Dispatcher.SendAsync(OptimizationCommands.Create([new ParameterRangeInput(name, from, to, step)]), Ct);

        Assert.Equal(ErrorType.Validation, result.Error?.Type);
        Assert.Contains(result.Error!.Details, d => d.Contains(expected, StringComparison.Ordinal));
        Assert.Empty(host.Database.Optimizations);
        Assert.Empty(host.Jobs.Pending);
    }

    [Fact]
    public async Task Too_large_grids_and_conflicting_fixed_values_are_rejected()
    {
        await using var host = new ApplicationTestHost();

        var tooLarge = await host.Dispatcher.SendAsync(OptimizationCommands.Create(
        [
            new ParameterRangeInput(FakeStrategyCatalog.FastPeriod, 2, 50, 2),
            new ParameterRangeInput(FakeStrategyCatalog.SlowPeriod, 100, 500, 20)
        ]), Ct);
        var conflicting = await host.Dispatcher.SendAsync(OptimizationCommands.Create(
            [new ParameterRangeInput(FakeStrategyCatalog.FastPeriod, 2, 10, 2)],
            new Dictionary<string, string> { [FakeStrategyCatalog.FastPeriod] = "5" }), Ct);

        Assert.Contains(tooLarge.Error!.Details, d => d.Contains("at most 400 are allowed", StringComparison.Ordinal));
        Assert.Contains(conflicting.Error!.Details, d => d.Contains("cannot also have a fixed value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_grid_without_any_valid_combination_is_a_validation_error()
    {
        await using var host = new ApplicationTestHost();

        // Every slow period is at most the fixed fast period.
        var result = await host.Dispatcher.SendAsync(OptimizationCommands.Create(
            [new ParameterRangeInput(FakeStrategyCatalog.SlowPeriod, 10, 30, 10)],
            new Dictionary<string, string> { [FakeStrategyCatalog.FastPeriod] = "50" }), Ct);

        Assert.Equal(ErrorType.Validation, result.Error?.Type);
        Assert.Contains("Slow EMA period must be greater than Fast EMA period.", result.Error!.Message);
        Assert.Empty(host.Database.Optimizations);
    }
}

public class ExecuteOptimizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Job_runs_every_trial_as_a_linked_backtest_and_picks_the_best()
    {
        await using var host = new ApplicationTestHost();
        host.Reader.ReportFor = OptimizationCommands.ReportWithNetProfitOfTrialIndex;

        var started = await host.Dispatcher.SendAsync(OptimizationCommands.FastSlowGrid(OptimizationObjective.NetProfit), Ct);
        var outcome = Assert.Single(await host.Jobs.RunAllAsync(host.Dispatcher));

        Assert.True(outcome.IsSuccess, outcome.Message);
        Assert.Contains("Best trial #5", outcome.Message);

        var run = Assert.Single(host.Database.Optimizations);
        Assert.Equal(OptimizationStatus.Completed, run.Status);
        Assert.Equal(5, run.BestTrialIndex);
        Assert.All(run.Trials, t => Assert.Equal(TrialStatus.Completed, t.Status));

        // Each trial is a normal, stored backtest run, linked to the optimization and kept out of the history list.
        Assert.Equal(6, host.Engine.Requests.Count);
        Assert.Equal(
            run.Trials.Select(t => t.RunId!.Value).Order(),
            host.Database.Runs.Select(r => r.Id.Value).Order());
        Assert.All(host.Database.Runs, r => Assert.Equal(run.Id, r.OptimizationId));
        Assert.Empty((await host.Dispatcher.SendAsync(new GetBacktestHistoryQuery(), Ct)).Value);
        Assert.Equal(6, (await host.Dispatcher.SendAsync(new GetBacktestHistoryQuery(IncludeTrials: true), Ct)).Value.Count);
        Assert.Equal(started.Value.OptimizationId, run.Id.Value);
    }

    [Fact]
    public async Task Trials_run_in_parallel_up_to_the_limit()
    {
        await using var host = new ApplicationTestHost();
        var running = 0;
        var maxRunning = 0;
        host.Engine.Behavior = async (_, _, cancellationToken) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            await Task.Delay(60, cancellationToken);
            Interlocked.Decrement(ref running);
        };

        await host.Dispatcher.SendAsync(OptimizationCommands.FastSlowGrid(maxParallelTrials: 2), Ct);
        var outcome = Assert.Single(await host.Jobs.RunAllAsync(host.Dispatcher));

        Assert.True(outcome.IsSuccess, outcome.Message);
        Assert.Equal(2, maxRunning);
        Assert.Equal(OptimizationStatus.Completed, host.Database.Optimizations[0].Status);
    }

    [Fact]
    public async Task A_failing_trial_is_recorded_and_the_others_still_run()
    {
        await using var host = new ApplicationTestHost();
        host.Engine.Behavior = (request, _, _) => request.Parameters.Values[FakeStrategyCatalog.FastPeriod] == "20"
            ? throw new BacktestEngineException("no data")
            : Task.CompletedTask;

        await host.Dispatcher.SendAsync(OptimizationCommands.FastSlowGrid(), Ct);
        await host.Jobs.RunAllAsync(host.Dispatcher);

        var run = host.Database.Optimizations[0];
        Assert.Equal(OptimizationStatus.Completed, run.Status);
        Assert.Equal(2, run.Trials.Count(t => t.Status == TrialStatus.Failed));
        Assert.All(run.Trials.Where(t => t.Status == TrialStatus.Failed), t => Assert.Contains("no data", t.FailureReason));
        Assert.Equal(4, run.Trials.Count(t => t.Status == TrialStatus.Completed));
    }

    [Fact]
    public async Task Cancelling_stops_the_engines_and_keeps_finished_trials()
    {
        await using var host = new ApplicationTestHost();
        var firstFinished = new TaskCompletionSource();
        host.Engine.Behavior = async (request, _, cancellationToken) =>
        {
            if (request.Parameters.Values[FakeStrategyCatalog.SlowPeriod] == "20")
            {
                firstFinished.TrySetResult();
                return;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
        };

        var started = await host.Dispatcher.SendAsync(OptimizationCommands.FastSlowGrid(maxParallelTrials: 1), Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var execution = host.Dispatcher.SendAsync(new ExecuteOptimizationCommand(started.Value.OptimizationId), cancellation.Token);
        await firstFinished.Task.WaitAsync(Ct);
        await Task.Delay(50, Ct);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        var run = host.Database.Optimizations[0];
        Assert.Equal(OptimizationStatus.Cancelled, run.Status);
        Assert.Equal(TrialStatus.Completed, run.Trials[0].Status);
        Assert.Equal(0, run.BestTrialIndex);
        Assert.All(run.Trials.Skip(1), t => Assert.Equal(TrialStatus.Cancelled, t.Status));
        Assert.Contains(host.Database.Runs, r => r.Status == BacktestStatus.Cancelled);
    }

    [Fact]
    public async Task A_finished_optimization_cannot_run_again()
    {
        await using var host = new ApplicationTestHost();
        var started = await host.Dispatcher.SendAsync(OptimizationCommands.FastSlowGrid(), Ct);
        await host.Jobs.RunAllAsync(host.Dispatcher);

        var again = await host.Dispatcher.SendAsync(new ExecuteOptimizationCommand(started.Value.OptimizationId), Ct);

        Assert.Equal("Optimization.AlreadyFinished", again.Error?.Code);
    }

    [Fact]
    public async Task Details_list_every_trial_and_build_a_heatmap_with_the_best_cell()
    {
        await using var host = new ApplicationTestHost();
        host.Reader.ReportFor = OptimizationCommands.ReportWithNetProfitOfTrialIndex;
        var started = await host.Dispatcher.SendAsync(OptimizationCommands.FastSlowGrid(OptimizationObjective.NetProfit), Ct);
        await host.Jobs.RunAllAsync(host.Dispatcher);

        var details = (await host.Dispatcher.SendAsync(new GetOptimizationQuery(started.Value.OptimizationId), Ct)).Value;
        var heatmap = details.Heatmap(FakeStrategyCatalog.FastPeriod, FakeStrategyCatalog.SlowPeriod);

        Assert.Equal(["Fast EMA period", "Slow EMA period"], details.Axes.Select(a => a.DisplayName));
        Assert.Equal(["10", "20", "30"], heatmap.X.Labels);
        Assert.Equal(6, details.Trials.Count);
        Assert.Equal(5, details.Best!.Index);
        Assert.Equal("fast-period=30, slow-period=40", details.Best.PointText);

        // Rows are slow periods, columns fast periods; cells with slow <= fast have no trial.
        Assert.Equal(0, heatmap.Scores[0, 0]);
        Assert.Null(heatmap.Scores[0, 1]);
        Assert.Null(heatmap.Scores[0, 2]);
        Assert.Null(heatmap.Scores[1, 2]);
        Assert.Equal(5, heatmap.Scores[2, 2]);
        Assert.Equal((2, 2), heatmap.Best);

        var summary = Assert.Single((await host.Dispatcher.SendAsync(new GetOptimizationsQuery(), Ct)).Value);
        Assert.Equal(100, summary.Progress);
        Assert.Equal(5, summary.BestScore);
    }

    [Fact]
    public async Task Interrupted_optimizations_are_failed_at_startup()
    {
        await using var host = new ApplicationTestHost();
        await host.Dispatcher.SendAsync(OptimizationCommands.FastSlowGrid(), Ct);

        var recovered = await host.Dispatcher.SendAsync(new FailInterruptedOptimizationsCommand(), Ct);

        Assert.Equal(1, recovered.Value);
        Assert.Equal(OptimizationStatus.Failed, host.Database.Optimizations[0].Status);
    }

    [Fact]
    public async Task Check_backtests_the_best_trials_again_on_the_new_period()
    {
        await using var host = new ApplicationTestHost();
        host.Reader.ReportFor = OptimizationCommands.ReportWithNetProfitOfTrialIndex;
        var started = await host.Dispatcher.SendAsync(OptimizationCommands.FastSlowGrid(OptimizationObjective.NetProfit), Ct);
        await host.Jobs.RunAllAsync(host.Dispatcher);
        var trialRequests = host.Engine.Requests.Count;

        // Check runs are normal backtests with timestamp ids: give each its own millisecond.
        host.Reader.ReportFor = TestData.Report;
        host.Clock.AutoAdvanceAmount = TimeSpan.FromMilliseconds(1);
        var queued = await host.Dispatcher.SendAsync(
            new QueueOptimizationCheckCommand(started.Value.OptimizationId, new DateOnly(2025, 1, 2), new DateOnly(2025, 12, 31), Top: 3), Ct);
        var outcome = Assert.Single(await host.Jobs.RunAllAsync(host.Dispatcher));

        Assert.Equal(3, queued.Value.Trials);
        Assert.True(outcome.IsSuccess, outcome.Message);

        var run = host.Database.Optimizations[0];
        Assert.Equal(DateRange.Create(new DateOnly(2025, 1, 2), new DateOnly(2025, 12, 31)), run.CheckPeriod);
        Assert.Equal([5, 4, 3], run.Trials.Where(t => t.CheckStatus == TrialStatus.Completed).Select(t => t.Index).OrderDescending());
        Assert.All(run.Trials.Take(3), t => Assert.Null(t.CheckStatus));

        // Best first, with each trial's parameters, on the check period, and visible in the history.
        var checks = host.Engine.Requests.Skip(trialRequests).ToList();
        Assert.Equal(run.Trials[5].Parameters, checks[0].Parameters);
        Assert.All(checks, r => Assert.Equal(new DateOnly(2025, 1, 2), r.Period.Start));
        Assert.Equal(3, (await host.Dispatcher.SendAsync(new GetBacktestHistoryQuery(), Ct)).Value.Count);

        var details = (await host.Dispatcher.SendAsync(new GetOptimizationQuery(run.Id.Value), Ct)).Value;
        Assert.Equal(new DateOnly(2025, 1, 2), details.CheckStart);
        Assert.NotNull(details.Trials.Single(t => t.Index == 5).CheckScore);

        var best = Assert.Single((await host.Dispatcher.SendAsync(new GetBestStrategyParametersQuery("SmaCrossStrategy"), Ct)).Value);
        Assert.NotNull(best.CheckMetrics);
        Assert.Equal("2025-01-02 – 2025-12-31", best.CheckPeriodText);
    }

    [Fact]
    public async Task Check_on_days_the_optimization_saw_is_rejected()
    {
        await using var host = new ApplicationTestHost();
        var started = await host.Dispatcher.SendAsync(OptimizationCommands.FastSlowGrid(), Ct);
        await host.Jobs.RunAllAsync(host.Dispatcher);

        var result = await host.Dispatcher.SendAsync(
            new QueueOptimizationCheckCommand(started.Value.OptimizationId, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 14)), Ct);

        Assert.Equal(ErrorType.Validation, result.Error?.Type);
        Assert.Contains("overlaps", result.Error!.Message);
        Assert.Empty(host.Jobs.Pending);
    }

    [Fact]
    public async Task An_unfinished_optimization_cannot_be_checked()
    {
        await using var host = new ApplicationTestHost();
        var started = await host.Dispatcher.SendAsync(OptimizationCommands.FastSlowGrid(), Ct);

        var result = await host.Dispatcher.SendAsync(
            new QueueOptimizationCheckCommand(started.Value.OptimizationId, new DateOnly(2025, 1, 2), new DateOnly(2025, 12, 31)), Ct);

        Assert.Equal(ErrorType.Validation, result.Error?.Type);
        Assert.Single(host.Jobs.Pending);  // only the optimization itself
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

internal static class OptimizationCommands
{
    public static StartOptimizationCommand FastSlowGrid(OptimizationObjective objective = OptimizationObjective.SharpeRatio, int maxParallelTrials = 3) =>
        Create(
        [
            new ParameterRangeInput(FakeStrategyCatalog.FastPeriod, 10, 30, 10),
            new ParameterRangeInput(FakeStrategyCatalog.SlowPeriod, 20, 40, 10)
        ], objective: objective, maxParallelTrials: maxParallelTrials);

    public static StartOptimizationCommand Create(
        IReadOnlyList<ParameterRangeInput> ranges,
        IReadOnlyDictionary<string, string>? fixedParameters = null,
        OptimizationObjective objective = OptimizationObjective.SharpeRatio,
        int maxParallelTrials = 3) =>
        new("SmaCrossStrategy", "AAPL", new DateOnly(2026, 1, 2), new DateOnly(2026, 9, 30), 100_000m,
            fixedParameters ?? new Dictionary<string, string>(), ranges, objective, maxParallelTrials);

    /// <summary>Trial <c>k</c> (run id ending in <c>-tk</c>) earns k%: the last trial is the best by net profit.</summary>
    public static BacktestReport ReportWithNetProfitOfTrialIndex(string runId)
    {
        var index = int.Parse(runId[(runId.LastIndexOf("-t", StringComparison.Ordinal) + 2)..], CultureInfo.InvariantCulture);
        return TestData.Report(runId) with
        {
            Equity = [new EquityPoint(new DateTime(2026, 1, 2), 100_000), new EquityPoint(new DateTime(2026, 9, 30), 100_000 + index * 1_000)]
        };
    }
}
