using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Scanning;
using Stockhog.Domain.Signals;

namespace Stockhog.Application.Tests.TestSupport;

/// <summary>Finished scans with signals on their candidates, as the proposal commands read them.</summary>
internal static class ProposalScans
{
    /// <summary>TestData.Now is 2026-10-15 08:00 in New York: a signal of the 14th is for today's session.</summary>
    public static readonly DateOnly LastSession = new(2026, 10, 14);

    /// <summary>A finished scan whose pairs all passed, with the given scores and signals, stored in the host.</summary>
    public static ScanRun Given(ApplicationTestHost host, params (string Symbol, string Strategy, decimal Score, StrategySignal Signal)[] pairs)
    {
        var scan = ScanRun.Create(
            ScanRunId.New(TestData.Now),
            pairs.Select(p => Symbol.Create(p.Symbol)).ToList(),
            pairs.Select(p => StrategyName.Create(p.Strategy)).Distinct().ToList(),
            DateRange.Create(LastSession.AddYears(-2), LastSession),
            Money.Usd(100_000m),
            ScanThresholds.Default,
            2,
            null,
            TestData.Now);
        scan.Start(TestData.Now);

        foreach (var candidate in scan.Candidates)
        {
            scan.StartCandidate(candidate.Index);
            var pair = pairs.FirstOrDefault(p => p.Symbol == candidate.Symbol.Value && p.Strategy == candidate.Strategy.Value);
            if (pair.Symbol is null)
            {
                scan.FailCandidate(candidate.Index, "Not part of the test", TestData.Now);
                continue;
            }

            // Score = net profit / Monte Carlo drawdown, so a 10% drawdown gives net profit = 10 × score.
            var metrics = new PerformanceMetrics(pair.Score * 10m, 8m, 1m, 12, 55m, 1.5m);
            scan.CompleteCandidate(candidate.Index, BacktestRunId.From($"run-{candidate.Index}"),
                new CandidateEvaluation(metrics, 5m, 5m, 10m, null, ScanRejection.None), TestData.Now);
            scan.RecordSignal(candidate.Index, pair.Signal, TestData.Now);
        }

        host.Scans.Add(scan);
        return scan;
    }

    public static StrategySignal Signal(SignalKind kind, decimal price, decimal? stop, DateOnly? asOf = null) =>
        new(kind, asOf ?? LastSession, price, kind is SignalKind.Entry or SignalKind.Holding ? 10m : 0m, price,
            (asOf ?? LastSession).ToDateTime(new TimeOnly(16, 0)), "Entry", stop, null, null, 0, "run-signal");

    public static StrategySignal Entry(decimal price, decimal? stop) => Signal(SignalKind.Entry, price, stop);
}
