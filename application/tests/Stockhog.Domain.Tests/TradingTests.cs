using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;

namespace Stockhog.Domain.Tests;

public class OrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Client_order_ids_are_unique_sortable_and_safe()
    {
        var first = ClientOrderId.New(T0);
        var second = ClientOrderId.New(T0);

        Assert.NotEqual(first, second);
        Assert.StartsWith("emt-20261019-143000000-", first.Value);
        Assert.True(first.Value.Length <= ClientOrderId.MaxLength);
        Assert.Throws<DomainException>(() => ClientOrderId.From("bad id!"));
    }

    [Fact]
    public void Prices_must_match_the_order_type()
    {
        Assert.Throws<DomainException>(() => Create(OrderType.Limit, limit: null));
        Assert.Throws<DomainException>(() => Create(OrderType.Market, limit: 100));
        Assert.Throws<DomainException>(() => Create(OrderType.Stop, stop: null));
        Assert.Throws<DomainException>(() => Create(OrderType.StopLimit, limit: 100, stop: null));
        Assert.Throws<DomainException>(() => Create(OrderType.Market, quantity: 0));
        Assert.Throws<DomainException>(() => Create(OrderType.Limit, limit: 100, quantity: 1.5m));
        Assert.Equal(OrderStatus.New, Create(OrderType.StopLimit, limit: 101, stop: 100).Status);
        Assert.Equal(0.5m, Create(OrderType.Market, quantity: 0.5m).Quantity);
    }

    [Fact]
    public void Fills_accumulate_and_complete_the_order()
    {
        var order = Create(OrderType.Market, quantity: 10);
        order.MarkSubmitted("b-1", T0);

        Assert.True(order.ApplyFill(4, 100, T0.AddSeconds(1)));
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
        Assert.True(order.ApplyFill(10, 100.5m, T0.AddSeconds(2)));

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(100.5m, order.AverageFillPrice);
        Assert.Equal(
            [typeof(OrderCreated), typeof(OrderSubmitted), typeof(OrderPartiallyFilled), typeof(OrderFilled)],
            order.DomainEvents.Select(e => e.GetType()));
    }

    [Fact]
    public void Repeated_stale_and_late_reports_change_nothing()
    {
        var order = Create(OrderType.Market, quantity: 10);
        order.MarkSubmitted("b-1", T0);
        order.ApplyFill(6, 100, T0);

        Assert.False(order.ApplyFill(6, 100, T0));
        Assert.False(order.ApplyFill(3, 99, T0));

        Assert.True(order.Cancel(T0));
        Assert.False(order.Cancel(T0));
        Assert.False(order.ApplyFill(10, 100, T0));
        Assert.False(order.Reject("late", T0));
        order.MarkSubmitted("b-1", T0);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(6, order.FilledQuantity);
        Assert.Throws<DomainException>(() => Create(OrderType.Market, quantity: 1).ApplyFill(2, 100, T0));
    }

    [Fact]
    public void A_fill_reported_before_the_submit_answer_still_records_the_broker_id()
    {
        var order = Create(OrderType.Market, quantity: 5);

        order.ApplyFill(5, 100, T0);
        order.MarkSubmitted("b-9", T0);

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal("b-9", order.BrokerOrderId);
    }

    private static Order Create(OrderType type, decimal? limit = null, decimal? stop = null, decimal quantity = 10) =>
        Order.Create(ClientOrderId.New(T0), Symbol.Create("AAPL"), OrderSide.Buy, type, quantity, limit, stop, TimeInForce.Day, T0);
}

public class RiskPolicyTests
{
    private static readonly Symbol Aapl = Symbol.Create("AAPL");
    private static readonly RiskLimits Limits = new(true, MaxOrderNotional: 10_000, MaxPositionNotional: 20_000, MaxDailyLoss: 1_000, MaxOpenOrders: 3);
    private static readonly RiskContext Flat = new(CurrentPositionQuantity: 0, DailyProfitLoss: 0, OpenOrders: 0, BuyingPower: 100_000);

    [Fact]
    public void An_order_within_every_limit_is_allowed()
    {
        Assert.True(RiskPolicy.Evaluate(new OrderIntent(Aapl, OrderSide.Buy, 50, 100), Flat, Limits).IsAllowed);
    }

    [Theory]
    [InlineData(150, 100, "limit per order")]
    [InlineData(90, 100, "position would be worth")]
    public void Order_and_position_size_are_limited(decimal quantity, decimal price, string expected)
    {
        var context = Flat with { CurrentPositionQuantity = 120 };

        var decision = RiskPolicy.Evaluate(new OrderIntent(Aapl, OrderSide.Buy, quantity, price), context, Limits);

        Assert.Contains(decision.Violations, v => v.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Kill_switch_daily_loss_and_buying_power_block_new_exposure()
    {
        var context = Flat with { DailyProfitLoss = -1_000, BuyingPower = 1_000 };

        var decision = RiskPolicy.Evaluate(new OrderIntent(Aapl, OrderSide.Buy, 50, 100), context, Limits with { TradingEnabled = false });

        Assert.Equal(3, decision.Violations.Count);
        Assert.Contains(decision.Violations, v => v.Contains("kill switch", StringComparison.Ordinal));
        Assert.Contains(decision.Violations, v => v.Contains("daily limit", StringComparison.Ordinal));
        Assert.Contains(decision.Violations, v => v.Contains("buying power", StringComparison.Ordinal));
    }

    [Fact]
    public void Reducing_a_position_is_allowed_even_when_trading_is_off()
    {
        var longPosition = Flat with { CurrentPositionQuantity = 500, DailyProfitLoss = -5_000 };
        var shortPosition = Flat with { CurrentPositionQuantity = -200 };
        var off = Limits with { TradingEnabled = false };

        Assert.True(RiskPolicy.Evaluate(new OrderIntent(Aapl, OrderSide.Sell, 500, 100), longPosition, off).IsAllowed);
        Assert.True(RiskPolicy.Evaluate(new OrderIntent(Aapl, OrderSide.Buy, 200, 100), shortPosition, off).IsAllowed);

        // Selling more than held flips the position to short: that is new exposure.
        Assert.False(RiskPolicy.Evaluate(new OrderIntent(Aapl, OrderSide.Sell, 600, 100), longPosition, off).IsAllowed);
    }

    [Fact]
    public void The_open_order_limit_applies_to_every_order()
    {
        var busy = Flat with { CurrentPositionQuantity = 10, OpenOrders = 3 };

        Assert.False(RiskPolicy.Evaluate(new OrderIntent(Aapl, OrderSide.Sell, 10, 100), busy, Limits).IsAllowed);
    }
}
