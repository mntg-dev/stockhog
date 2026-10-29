using System.Globalization;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Optimization;

namespace Stockhog.Domain.Backtesting;

/// <summary>
/// Readable, sortable run id, e.g. <c>20261015-233403029_AAPL_SmaCrossStrategy</c>. It is also the result file name.
/// </summary>
public sealed record BacktestRunId
{
    private BacktestRunId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static BacktestRunId New(Symbol symbol, StrategyName strategy, DateTimeOffset createdAt) =>
        new($"{createdAt.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)}_{symbol.Value}_{strategy.Value}");

    /// <summary>
    /// Id of one trial of an optimization, e.g. <c>opt-20261019-101500123_AAPL_SmaCrossStrategy-t007</c>. Unique because
    /// the optimization id is, even when trials start in the same millisecond; the suffix also keeps trial results out
    /// of "latest result" lookups, which match <c>_{SYMBOL}_{Strategy}.json</c>.
    /// </summary>
    public static BacktestRunId ForTrial(OptimizationRunId optimization, int trialIndex) =>
        new($"{optimization.Value}-t{trialIndex.ToString("D3", CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Restores an existing id. Only characters that are safe in file names are accepted.
    /// </summary>
    public static BacktestRunId From(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? new BacktestRunId(value)
            : throw new DomainException($"'{value}' is not a valid backtest run id.");

    public override string ToString() => Value;
}
