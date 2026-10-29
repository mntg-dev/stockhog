using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Orders;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Tests.Trading;

public class PlaceOrderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Order_is_saved_before_the_submit_and_follows_the_broker_answer()
    {
        await using var host = new ApplicationTestHost();
        var statusAtSubmit = (OrderStatus?)null;
        host.Broker.SubmitBehavior = order =>
        {
            statusAtSubmit = host.Database.Orders.Single().Status;
            return null;
        };

        var placed = await host.Dispatcher.SendAsync(new PlaceOrderCommand("aapl", OrderSide.Buy, OrderType.Market, 10), Ct);

        Assert.True(placed.IsSuccess, placed.Error?.Message);
        Assert.Equal(OrderStatus.New, statusAtSubmit);
        Assert.Equal(OrderStatus.Submitted, placed.Value.Status);
        var order = Assert.Single(host.Database.Orders);
        Assert.Equal("AAPL", order.Symbol.Value);
        Assert.Equal(placed.Value.BrokerOrderId, order.BrokerOrderId);
        Assert.Contains(order.Id.Value, host.Broker.Orders.Keys);
    }

    [Fact]
    public async Task An_immediate_fill_is_recorded()
    {
        await using var host = new ApplicationTestHost();
        host.Broker.FillMarketOrdersImmediately = true;
        host.Broker.FillPrice = 101.5m;

        var placed = await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 10), Ct);

        Assert.Equal(OrderStatus.Filled, placed.Value.Status);
        Assert.Equal(101.5m, placed.Value.AverageFillPrice);
    }

    [Fact]
    public async Task Risk_violations_stop_the_order_before_anything_is_saved_or_sent()
    {
        await using var host = new ApplicationTestHost();
        host.LatestPrices.Price = 500;

        // 100 x 500 = 50,000 USD, above the default 10,000 per order.
        var result = await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 100), Ct);

        Assert.Equal("Trading.RiskRejected", result.Error?.Code);
        Assert.Contains(result.Error!.Details, d => d.Contains("limit per order", StringComparison.Ordinal));
        Assert.Empty(host.Database.Orders);
        Assert.Empty(host.Broker.Orders);
    }

    [Fact]
    public async Task The_live_quote_is_preferred_over_the_latest_trade_and_a_limit_over_both()
    {
        await using var host = new ApplicationTestHost();
        host.Quotes.Quotes["AAPL"] = new Quote("AAPL", 199, 201, null, TestData.Now);
        host.LatestPrices.Price = 1;

        // 60 x 200 = 12,000 USD: refused at the quote price, although 60 x 1 would pass.
        var market = await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 60), Ct);
        var limit = await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Limit, 60, LimitPrice: 100), Ct);

        Assert.Equal("Trading.RiskRejected", market.Error?.Code);
        Assert.True(limit.IsSuccess, limit.Error?.Message);
    }

    [Fact]
    public async Task A_broker_rejection_is_stored_and_reported()
    {
        await using var host = new ApplicationTestHost();
        host.Broker.SubmitBehavior = _ => throw new BrokerException("insufficient buying power", isRejection: true);

        var result = await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 10), Ct);

        Assert.Equal("Trading.BrokerRejected", result.Error?.Code);
        var order = Assert.Single(host.Database.Orders);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal("insufficient buying power", order.RejectReason);
    }

    [Fact]
    public async Task An_unknown_submit_outcome_leaves_the_order_new_for_reconciliation()
    {
        await using var host = new ApplicationTestHost();
        host.Broker.SubmitBehavior = _ => throw new BrokerException("timeout", isRejection: false);

        var result = await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 10), Ct);

        Assert.Equal("Trading.SubmitUnknown", result.Error?.Code);
        Assert.Equal(OrderStatus.New, Assert.Single(host.Database.Orders).Status);
    }

    [Theory]
    [InlineData(OrderType.Limit, null, null, "limit price")]
    [InlineData(OrderType.Market, 100.0, null, "no limit price")]
    [InlineData(OrderType.Stop, null, null, "stop price")]
    public async Task Prices_are_validated_per_order_type(OrderType type, double? limit, double? stop, string expected)
    {
        await using var host = new ApplicationTestHost();

        var result = await host.Dispatcher.SendAsync(
            new PlaceOrderCommand("AAPL", OrderSide.Buy, type, 1, (decimal?)limit, (decimal?)stop), Ct);

        Assert.Equal(ErrorType.Validation, result.Error?.Type);
        Assert.Contains(expected, result.Error!.Message);
    }

    [Fact]
    public async Task Fractional_quantities_only_for_market_day_orders()
    {
        await using var host = new ApplicationTestHost();

        var gtc = await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 0.5m, TimeInForce: TimeInForce.Gtc), Ct);
        var day = await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 0.5m), Ct);

        Assert.Equal(ErrorType.Validation, gtc.Error?.Type);
        Assert.True(day.IsSuccess, day.Error?.Message);
    }
}

