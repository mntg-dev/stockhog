using Stockhog.Domain.MarketData;

namespace Stockhog.Domain.Trading;

/// <summary>
/// Limits every order must satisfy before it is sent to the broker.
/// </summary>
/// <param name="TradingEnabled">The kill switch: when off, every order is refused (closing positions is still allowed).</param>
/// <param name="MaxOrderNotional">Largest value of a single order, in USD.</param>
/// <param name="MaxPositionNotional">Largest value of the position in one symbol after the order, in USD.</param>
/// <param name="MaxDailyLoss">Once the day's loss reaches this, new orders that increase exposure are refused.</param>
/// <param name="MaxOpenOrders">Most orders that may be working at the broker at once.</param>
public sealed record RiskLimits(
    bool TradingEnabled,
    decimal MaxOrderNotional,
    decimal MaxPositionNotional,
    decimal MaxDailyLoss,
    int MaxOpenOrders)
{
    public static RiskLimits Default { get; } = new(true, 10_000m, 25_000m, 1_000m, 10);
}

/// <summary>An order about to be submitted, with the price it is expected to execute at.</summary>
/// <param name="ReferencePrice">Limit price, or the latest quote for market and stop orders.</param>
public sealed record OrderIntent(Symbol Symbol, OrderSide Side, decimal Quantity, decimal ReferencePrice);

/// <summary>The account as the risk checks need it.</summary>
/// <param name="CurrentPositionQuantity">Signed: negative for a short position.</param>
/// <param name="DailyProfitLoss">Equity now minus equity at the previous close; negative is a loss.</param>
public sealed record RiskContext(
    decimal CurrentPositionQuantity,
    decimal DailyProfitLoss,
    int OpenOrders,
    decimal BuyingPower);

public sealed record RiskDecision(IReadOnlyList<string> Violations)
{
    public bool IsAllowed => Violations.Count == 0;
}

/// <summary>
/// Pre-trade risk checks. Orders that only reduce an existing position are always allowed, except by the order
/// count limit, so a position can be closed when limits are hit or trading is switched off.
/// </summary>
public static class RiskPolicy
{
    public static RiskDecision Evaluate(OrderIntent order, RiskContext context, RiskLimits limits)
    {
        var violations = new List<string>();
        var signedQuantity = order.Side == OrderSide.Buy ? order.Quantity : -order.Quantity;
        var positionAfter = context.CurrentPositionQuantity + signedQuantity;
        var reducesOnly = Math.Abs(positionAfter) <= Math.Abs(context.CurrentPositionQuantity)
                          && Math.Sign(positionAfter) * Math.Sign(context.CurrentPositionQuantity) >= 0;
        var notional = order.Quantity * order.ReferencePrice;

        if (context.OpenOrders >= limits.MaxOpenOrders)
            violations.Add($"{context.OpenOrders} orders are already open; the limit is {limits.MaxOpenOrders}.");

        if (reducesOnly)
            return new RiskDecision(violations);

        if (!limits.TradingEnabled)
            violations.Add("Trading is switched off (kill switch). Only orders that reduce a position are allowed.");
        if (notional > limits.MaxOrderNotional)
            violations.Add($"The order is worth {notional:N2} USD; the limit per order is {limits.MaxOrderNotional:N2} USD.");

        var positionNotional = Math.Abs(positionAfter) * order.ReferencePrice;
        if (positionNotional > limits.MaxPositionNotional)
            violations.Add($"The {order.Symbol} position would be worth {positionNotional:N2} USD; the limit is {limits.MaxPositionNotional:N2} USD.");
        if (-context.DailyProfitLoss >= limits.MaxDailyLoss)
            violations.Add($"Today's loss of {-context.DailyProfitLoss:N2} USD has reached the daily limit of {limits.MaxDailyLoss:N2} USD.");
        if (order.Side == OrderSide.Buy && notional > context.BuyingPower)
            violations.Add($"The order is worth {notional:N2} USD but buying power is {context.BuyingPower:N2} USD.");

        return new RiskDecision(violations);
    }
}
