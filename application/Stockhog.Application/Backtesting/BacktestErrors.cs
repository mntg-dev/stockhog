using Stockhog.Application.Abstractions.Results;

namespace Stockhog.Application.Backtesting;

public static class BacktestErrors
{
    public static Error EngineFailed(string reason) =>
        Error.Failure("Backtest.EngineFailed", $"The backtest failed: {reason}");

    public static Error ReportNotFound(string runId) =>
        Error.NotFound("Backtest.ReportNotFound", $"No result was found for run {runId}.");

    public static Error NoResults(string symbol, string strategy) =>
        Error.NotFound("Backtest.NoResults", $"No saved results for {symbol} / {strategy}.");
}

/// <summary>
/// Cache keys and tags shared by the report queries and the handlers that invalidate them.
/// </summary>
internal static class BacktestCacheKeys
{
    public static string Report(string symbol, string runId) => $"backtests:report:{symbol}:{runId}";

    public static string LatestReport(string symbol, string strategy) => $"backtests:latest:{symbol}:{strategy}";

    /// <summary>Everything cached for one symbol and strategy.</summary>
    public static string Tag(string symbol, string strategy) => $"backtests:{symbol}:{strategy}";
}
