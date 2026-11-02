using Stockhog.Domain.Common;
using Stockhog.Domain.Trading;

namespace Stockhog.Domain.Proposals;

/// <summary>How a proposed position is sized.</summary>
/// <param name="RiskPercent">Equity lost if the stop is hit, e.g. 1 for 1%.</param>
/// <param name="MaxPositionPercent">Largest share of equity in one position, e.g. 20 for 20%.</param>
/// <param name="FallbackStopPercent">Stop below the price for strategies that have none, e.g. 8 for 8%.</param>
public sealed record SizingRules(decimal RiskPercent, decimal MaxPositionPercent, decimal FallbackStopPercent)
{
    /// <summary>A stop further than this below the price is treated as missing: the risk would be mostly unprotected.</summary>
    public const decimal MaxStopPercent = 30m;

    public static SizingRules Default { get; } = new(1m, 20m, 8m);

    public static SizingRules Create(decimal riskPercent, decimal maxPositionPercent, decimal fallbackStopPercent)
    {
        if (riskPercent is <= 0 or > 10)
            throw new DomainException("The risk per trade must be above 0% and at most 10% of equity.");
        if (maxPositionPercent is <= 0 or > 100)
            throw new DomainException("The largest position must be above 0% and at most 100% of equity.");
        if (fallbackStopPercent is <= 0 or > MaxStopPercent)
            throw new DomainException($"The fallback stop must be above 0% and at most {MaxStopPercent}% below the price.");

        return new SizingRules(riskPercent, maxPositionPercent, fallbackStopPercent);
    }
}

/// <summary>What limited the size: the smallest of the four caps wins.</summary>
public enum SizeLimit
{
    Risk,
    PositionShare,
    RiskLimits,
    BuyingPower
}

/// <param name="Quantity">Whole shares; 0 when not even one share fits.</param>
/// <param name="StopPrice">The strategy's stop, or the fallback one when it had none (or an unusable one).</param>
/// <param name="RiskAmount">Loss in USD if the stop is hit: quantity × (price − stop).</param>
public sealed record PositionSize(decimal Quantity, decimal StopPrice, decimal RiskAmount, SizeLimit LimitedBy, bool UsesFallbackStop);

/// <summary>
/// Sizes a long position so that hitting the stop costs at most a set share of equity, then caps it by the share of
/// equity one position may take, by the account's risk limits and by the buying power. Whole shares only, because the
/// broker-side stop loss (OTO) does not accept fractions.
/// </summary>
public static class PositionSizer
{
    public static PositionSize Size(decimal equity, decimal buyingPower, decimal price, decimal? strategyStop, SizingRules rules, RiskLimits limits)
    {
        if (price <= 0)
            throw new DomainException("The price must be positive.");

        var usable = strategyStop is { } stop && stop < price && stop >= price * (1 - SizingRules.MaxStopPercent / 100m);
        var stopPrice = usable ? strategyStop!.Value : Math.Round(price * (1 - rules.FallbackStopPercent / 100m), 2);
        var riskPerShare = price - stopPrice;

        var caps = new (decimal Shares, SizeLimit Limit)[]
        {
            (Shares(equity * rules.RiskPercent / 100m, riskPerShare), SizeLimit.Risk),
            (Shares(equity * rules.MaxPositionPercent / 100m, price), SizeLimit.PositionShare),
            (Shares(Math.Min(limits.MaxOrderNotional, limits.MaxPositionNotional), price), SizeLimit.RiskLimits),
            (Shares(buyingPower, price), SizeLimit.BuyingPower)
        };
        var (quantity, limitedBy) = caps.MinBy(c => c.Shares);

        return new PositionSize(quantity, stopPrice, Math.Round(quantity * riskPerShare, 2), limitedBy, !usable);
    }

    private static decimal Shares(decimal amount, decimal perShare) => perShare <= 0 || amount <= 0 ? 0 : decimal.Floor(amount / perShare);
}
