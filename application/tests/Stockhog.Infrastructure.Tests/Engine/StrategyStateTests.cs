using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Stockhog.Infrastructure.Configuration;
using Stockhog.Infrastructure.Lean.Engine.Contracts;
using Stockhog.Infrastructure.Lean.Engine.Results;
using Stockhog.Infrastructure.Tests.TestSupport;
using Stockhog.Strategies;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Tests.Engine;

public class StrategyStateTests
{
    private const string RunId = "20261102-120000000_SPY_RsiPullbackStrategy";

    [Fact]
    public void The_state_file_goes_to_the_strategy_with_the_other_parameters()
    {
        var job = new BacktestJob(RunId, "RsiPullbackStrategy", "SPY", new DateOnly(2025, 11, 1), new DateOnly(2026, 11, 2), 100_000m,
            "data", "results", new Dictionary<string, string> { ["rsi-period"] = "4" }, BacktestJob.StateFilePath("results", RunId));

        var parameters = job.ToAlgorithmParameters();

        Assert.Equal(Path.Combine("results", $"{RunId}-state.json"), parameters[EngineParameters.StateFile]);
        Assert.Equal("4", parameters["rsi-period"]);
        Assert.DoesNotContain(EngineParameters.StateFile, (job with { StateFile = null }).ToAlgorithmParameters().Keys);
    }

    [Fact]
    public async Task A_state_written_by_a_strategy_is_read_back_for_its_run()
    {
        using var results = new TempDirectory();
        Directory.CreateDirectory(results.Combine("SPY"));
        var written = new StrategyState(
            new DateTime(2026, 11, 2), new DateTime(2026, 10, 30, 16, 0, 0), 681.5m, 130m, 676.2m,
            new DateTime(2026, 10, 29, 9, 31, 0), "RSI(4) 12.3 in uptrend", null, new DateTime(2026, 9, 15, 9, 31, 0), "Close above 5-day SMA");
        written.Save(BacktestJob.StateFilePath(results.Combine("SPY"), RunId));
        var reader = new StrategyStateReader(Options.Create(new StorageOptions { DataFolder = results.Path, ResultsFolder = results.Path }));

        var state = await reader.ReadAsync(Symbol.Create("SPY"), BacktestRunId.From(RunId), TestContext.Current.CancellationToken);
        var missing = await reader.ReadAsync(Symbol.Create("SPY"), BacktestRunId.From("other-run"), TestContext.Current.CancellationToken);

        Assert.NotNull(state);
        Assert.Equal((130m, 676.2m, "RSI(4) 12.3 in uptrend"), (state.Quantity, state.AveragePrice, state.EntryTag));
        Assert.Equal((written.EntryTime, written.LastBarTime, written.LastExitTime), (state.EntryTime, state.LastBarTime, state.LastExitTime));
        Assert.Null(state.StopPrice);
        Assert.Null(missing);
    }
}
