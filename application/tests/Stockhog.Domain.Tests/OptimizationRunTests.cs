using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Optimization;

namespace Stockhog.Domain.Tests;

public class ParameterGridTests
{
    [Fact]
    public void Range_values_run_from_start_to_end_by_step_without_passing_the_end()
    {
        var range = ParameterRange.Create("fast-period", 10, 25, 5);

        Assert.Equal([10m, 15m, 20m, 25m], range.Values());
        Assert.Equal([0.5m, 0.75m], ParameterRange.Create("risk-percent", 0.5m, 0.9m, 0.25m).Values());
    }

    [Fact]
    public void Grid_expands_to_every_combination_with_the_first_range_varying_slowest()
    {
        var grid = ParameterGrid.Create(
        [
            ParameterRange.Create("fast-period", 10, 20, 10),
            ParameterRange.Create("slow-period", 100, 300, 100)
        ]);

        var points = grid.Expand().Select(p => $"{p["fast-period"]}/{p["slow-period"]}").ToList();

        Assert.Equal(6, grid.Combinations);
        Assert.Equal(["10/100", "10/200", "10/300", "20/100", "20/200", "20/300"], points);
    }

    [Fact]
    public void Grid_limits_dimensions_size_and_duplicates()
    {
        Assert.Throws<DomainException>(() => ParameterGrid.Create([]));
        Assert.Throws<DomainException>(() => ParameterGrid.Create(
            [ParameterRange.Create("a", 1, 2, 1), ParameterRange.Create("a", 1, 2, 1)]));
        Assert.Throws<DomainException>(() => ParameterGrid.Create(
        [
            ParameterRange.Create("a", 1, 2, 1), ParameterRange.Create("b", 1, 2, 1),
            ParameterRange.Create("c", 1, 2, 1), ParameterRange.Create("d", 1, 2, 1)
        ]));
        Assert.Throws<DomainException>(() => ParameterGrid.Create(
            [ParameterRange.Create("a", 1, 50, 1), ParameterRange.Create("b", 1, 50, 1)]));
        Assert.Throws<DomainException>(() => ParameterRange.Create("a", 1, 100, 1));
        Assert.Throws<DomainException>(() => ParameterRange.Create("a", 5, 1, 1));
        Assert.Throws<DomainException>(() => ParameterRange.Create("a", 1, 5, 0));
    }

    [Fact]
    public void Trial_run_ids_derive_from_the_optimization_and_stay_unique()
    {
        var optimization = OptimizationRunId.New(Symbol.Create("AAPL"), StrategyName.Create("SmaCrossStrategy"), new DateTimeOffset(2026, 10, 19, 10, 15, 0, 123, TimeSpan.Zero));

        Assert.Equal("opt-20261019-101500123_AAPL_SmaCrossStrategy", optimization.Value);
        Assert.Equal("opt-20261019-101500123_AAPL_SmaCrossStrategy-t007", BacktestRunId.ForTrial(optimization, 7).Value);
        Assert.False(OptimizationRunId.IsValid("20261019_AAPL"));
        Assert.Throws<DomainException>(() => OptimizationRunId.From("opt-../x"));
    }

    [Theory]
    [InlineData(OptimizationObjective.SharpeRatio, 1.5)]
    [InlineData(OptimizationObjective.NetProfit, 12)]
    [InlineData(OptimizationObjective.ReturnOverDrawdown, 3)]
    public void Metrics_are_scored_by_the_objective(OptimizationObjective objective, decimal expected)
    {
        var metrics = new PerformanceMetrics(NetProfitPercent: 12, MaxDrawdownPercent: 4, SharpeRatio: 1.5m, TotalTrades: 10, WinRatePercent: 50, ProfitFactor: 1.2m);

        Assert.Equal(expected, metrics.Score(objective));
    }

    [Fact]
    public void Return_over_drawdown_does_not_explode_on_a_flat_curve()
    {
        var metrics = new PerformanceMetrics(2, 0, 0, 1, 100, 0);

        Assert.Equal(2, metrics.Score(OptimizationObjective.ReturnOverDrawdown));
    }
}

