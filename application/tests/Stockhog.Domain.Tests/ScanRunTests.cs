using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Scanning;

namespace Stockhog.Domain.Tests;

public class ScanRunTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly PerformanceMetrics Good = new(20m, 8m, 1.2m, 12, 55m, 1.6m);

    [Fact]
    public void Creates_one_candidate_per_symbol_and_strategy_without_duplicates()
    {
        var scan = Create(["AAPL", "MSFT", "AAPL"], ["SmaCrossStrategy", "RsiPullbackStrategy"]);

        Assert.Equal(ScanStatus.Queued, scan.Status);
        Assert.Equal(
            ["AAPL/SmaCrossStrategy", "AAPL/RsiPullbackStrategy", "MSFT/SmaCrossStrategy", "MSFT/RsiPullbackStrategy"],
            scan.Candidates.Select(c => $"{c.Symbol}/{c.Strategy}"));
        Assert.Equal([0, 1, 2, 3], scan.Candidates.Select(c => c.Index));
        Assert.Equal(2, scan.Symbols.Count);
    }

    [Theory]
    [InlineData(0, 1, 3)]     // no symbols
    [InlineData(101, 1, 3)]   // too many symbols
    [InlineData(1, 0, 3)]     // no strategies
    [InlineData(1, 1, 0)]     // nothing in parallel
    [InlineData(1, 1, 9)]     // too much in parallel
    public void Rejects_scans_outside_the_limits(int symbols, int strategies, int maxParallel)
    {
        var symbolList = Enumerable.Range(0, symbols).Select(i => $"S{i}").ToList();
        var strategyList = Enumerable.Range(0, strategies).Select(i => $"Strategy{i}").ToList();

        Assert.Throws<DomainException>(() => Create(symbolList, strategyList, maxParallel));
    }

    [Fact]
    public void Completes_when_every_candidate_finished_and_one_was_evaluated()
    {
        var scan = Create(["AAPL", "MSFT"], ["SmaCrossStrategy"]);
        scan.Start(T0);

        scan.StartCandidate(0);
        scan.CompleteCandidate(0, BacktestRunId.From("run-1"), Evaluation(Good), T0.AddMinutes(1));
        Assert.Equal(ScanStatus.Running, scan.Status);

        scan.FailCandidate(1, "No market data", T0.AddMinutes(2));

        Assert.Equal(ScanStatus.Completed, scan.Status);
        Assert.Equal(T0.AddMinutes(2), scan.FinishedAt);
        Assert.Equal(1, scan.PassedCandidates);
        Assert.Equal("run-1", scan.Candidates[0].RunId?.Value);
        var finished = Assert.IsType<ScanRunFinished>(Assert.Single(scan.DomainEvents));
        Assert.Equal((ScanStatus.Completed, 1), (finished.Status, finished.PassedCandidates));
    }

    [Fact]
    public void Fails_when_no_candidate_could_be_evaluated()
    {
        var scan = Create(["AAPL"], ["SmaCrossStrategy"]);
        scan.Start(T0);

        scan.FailCandidate(0, "No market data", T0);

        Assert.Equal(ScanStatus.Failed, scan.Status);
        Assert.Equal("No candidate could be evaluated.", scan.FailureReason);
    }

    [Fact]
    public void Cancelling_keeps_evaluated_candidates_and_cancels_the_rest()
    {
        var scan = Create(["AAPL", "MSFT", "SPY"], ["SmaCrossStrategy"]);
        scan.Start(T0);
        scan.StartCandidate(0);
        scan.CompleteCandidate(0, BacktestRunId.From("run-1"), Evaluation(Good), T0);
        scan.StartCandidate(1);

        scan.Cancel(T0.AddMinutes(1));

        Assert.Equal(ScanStatus.Cancelled, scan.Status);
        Assert.Equal([CandidateStatus.Evaluated, CandidateStatus.Cancelled, CandidateStatus.Cancelled], scan.Candidates.Select(c => c.Status));
        Assert.Throws<DomainException>(() => scan.Fail("late", T0));
    }

    [Fact]
    public void Candidates_change_only_while_the_scan_runs()
    {
        var scan = Create(["AAPL"], ["SmaCrossStrategy"]);

        Assert.Throws<DomainException>(() => scan.StartCandidate(0));
        scan.Start(T0);
        Assert.Throws<DomainException>(() => scan.Start(T0));
        Assert.Throws<DomainException>(() => scan.CompleteCandidate(0, BacktestRunId.From("run-1"), Evaluation(Good), T0)); // not started
        Assert.Throws<DomainException>(() => scan.StartCandidate(5));
    }

    [Fact]
    public void Signals_are_recorded_on_evaluated_candidates_even_after_the_scan_finished()
    {
        var scan = Create(["AAPL", "MSFT"], ["SmaCrossStrategy"]);
        scan.Start(T0);
        scan.StartCandidate(0);
        scan.CompleteCandidate(0, BacktestRunId.From("run-1"), Evaluation(Good), T0);
        scan.FailCandidate(1, "No market data", T0);
        var signal = Signals.StrategySignal.Classify(new DateOnly(2026, 10, 30), 100m, 0, 0, null, null, null, null, null, "run-2");

        scan.RecordSignal(0, signal, T0.AddDays(1));

        Assert.Equal(ScanStatus.Completed, scan.Status);
        Assert.Equal(signal, scan.Candidates[0].Signal);
        Assert.Equal(T0.AddDays(1), scan.Candidates[0].SignalCheckedAt);
        Assert.Throws<DomainException>(() => scan.RecordSignal(1, signal, T0));
    }

    [Fact]
    public void Thresholds_flag_every_failed_check()
    {
        var thresholds = ScanThresholds.Create(minTrades: 6, maxDrawdownPercent: 25m, requireBothHalvesProfitable: true);

        Assert.Equal(ScanRejection.None, thresholds.Assess(Good, 5m, 10m, 12m));
        Assert.Equal(
            ScanRejection.TooFewTrades | ScanRejection.NotProfitable | ScanRejection.LosingHalf | ScanRejection.DrawdownTooDeep,
            thresholds.Assess(Good with { TotalTrades = 3, NetProfitPercent = -2m }, 4m, -6m, 30m));
        Assert.Equal(ScanRejection.None, (thresholds with { RequireBothHalvesProfitable = false }).Assess(Good, -1m, 21m, 12m));

        Assert.Throws<DomainException>(() => ScanThresholds.Create(-1, 25m, true));
        Assert.Throws<DomainException>(() => ScanThresholds.Create(6, 0m, true));
    }

    [Fact]
    public void Score_is_profit_over_the_monte_carlo_drawdown()
    {
        var evaluation = new CandidateEvaluation(Good, 8m, 11m, 10m, 25m, ScanRejection.None);

        Assert.True(evaluation.Passed);
        Assert.Equal(2m, evaluation.Score);                             // 20% / 10%
        Assert.Equal(-5m, evaluation.ExcessOverBuyAndHoldPercent);     // 20% vs 25% for holding
        Assert.Equal(20m, (evaluation with { MonteCarloDrawdownPercent = 0.2m }).Score); // floored at 1%
    }

    private static CandidateEvaluation Evaluation(PerformanceMetrics metrics) => new(metrics, 8m, 11m, 10m, null, ScanRejection.None);

    private static ScanRun Create(IReadOnlyList<string> symbols, IReadOnlyList<string> strategies, int maxParallel = 3) =>
        ScanRun.Create(
            ScanRunId.New(T0),
            symbols.Select(Symbol.Create).ToList(),
            strategies.Select(StrategyName.Create).ToList(),
            DateRange.Create(new DateOnly(2024, 11, 1), new DateOnly(2026, 10, 30)),
            Money.Usd(100_000m),
            ScanThresholds.Default,
            maxParallel,
            screenerRunId: null,
            T0);
}
