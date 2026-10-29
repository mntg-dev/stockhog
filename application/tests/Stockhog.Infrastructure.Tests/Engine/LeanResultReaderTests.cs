using Stockhog.Application.Backtesting;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;
using Stockhog.Infrastructure.Configuration;
using Stockhog.Infrastructure.Lean.Engine.Results;
using Stockhog.Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Tests.Engine;

public class LeanResultReaderTests
{
    // Trimmed copy of a real Lean result file: candlestick equity series, string-encoded numbers, order tags.
    private const string ResultJson = """
        {
          "statistics": { "Sharpe Ratio": "0.896", "Drawdown": "7.500%" },
          "charts": {
            "Strategy Equity": {
              "series": {
                "Equity": { "seriesType": 2, "values": [[1756785600, 1000000.0, 1000100.0, 999000.0, 1000050.0], [1756872000, 1000050.0, 1002000.0, 1000000.0, 1001500.5]] },
                "Return": { "seriesType": 3, "values": [[1756785600, 0.0]] }
              }
            }
          },
          "orders": {
            "1": { "id": 1, "tag": "" },
            "2": { "id": 2, "tag": "Trailing Stop Short" }
          },
          "totalPerformance": {
            "tradeStatistics": { "totalNumberOfTrades": 521, "profitFactor": "1.3492", "maxConsecutiveWinningTrades": 8, "maxConsecutiveLosingTrades": 11 },
            "closedTrades": [
              {
                "symbols": [{ "value": "AAPL", "id": "AAPL 2T" }],
                "entryTime": "2025-09-02T16:15:00Z", "entryPrice": 227.84, "direction": 1, "quantity": -100.0,
                "exitTime": "2025-09-02T16:30:00Z", "exitPrice": 226.84, "profitLoss": 100.0, "totalFees": 2.5,
                "orderIds": [1, 2]
              }
            ]
          }
        }
        """;

    [Fact]
    public async Task Reads_statistics_equity_and_trades()
    {
        using var results = new TempDirectory();
        var runId = BacktestRunId.From("20261015-120000000_AAPL_SmaCrossStrategy");
        WriteResult(results, "AAPL", runId.Value + ".json");

        var report = await CreateReader(results).ReadAsync(Symbol.Create("AAPL"), runId, TestContext.Current.CancellationToken);

        Assert.NotNull(report);
        Assert.Equal(runId.Value, report.RunId);
        Assert.Equal("0.896", report.Statistics["Sharpe Ratio"]);
        Assert.Equal(new TradeStatistics(521, 1.3492m, 8, 11), report.TradeStatistics);

        // Candlestick values: the close (last element) is used; time is shown in exchange time.
        Assert.Equal(2, report.Equity.Count);
        Assert.Equal(1001500.5m, report.Equity[1].Value);
        Assert.Equal(new DateTime(2025, 9, 3, 0, 0, 0), report.Equity[1].Time);

        var trade = Assert.Single(report.ClosedTrades);
        Assert.Equal(1, trade.Number);
        Assert.Equal("AAPL", trade.Symbol);
        Assert.Equal(TradeDirection.Short, trade.Direction);
        Assert.Equal(new DateTime(2025, 9, 2, 12, 15, 0), trade.EntryTime);
        Assert.Equal("Trailing Stop Short", trade.ExitReason);
        Assert.Equal(100m / 22784m, trade.ProfitLossPercent);
    }

    [Fact]
    public async Task Latest_result_ignores_sibling_files_and_other_strategies()
    {
        using var results = new TempDirectory();
        var older = WriteResult(results, "AAPL", "2025-10-05_AAPL_SmaCrossStrategy.json");
        var newer = WriteResult(results, "AAPL", "20261015-120000000_AAPL_SmaCrossStrategy.json");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));
        // Written later, but none of these are result files for this strategy.
        WriteResult(results, "AAPL", "20261015-120000000_AAPL_SmaCrossStrategy-summary.json");
        WriteResult(results, "AAPL", "20261015-120000000_AAPL_SmaCrossStrategy-job.json");
        WriteResult(results, "AAPL", "20261016-120000000_AAPL_OtherStrategy.json");

        Assert.Equal(newer, LeanResultReader.FindLatestResultFile(results.Path, "AAPL", "SmaCrossStrategy"));

        var report = await CreateReader(results).ReadLatestAsync(Symbol.Create("aapl"), StrategyName.Create("SmaCrossStrategy"), TestContext.Current.CancellationToken);
        Assert.Equal("20261015-120000000_AAPL_SmaCrossStrategy", report?.RunId);
    }

    [Fact]
    public async Task Missing_run_returns_null()
    {
        using var results = new TempDirectory();

        var report = await CreateReader(results).ReadAsync(
            Symbol.Create("AAPL"), BacktestRunId.From("missing_AAPL_SmaCrossStrategy"), TestContext.Current.CancellationToken);

        Assert.Null(report);
    }

    [Fact]
    public async Task Latest_result_is_null_when_nothing_was_saved()
    {
        using var results = new TempDirectory();

        Assert.Null(await CreateReader(results).ReadLatestAsync(Symbol.Create("MSFT"), StrategyName.Create("SmaCrossStrategy"), TestContext.Current.CancellationToken));
    }

    private static LeanResultReader CreateReader(TempDirectory results) =>
        new(Options.Create(new StorageOptions { DataFolder = results.Path, ResultsFolder = results.Path }));

    private static string WriteResult(TempDirectory results, string symbol, string fileName)
    {
        Directory.CreateDirectory(results.Combine(symbol));
        var path = results.Combine(symbol, fileName);
        File.WriteAllText(path, ResultJson);
        return path;
    }
}