public class OptimizationRunTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_optimization_is_queued_with_one_pending_trial_per_parameter_set()
    {
        var run = Create(3);

        Assert.Equal(OptimizationStatus.Queued, run.Status);
        Assert.Equal([0, 1, 2], run.Trials.Select(t => t.Index));
        Assert.All(run.Trials, t => Assert.Equal(TrialStatus.Pending, t.Status));
        Assert.Empty(run.DomainEvents);
    }

    [Fact]
    public void Completes_when_the_last_trial_finishes_and_picks_the_best_score()
    {
        var run = Create(3);
        run.Start(T0);
        StartAll(run);

        run.CompleteTrial(0, Metrics(sharpe: 0.8m), T0.AddMinutes(1));
        run.FailTrial(1, "engine crashed", T0.AddMinutes(2));
        Assert.Equal(OptimizationStatus.Running, run.Status);

        run.CompleteTrial(2, Metrics(sharpe: 1.4m), T0.AddMinutes(3));

        Assert.Equal(OptimizationStatus.Completed, run.Status);
        Assert.Equal(2, run.BestTrialIndex);
        Assert.Equal(TimeSpan.FromMinutes(3), run.Duration);
        var completed = Assert.IsType<OptimizationRunCompleted>(Assert.Single(run.DomainEvents));
        Assert.Equal(2, completed.BestTrialIndex);
    }

    [Fact]
    public void Ties_go_to_the_earlier_trial()
    {
        var run = Create(2);
        run.Start(T0);
        StartAll(run);

        run.CompleteTrial(1, Metrics(sharpe: 1), T0);
        run.CompleteTrial(0, Metrics(sharpe: 1), T0);

        Assert.Equal(0, run.BestTrialIndex);
    }

    [Fact]
    public void Fails_when_every_trial_failed()
    {
        var run = Create(2);
        run.Start(T0);
        StartAll(run);

        run.FailTrial(0, "no data", T0);
        run.FailTrial(1, "no data", T0);

        Assert.Equal(OptimizationStatus.Failed, run.Status);
        Assert.Null(run.BestTrialIndex);
        Assert.IsType<OptimizationRunFailed>(Assert.Single(run.DomainEvents));
    }

    [Fact]
    public void Cancelling_keeps_finished_trials_and_their_best()
    {
        var run = Create(3);
        run.Start(T0);
        run.StartTrial(0, TrialRunId(run, 0));
        run.StartTrial(1, TrialRunId(run, 1));
        run.CompleteTrial(0, Metrics(sharpe: 0.5m), T0);

        run.Cancel(T0.AddMinutes(1));

        Assert.Equal(OptimizationStatus.Cancelled, run.Status);
        Assert.Equal([TrialStatus.Completed, TrialStatus.Cancelled, TrialStatus.Cancelled], run.Trials.Select(t => t.Status));
        Assert.Equal(0, run.BestTrialIndex);
        Assert.Equal(1, Assert.IsType<OptimizationRunCancelled>(Assert.Single(run.DomainEvents)).FinishedTrials);
    }

    [Fact]
    public void Trials_change_only_while_the_optimization_runs_and_in_order()
    {
        var run = Create(2);

        Assert.Throws<DomainException>(() => run.StartTrial(0, TrialRunId(run, 0)));

        run.Start(T0);
        Assert.Throws<DomainException>(() => run.CompleteTrial(0, Metrics(1), T0));
        Assert.Throws<DomainException>(() => run.StartTrial(5, TrialRunId(run, 5)));

        run.StartTrial(0, TrialRunId(run, 0));
        Assert.Throws<DomainException>(() => run.StartTrial(0, TrialRunId(run, 0)));

        run.Cancel(T0);
        Assert.Throws<DomainException>(() => run.Cancel(T0));
        Assert.Throws<DomainException>(() => run.Fail("x", T0));
    }

    [Fact]
    public void Rejects_duplicate_or_missing_trials_and_bad_parallelism()
    {
        var grid = ParameterGrid.Create([ParameterRange.Create("fast-period", 10, 20, 10)]);
        StrategyParameters P(string fast) => StrategyParameters.Create([new("fast-period", fast)]);

        Assert.Throws<DomainException>(() => Create(grid, [P("10"), P("10")]));
        Assert.Throws<DomainException>(() => Create(grid, []));
        Assert.Throws<DomainException>(() => Create(grid, [P("10"), P("20"), P("30")]));
        Assert.Throws<DomainException>(() => Create(grid, [P("10")], maxParallel: 0));
        Assert.Throws<DomainException>(() => Create(grid, [P("10")], maxParallel: OptimizationRun.MaxParallelTrialsLimit + 1));
    }

    private static readonly DateRange LaterPeriod = DateRange.Create(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 14));

    [Fact]
    public void Check_needs_a_finished_optimization_and_days_it_did_not_see()
    {
        var run = Create(2);
        Assert.Throws<DomainException>(() => run.StartCheck(LaterPeriod, 1));

        run.Start(T0);
        StartAll(run);
        run.CompleteTrial(0, Metrics(sharpe: 1), T0);
        run.CompleteTrial(1, Metrics(sharpe: 2), T0);

        Assert.Throws<DomainException>(() => run.StartCheck(DateRange.Create(new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 14)), 1));
        Assert.Throws<DomainException>(() => run.StartCheck(LaterPeriod, 0));
        Assert.Throws<DomainException>(() => run.StartCheck(LaterPeriod, OptimizationRun.MaxCheckedTrials + 1));
        Assert.Null(run.CheckPeriod);
    }

    [Fact]
    public void Check_takes_the_best_completed_trials_and_records_their_results()
    {
        var run = Create(3);
        run.Start(T0);
        StartAll(run);
        run.CompleteTrial(0, Metrics(sharpe: 0.8m), T0);
        run.FailTrial(1, "no data", T0);
        run.CompleteTrial(2, Metrics(sharpe: 1.4m), T0);

        var checkedTrials = run.StartCheck(LaterPeriod, top: 5);

        Assert.Equal([2, 0], checkedTrials);
        Assert.Equal(LaterPeriod, run.CheckPeriod);
        Assert.Equal([TrialStatus.Pending, null, TrialStatus.Pending], run.Trials.Select(t => t.CheckStatus));
        Assert.Throws<DomainException>(() => run.CompleteTrialCheck(2, CheckRunId, Metrics(0.3m)));

        run.StartTrialCheck(2);
        run.CompleteTrialCheck(2, CheckRunId, Metrics(sharpe: 0.3m));
        run.StartTrialCheck(0);
        run.FailTrialCheck(0, "engine crashed");

        Assert.Equal((TrialStatus.Completed, 0.3m, CheckRunId), (run.Trials[2].CheckStatus!.Value, run.Trials[2].CheckMetrics!.SharpeRatio, run.Trials[2].CheckRunId!));
        Assert.Equal((TrialStatus.Failed, "engine crashed"), (run.Trials[0].CheckStatus!.Value, run.Trials[0].CheckFailureReason));
        Assert.Throws<DomainException>(() => run.StartTrialCheck(1));
        // The optimization's own results are untouched.
        Assert.Equal((OptimizationStatus.Completed, 2), (run.Status, run.BestTrialIndex));
    }

    [Fact]
    public void A_new_check_replaces_the_previous_one_and_cancelling_stops_the_rest()
    {
        var run = Create(2);
        run.Start(T0);
        StartAll(run);
        run.CompleteTrial(0, Metrics(sharpe: 1), T0);
        run.CompleteTrial(1, Metrics(sharpe: 2), T0);
        run.StartCheck(LaterPeriod, 2);
        run.StartTrialCheck(1);
        run.CompleteTrialCheck(1, CheckRunId, Metrics(sharpe: 1));

        var earlier = DateRange.Create(new DateOnly(2025, 1, 2), new DateOnly(2025, 12, 31));
        run.StartCheck(earlier, 2);
        run.StartTrialCheck(1);
        run.CancelCheck();

        Assert.Equal(earlier, run.CheckPeriod);
        Assert.Equal([TrialStatus.Cancelled, TrialStatus.Cancelled], run.Trials.Select(t => t.CheckStatus));
        Assert.All(run.Trials, t => Assert.Null(t.CheckMetrics));
    }

    private static readonly BacktestRunId CheckRunId = BacktestRunId.From("20261015-120000000_AAPL_SmaCrossStrategy");

    private static void StartAll(OptimizationRun run)
    {
        foreach (var trial in run.Trials)
            run.StartTrial(trial.Index, TrialRunId(run, trial.Index));
    }

    private static BacktestRunId TrialRunId(OptimizationRun run, int index) => BacktestRunId.ForTrial(run.Id, index);

    private static PerformanceMetrics Metrics(decimal sharpe) => new(10, 5, sharpe, 12, 50, 1.3m);

    private static OptimizationRun Create(int trials)
    {
        var grid = ParameterGrid.Create([ParameterRange.Create("fast-period", 1, trials, 1)]);
        return Create(grid, Enumerable.Range(1, trials).Select(i => StrategyParameters.Create([new("fast-period", i.ToString())])));
    }

    private static OptimizationRun Create(ParameterGrid grid, IEnumerable<StrategyParameters> trials, int maxParallel = 2)
    {
        var symbol = Symbol.Create("AAPL");
        var strategy = StrategyName.Create("SmaCrossStrategy");
        return OptimizationRun.Create(
            OptimizationRunId.New(symbol, strategy, T0),
            symbol,
            strategy,
            DateRange.Create(new DateOnly(2026, 1, 2), new DateOnly(2026, 9, 30)),
            Money.Usd(100_000),
            grid,
            OptimizationObjective.SharpeRatio,
            maxParallel,
            trials,
            T0);
    }
}
