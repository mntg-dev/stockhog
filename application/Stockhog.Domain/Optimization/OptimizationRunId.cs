using System.Globalization;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Domain.Optimization;

/// <summary>
/// Readable, sortable id, e.g. <c>opt-20261019-101500123_AAPL_SmaCrossStrategy</c>.
/// Trial runs derive their backtest run ids from it (see <see cref="BacktestRunId.ForTrial"/>).
/// </summary>
public sealed record OptimizationRunId
{
    private const string Prefix = "opt-";

    private OptimizationRunId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static OptimizationRunId New(Symbol symbol, StrategyName strategy, DateTimeOffset createdAt) =>
        new($"{Prefix}{createdAt.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)}_{symbol.Value}_{strategy.Value}");

    /// <summary>Restores an existing id. Only characters that are safe in file names are accepted.</summary>
    public static OptimizationRunId From(string value) =>
        IsValid(value) ? new OptimizationRunId(value) : throw new DomainException($"'{value}' is not a valid optimization id.");

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith(Prefix, StringComparison.Ordinal)
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public override string ToString() => Value;
}
