using BenchmarkDotNet.Attributes;
using Stockhog.Application.Analytics;

namespace Stockhog.Benchmarks;

/// <summary>
/// The same simulation single-threaded and with <c>Parallel.For</c> over seeded blocks. Both produce identical
/// results (see the simulator tests), so the difference is purely the parallel speed-up and its overhead.
/// </summary>
[MemoryDiagnoser]
public class MonteCarloBenchmarks
{
    private const double StartingEquity = 100_000;

    private double[] _profits = [];

    [Params(10_000, 100_000)]
    public int Simulations { get; set; }

    [Params(MonteCarloMethod.Shuffle, MonteCarloMethod.Bootstrap)]
    public MonteCarloMethod Method { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // 100 trades with a slight positive edge, like a typical strategy result.
        var random = new Random(1);
        _profits = Enumerable.Range(0, 100).Select(_ => (random.NextDouble() - 0.45) * 2_000).ToArray();
    }

    [Benchmark(Baseline = true)]
    public MonteCarloResult Sequential() =>
        MonteCarloSimulator.RunSequential(StartingEquity, _profits, new MonteCarloSettings(Simulations, Method, Seed: 42));

    [Benchmark]
    public MonteCarloResult Parallel() =>
        MonteCarloSimulator.Run(StartingEquity, _profits, new MonteCarloSettings(Simulations, Method, Seed: 42));
}

/// <summary>Metrics are calculated once per optimization trial, over every equity point of the run.</summary>
[MemoryDiagnoser]
public class PerformanceCalculatorBenchmarks
{
    private List<Application.Backtesting.EquityPoint> _equity = [];

    [Params(10_000, 100_000)]
    public int Points { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(1);
        var value = 100_000m;
        var time = new DateTime(2025, 1, 2, 9, 30, 0);
        _equity = Enumerable.Range(0, Points)
            .Select(i =>
            {
                value += (decimal)((random.NextDouble() - 0.49) * 200);
                return new Application.Backtesting.EquityPoint(time.AddMinutes(i * 15), value);
            })
            .ToList();
    }

    [Benchmark]
    public decimal MaxDrawdown() => PerformanceCalculator.MaxDrawdownPercent(_equity);

    [Benchmark]
    public decimal SharpeRatio() => PerformanceCalculator.SharpeRatio(_equity);

    [Benchmark]
    public int DrawdownPeriods() => PerformanceCalculator.DrawdownPeriods(_equity, top: 5).Count;
}
