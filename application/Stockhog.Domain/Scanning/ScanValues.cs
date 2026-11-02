using System.Globalization;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;

namespace Stockhog.Domain.Scanning;

/// <summary>Readable, sortable id, e.g. <c>scan-20261102-121500123</c>.</summary>
public sealed record ScanRunId
{
    private const string Prefix = "scan-";

    private ScanRunId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ScanRunId New(DateTimeOffset createdAt) =>
        new($"{Prefix}{createdAt.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)}");

    public static ScanRunId From(string value) =>
        IsValid(value) ? new ScanRunId(value) : throw new DomainException($"'{value}' is not a valid scan id.");

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith(Prefix, StringComparison.Ordinal)
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public override string ToString() => Value;
}

/// <summary>Why a candidate did not pass; a candidate can fail several checks at once.</summary>
[Flags]
public enum ScanRejection
{
    None = 0,

    /// <summary>Too few closed trades for the result to mean much.</summary>
    TooFewTrades = 1,

    /// <summary>Lost money over the whole period.</summary>
    NotProfitable = 2,

    /// <summary>Lost money in one half of the period: the profit came from one lucky stretch.</summary>
    LosingHalf = 4,

    /// <summary>The 95th percentile drawdown of the reshuffled trades is deeper than the limit.</summary>
    DrawdownTooDeep = 8
}

/// <summary>The hard checks every candidate must pass.</summary>
/// <param name="MaxDrawdownPercent">Limit for the Monte Carlo 95th percentile drawdown, e.g. 25 for 25%.</param>
/// <param name="RequireBothHalvesProfitable">Reject candidates that lost money in either half of the period.</param>
public sealed record ScanThresholds(int MinTrades, decimal MaxDrawdownPercent, bool RequireBothHalvesProfitable)
{
    public const int MaxMinTrades = 500;

    public static ScanThresholds Default { get; } = new(6, 25m, true);

    public static ScanThresholds Create(int minTrades, decimal maxDrawdownPercent, bool requireBothHalvesProfitable)
    {
        if (minTrades is < 0 or > MaxMinTrades)
            throw new DomainException($"The minimum number of trades must be between 0 and {MaxMinTrades}.");
        if (maxDrawdownPercent is <= 0 or > 100)
            throw new DomainException("The drawdown limit must be above 0% and at most 100%.");

        return new ScanThresholds(minTrades, maxDrawdownPercent, requireBothHalvesProfitable);
    }

    public ScanRejection Assess(PerformanceMetrics metrics, decimal firstHalfReturnPercent, decimal secondHalfReturnPercent, decimal monteCarloDrawdownPercent)
    {
        var rejections = ScanRejection.None;
        if (metrics.TotalTrades < MinTrades)
            rejections |= ScanRejection.TooFewTrades;
        if (metrics.NetProfitPercent <= 0)
            rejections |= ScanRejection.NotProfitable;
        if (RequireBothHalvesProfitable && (firstHalfReturnPercent <= 0 || secondHalfReturnPercent <= 0))
            rejections |= ScanRejection.LosingHalf;
        if (monteCarloDrawdownPercent > MaxDrawdownPercent)
            rejections |= ScanRejection.DrawdownTooDeep;
        return rejections;
    }
}

/// <summary>
/// How one strategy did on one symbol over the scan period, and whether it passed the scan's checks.
/// </summary>
/// <param name="FirstHalfReturnPercent">Equity change over the first half of the period.</param>
/// <param name="MonteCarloDrawdownPercent">95th percentile of the deepest drawdown when the trades are reshuffled.</param>
/// <param name="BuyAndHoldReturnPercent">The stock's own price change over the period, if known.</param>
public sealed record CandidateEvaluation(
    PerformanceMetrics Metrics,
    decimal FirstHalfReturnPercent,
    decimal SecondHalfReturnPercent,
    decimal MonteCarloDrawdownPercent,
    decimal? BuyAndHoldReturnPercent,
    ScanRejection Rejections)
{
    /// <summary>Drawdowns below this count as this, so a nearly flat strategy does not get an extreme score.</summary>
    private const decimal MinimumDrawdownPercent = 1m;

    public bool Passed => Rejections == ScanRejection.None;

    /// <summary>Return per unit of realistic risk: net profit over the Monte Carlo drawdown. Higher is better.</summary>
    public decimal Score => Math.Round(Metrics.NetProfitPercent / Math.Max(MonteCarloDrawdownPercent, MinimumDrawdownPercent), 4);

    /// <summary>Net profit minus buy and hold; positive when the strategy beat simply holding the stock.</summary>
    public decimal? ExcessOverBuyAndHoldPercent => BuyAndHoldReturnPercent is { } hold ? Metrics.NetProfitPercent - hold : null;
}
