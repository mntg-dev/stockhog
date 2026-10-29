using System.ComponentModel;

namespace Stockhog.Application.Analytics;

public enum MonteCarloMethod
{
    /// <summary>
    /// Every trade exactly once, in random order. The final result is fixed; the spread of drawdowns shows how much
    /// of the historical drawdown was luck of the order.
    /// </summary>
    Shuffle,

    /// <summary>Trades drawn with replacement: final results vary too, as if another sample of the same trades had happened.</summary>
    Bootstrap
}

/// <param name="Seed">Same seed, same results, however many threads run the simulation.</param>
/// <param name="SamplePaths">How many simulated equity paths to return for charting.</param>
public sealed record MonteCarloSettings(int Simulations, MonteCarloMethod Method, int Seed, int SamplePaths = 25);

/// <param name="FinalReturnPercent">Distribution of the return after the last trade, in percent.</param>
/// <param name="MaxDrawdownPercent">Distribution of each simulation's deepest drawdown, as a positive percentage.</param>
/// <param name="SamplePaths">Equity after each trade (starting value first) of the first simulations.</param>
/// <param name="OriginalPath">The same for the historical order of trades.</param>
[ImmutableObject(true)]
public sealed record MonteCarloResult(
    int Simulations,
    int Trades,
    MonteCarloMethod Method,
    Percentiles FinalReturnPercent,
    Percentiles MaxDrawdownPercent,
    double AverageReturnPercent,
    double ProbabilityOfLossPercent,
    double OriginalReturnPercent,
    double OriginalMaxDrawdownPercent,
    IReadOnlyList<HistogramBin> DrawdownHistogram,
    IReadOnlyList<IReadOnlyList<double>> SamplePaths,
    IReadOnlyList<double> OriginalPath);

public sealed record Percentiles(double P5, double P25, double P50, double P75, double P95)
{
    /// <param name="sorted">Ascending values.</param>
    internal static Percentiles FromSorted(double[] sorted) =>
        new(At(sorted, 0.05), At(sorted, 0.25), At(sorted, 0.50), At(sorted, 0.75), At(sorted, 0.95));

    /// <summary>Linear interpolation between the closest ranks.</summary>
    private static double At(double[] sorted, double quantile)
    {
        var position = quantile * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}

public readonly record struct HistogramBin(double From, double To, int Count);

/// <summary>
/// Monte Carlo resampling of closed trades. Profits are applied as amounts (the sizes the strategy actually traded),
/// not compounded.
/// </summary>
/// <remarks>
/// CPU-bound and parallel. Simulations are processed in fixed blocks; each block seeds its own generator from the
/// seed and the block number, and writes only its own slots of the result arrays. Counters are kept per thread
/// (<c>Parallel.For</c> local state) and merged once per thread, so threads never contend while simulating.
/// The output is therefore identical for any degree of parallelism, which the tests check against
/// <see cref="RunSequential"/>.
/// </remarks>
public static class MonteCarloSimulator
{
    public const int BlockSize = 256;
    public const double HistogramBinPercent = 2.5;
    private const int HistogramBins = 40; // 0..100%; deeper drawdowns go into the last bin.

    public static MonteCarloResult Run(double startingEquity, IReadOnlyList<double> tradeProfits, MonteCarloSettings settings, CancellationToken cancellationToken = default) =>
        Simulate(startingEquity, tradeProfits, settings, parallel: true, cancellationToken);

    /// <summary>Single-threaded baseline with the same output, for tests and benchmarks.</summary>
    public static MonteCarloResult RunSequential(double startingEquity, IReadOnlyList<double> tradeProfits, MonteCarloSettings settings) =>
        Simulate(startingEquity, tradeProfits, settings, parallel: false, CancellationToken.None);

    private static MonteCarloResult Simulate(double startingEquity, IReadOnlyList<double> tradeProfits, MonteCarloSettings settings, bool parallel, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startingEquity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.Simulations);
        if (tradeProfits.Count == 0)
            throw new ArgumentException("At least one trade is needed.", nameof(tradeProfits));

        var work = new SimulationWork(startingEquity, tradeProfits.ToArray(), settings);
        var totals = new Accumulator(0);
        var blocks = (settings.Simulations + BlockSize - 1) / BlockSize;