public class OrderLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Trade_updates_drive_the_order_and_duplicates_change_nothing()
    {
        await using var host = new ApplicationTestHost();
        var placed = (await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 10), Ct)).Value;
        var broker = host.Broker.Orders[placed.ClientOrderId];

        var partial = new TradeUpdate("partial_fill", broker with { Status = OrderStatus.PartiallyFilled, FilledQuantity = 4, AverageFillPrice = 100 }, TestData.Now);
        var fill = new TradeUpdate("fill", broker with { Status = OrderStatus.Filled, FilledQuantity = 10, AverageFillPrice = 100.2m }, TestData.Now);

        Assert.True((await host.Dispatcher.SendAsync(new ApplyTradeUpdateCommand(partial), Ct)).Value);
        Assert.True((await host.Dispatcher.SendAsync(new ApplyTradeUpdateCommand(fill), Ct)).Value);
        Assert.False((await host.Dispatcher.SendAsync(new ApplyTradeUpdateCommand(fill), Ct)).Value);
        Assert.False((await host.Dispatcher.SendAsync(new ApplyTradeUpdateCommand(partial), Ct)).Value);

        var order = Assert.Single(host.Database.Orders);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(100.2m, order.AverageFillPrice);
    }

    [Fact]
    public async Task Updates_for_orders_placed_elsewhere_are_ignored()
    {
        await using var host = new ApplicationTestHost();
        var foreign = new BrokerOrder("b-x", "web-order-1", "AAPL", OrderStatus.Filled, 1, 1, 100, TestData.Now);

        var result = await host.Dispatcher.SendAsync(new ApplyTradeUpdateCommand(new TradeUpdate("fill", foreign, TestData.Now)), Ct);

        Assert.False(result.Value);
    }

    [Fact]
    public async Task Cancelling_asks_the_broker_and_a_filled_order_cannot_be_cancelled()
    {
        await using var host = new ApplicationTestHost();
        var open = (await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Limit, 10, LimitPrice: 90), Ct)).Value;

        var cancel = await host.Dispatcher.SendAsync(new CancelOrderCommand(open.ClientOrderId), Ct);

        Assert.True(cancel.IsSuccess, cancel.Error?.Message);
        Assert.Equal([open.BrokerOrderId], host.Broker.Cancelled);

        host.Broker.FillMarketOrdersImmediately = true;
        var filled = (await host.Dispatcher.SendAsync(new PlaceOrderCommand("MSFT", OrderSide.Buy, OrderType.Market, 1), Ct)).Value;
        var tooLate = await host.Dispatcher.SendAsync(new CancelOrderCommand(filled.ClientOrderId), Ct);

        Assert.Equal("Trading.NotCancellable", tooLate.Error?.Code);
    }

    [Fact]
    public async Task Reconciliation_catches_up_and_gives_up_on_orders_the_broker_never_saw()
    {
        await using var host = new ApplicationTestHost();
        var placed = (await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Limit, 10, LimitPrice: 90), Ct)).Value;
        host.Broker.Orders[placed.ClientOrderId] = host.Broker.Orders[placed.ClientOrderId] with
        {
            Status = OrderStatus.Filled, FilledQuantity = 10, AverageFillPrice = 89.9m
        };

        host.Broker.SubmitBehavior = _ => throw new BrokerException("timeout", isRejection: false);
        await host.Dispatcher.SendAsync(new PlaceOrderCommand("MSFT", OrderSide.Buy, OrderType.Limit, 1, LimitPrice: 90), Ct);
        host.Broker.Orders.Clear();
        host.Broker.Orders[placed.ClientOrderId] = new BrokerOrder("b-1", placed.ClientOrderId, "AAPL", OrderStatus.Filled, 10, 10, 89.9m, TestData.Now);

        var early = await host.Dispatcher.SendAsync(new ReconcileOrdersCommand(), Ct);
        host.Clock.Advance(ReconcileOrdersCommand.LostAfter + TimeSpan.FromSeconds(1));
        var later = await host.Dispatcher.SendAsync(new ReconcileOrdersCommand(), Ct);

        Assert.Equal(new ReconciliationResult(2, 1), early.Value);
        Assert.Equal(new ReconciliationResult(1, 1), later.Value);
        Assert.Equal(OrderStatus.Filled, host.Database.Orders.Single(o => o.Symbol.Value == "AAPL").Status);
        Assert.Equal(OrderStatus.Rejected, host.Database.Orders.Single(o => o.Symbol.Value == "MSFT").Status);
    }

    [Fact]
    public async Task Closing_a_position_cancels_its_orders_and_sells_the_whole_position()
    {
        await using var host = new ApplicationTestHost();
        var resting = (await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Sell, OrderType.Limit, 5, LimitPrice: 150), Ct)).Value;
        host.Broker.Positions.Add(new BrokerPosition("AAPL", 12.5m, 100, 110, 1375, 125, 10));
        host.RiskLimits.Update(host.RiskLimits.Current with { TradingEnabled = false });

        var closed = await host.Dispatcher.SendAsync(new ClosePositionCommand("AAPL"), Ct);

        Assert.True(closed.IsSuccess, closed.Error?.Message);
        Assert.Equal([resting.BrokerOrderId], host.Broker.Cancelled);
        var closing = host.Database.Orders.Single(o => o.Id.Value == closed.Value.ClientOrderId);
        Assert.Equal((OrderSide.Sell, OrderType.Market, 12.5m), (closing.Side, closing.Type, closing.Quantity));
    }

    [Fact]
    public async Task Close_all_closes_every_position_and_reports_failures()
    {
        await using var host = new ApplicationTestHost();
        host.Broker.Positions.Add(new BrokerPosition("AAPL", 10, 100, 100, 1000, 0, 0));
        host.Broker.Positions.Add(new BrokerPosition("MSFT", -3, 300, 300, -900, 0, 0));
        host.Broker.Positions.Add(new BrokerPosition("TSLA", 1, 200, 200, 200, 0, 0));
        host.Broker.SubmitBehavior = order => order.Symbol.Value == "TSLA" ? throw new BrokerException("halted", isRejection: true) : null;

        var result = await host.Dispatcher.SendAsync(new CloseAllPositionsCommand(), Ct);

        Assert.Equal(2, result.Value.Orders.Count);
        Assert.Contains("TSLA", Assert.Single(result.Value.Failures));
        Assert.Equal(OrderSide.Buy, host.Database.Orders.Single(o => o.Symbol.Value == "MSFT").Side);
    }

    [Fact]
    public async Task The_account_is_cached_briefly_and_dropped_when_an_order_fills()
    {
        await using var host = new ApplicationTestHost();
        host.Broker.FillMarketOrdersImmediately = true;

        var before = (await host.Dispatcher.SendAsync(new GetAccountQuery(), Ct)).Value;
        host.Broker.Account = host.Broker.Account with { Cash = 1 };
        var cached = (await host.Dispatcher.SendAsync(new GetAccountQuery(), Ct)).Value;
        await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 1), Ct);
        var after = (await host.Dispatcher.SendAsync(new GetAccountQuery(), Ct)).Value;

        Assert.Same(before, cached);
        Assert.Equal(1, after.Cash);
    }
}
