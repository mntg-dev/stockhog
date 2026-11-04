using Stockhog.Application.Notifications;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Orders;
using Stockhog.Application.Trading.Portfolio;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Tests.Notifications;

public class NotificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fill_is_announced_with_side_quantity_symbol_and_price()
    {
        await using var host = new ApplicationTestHost();
        host.Broker.FillMarketOrdersImmediately = true;
        host.Broker.FillPrice = 101.5m;

        await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 10), Ct);

        var notice = Assert.Single(host.Notifier.Notices);
        Assert.Equal(NoticeTopic.OrderFilled, notice.Topic);
        Assert.Equal([OrderSide.Buy, 10m, "AAPL", 101.5m], notice.Args);
    }

    [Fact]
    public async Task A_rejection_by_the_broker_is_a_warning_with_its_reason()
    {
        await using var host = new ApplicationTestHost();
        host.Broker.SubmitBehavior = _ => throw new BrokerException("insufficient buying power", isRejection: true);

        await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 10), Ct);

        var notice = Assert.Single(host.Notifier.Notices);
        Assert.Equal((NoticeTopic.OrderRejected, true), (notice.Topic, notice.IsWarning));
        Assert.Equal(["AAPL", "insufficient buying power"], notice.Args);
    }

    [Fact]
    public async Task Trading_halted_by_a_risk_limit_is_announced_once()
    {
        await using var host = new ApplicationTestHost();
        host.Broker.Account = host.Broker.Account with { Equity = 98_000, LastEquity = 100_000 };

        await host.Dispatcher.SendAsync(new EnforceDailyLossLimitCommand(), Ct);
        await host.Dispatcher.SendAsync(new EnforceDailyLossLimitCommand(), Ct);

        var notice = Assert.Single(host.Notifier.Notices);
        Assert.Equal(NoticeTopic.HaltedOnDailyLoss, notice.Topic);
        Assert.Equal([2_000m, 1_000m], notice.Args);
    }

    [Fact]
    public async Task A_drawdown_halt_is_announced_with_the_drawdown_and_its_limit()
    {
        await using var host = new ApplicationTestHost();
        host.EquityPeaks.Peaks["PA123"] = new EquityPeak("PA123", 100_000m, TestData.Now.AddDays(-5));
        host.Broker.Account = host.Broker.Account with { Equity = 84_000 };

        await host.Dispatcher.SendAsync(new EnforcePortfolioLimitsCommand(), Ct);

        var notice = Assert.Single(host.Notifier.Notices, n => n.Topic == NoticeTopic.HaltedOnDrawdown);
        Assert.Equal([16m, 15m], notice.Args);
    }
}