        if (parallel)
        {
            Parallel.For(
                0,
                blocks,
                new ParallelOptions { CancellationToken = cancellationToken },
                localInit: () => new Accumulator(work.Profits.Length),
                body: (block, _, local) =>
                {
                    work.SimulateBlock(block, local);
                    return local;
                },
                localFinally: local =>
                {
                    lock (totals)
                        totals.Add(local);
                });
        }
        else
        {
            var local = new Accumulator(work.Profits.Length);
            for (var block = 0; block < blocks; block++)
                work.SimulateBlock(block, local);
            totals.Add(local);
        }

        var original = Path(startingEquity, work.Profits);
        Array.Sort(work.FinalReturns);
        var averageReturn = work.FinalReturns.Average();
        Array.Sort(work.MaxDrawdowns);

        return new MonteCarloResult(
            settings.Simulations,
            work.Profits.Length,
            settings.Method,
            Percentiles.FromSorted(work.FinalReturns),
            Percentiles.FromSorted(work.MaxDrawdowns),
            averageReturn,
            totals.Losses * 100.0 / settings.Simulations,
            (original[^1] / startingEquity - 1) * 100,
            MaxDrawdown(original) * 100,
            totals.Histogram.Select((count, bin) => new HistogramBin(bin * HistogramBinPercent, (bin + 1) * HistogramBinPercent, count)).ToList(),
            work.Paths,
            original);
    }

    private static double[] Path(double start, double[] profits)
    {
        var path = new double[profits.Length + 1];
        path[0] = start;
        for (var i = 0; i < profits.Length; i++)
            path[i + 1] = path[i] + profits[i];
        return path;
    }

    private static double MaxDrawdown(double[] path)
    {
        double peak = path[0], worst = 0;
        foreach (var equity in path)
        {
            if (equity > peak)
                peak = equity;
            else
                worst = Math.Max(worst, (peak - equity) / peak);
        }

        return worst;
    }

    /// <summary>Shared, read-only inputs plus result arrays that each block writes at its own indexes.</summary>
    private sealed class SimulationWork(double start, double[] profits, MonteCarloSettings settings)
    {
        public double[] Profits { get; } = profits;
        public double[] FinalReturns { get; } = new double[settings.Simulations];
        public double[] MaxDrawdowns { get; } = new double[settings.Simulations];

        public double[][] Paths { get; } = Enumerable.Range(0, Math.Min(settings.SamplePaths, settings.Simulations))
            .Select(_ => new double[profits.Length + 1])
            .ToArray();

        public void SimulateBlock(int block, Accumulator local)
        {
            var random = new Random(unchecked(settings.Seed * 1_000_003 + block));
            var order = local.Order;

            // The scratch array is reused across blocks of one thread: reset it so a block's sequence
            // depends only on its own seed.
            for (var k = 0; k < order.Length; k++)
                order[k] = k;

            var first = block * BlockSize;
            var last = Math.Min(settings.Simulations, first + BlockSize);
            for (var simulation = first; simulation < last; simulation++)
            {
                if (settings.Method == MonteCarloMethod.Shuffle)
                    random.Shuffle(order);

                var path = simulation < Paths.Length ? Paths[simulation] : null;
                double equity = start, peak = start, worst = 0;
                if (path is not null)
                    path[0] = start;

                for (var k = 0; k < Profits.Length; k++)
                {
                    equity += Profits[settings.Method == MonteCarloMethod.Shuffle ? order[k] : random.Next(Profits.Length)];
                    if (equity > peak)
                        peak = equity;
                    else if ((peak - equity) / peak > worst)
                        worst = (peak - equity) / peak;

                    if (path is not null)
                        path[k + 1] = equity;
                }

                var finalReturn = (equity / start - 1) * 100;
                FinalReturns[simulation] = finalReturn;
                MaxDrawdowns[simulation] = worst * 100;
                local.Record(finalReturn, worst * 100);
            }
        }
    }

    /// <summary>Per-thread counters and scratch space; merged into the totals once per thread.</summary>
    private sealed class Accumulator(int trades)
    {
        public int[] Order { get; } = new int[trades];
        public int[] Histogram { get; } = new int[HistogramBins];
        public int Losses { get; private set; }

        public void Record(double finalReturnPercent, double maxDrawdownPercent)
        {
            if (finalReturnPercent < 0)
                Losses++;

            Histogram[Math.Clamp((int)(maxDrawdownPercent / HistogramBinPercent), 0, HistogramBins - 1)]++;
        }

        public void Add(Accumulator other)
        {
            Losses += other.Losses;
            for (var bin = 0; bin < HistogramBins; bin++)
                Histogram[bin] += other.Histogram[bin];
        }
    }
}
