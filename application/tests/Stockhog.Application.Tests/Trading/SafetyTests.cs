using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Orders;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Tests.Trading;

public class LiveTradingSafetyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Live_orders_are_refused_until_armed_with_the_account_number()
    {
        await using var host = new ApplicationTestHost();
        host.Environment.Mode = TradingMode.Live;
        var order = new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 1);

        var unarmed = await host.Dispatcher.SendAsync(order, Ct);
        var wrong = await host.Dispatcher.SendAsync(new ArmLiveTradingCommand("PA999"), Ct);
        var armed = await host.Dispatcher.SendAsync(new ArmLiveTradingCommand(" PA123 "), Ct);
        var placed = await host.Dispatcher.SendAsync(order, Ct);
        var disarmed = await host.Dispatcher.SendAsync(new DisarmLiveTradingCommand(), Ct);
        var afterDisarm = await host.Dispatcher.SendAsync(order, Ct);

        Assert.Equal("Trading.LiveNotArmed", unarmed.Error?.Code);
        Assert.Equal(ErrorType.Validation, wrong.Error?.Type);
        Assert.True(armed.Value.IsArmed);
        Assert.True(placed.IsSuccess, placed.Error?.Message);
        Assert.False(disarmed.Value.CanTrade);
        Assert.Equal("Trading.LiveNotArmed", afterDisarm.Error?.Code);
        Assert.Single(host.Database.Orders);

        Assert.Equal(
            ["Refused", "ArmRefused", "Armed", "Created", "Submitted", "Disarmed", "Refused"],
            host.AuditLog.Entries.Select(e => e.Action));
        Assert.All(host.AuditLog.Entries, e => Assert.Equal(TradingMode.Live, e.Mode));
    }

    [Fact]
    public async Task Paper_trading_needs_no_arming_and_cannot_be_armed()
    {
        await using var host = new ApplicationTestHost();

        var session = await host.Dispatcher.SendAsync(new GetTradingSessionQuery(), Ct);
        var arm = await host.Dispatcher.SendAsync(new ArmLiveTradingCommand("PA123"), Ct);

        Assert.True(session.Value.CanTrade);
        Assert.Equal("Trading.NotLive", arm.Error?.Code);
    }

    [Fact]
    public async Task Reaching_the_daily_loss_limit_switches_trading_off_once()
    {
        await using var host = new ApplicationTestHost();
        // Default limit: 1,000 USD.
        host.Broker.Account = host.Broker.Account with { Equity = 99_500, LastEquity = 100_000 };
        var withinLimit = await host.Dispatcher.SendAsync(new EnforceDailyLossLimitCommand(), Ct);

        host.Broker.Account = host.Broker.Account with { Equity = 98_000 };
        var halted = await host.Dispatcher.SendAsync(new EnforceDailyLossLimitCommand(), Ct);
        var again = await host.Dispatcher.SendAsync(new EnforceDailyLossLimitCommand(), Ct);

        Assert.False(withinLimit.Value);
        Assert.True(halted.Value);
        Assert.False(again.Value);
        Assert.False(host.RiskLimits.Current.TradingEnabled);
        Assert.Equal("Halted", Assert.Single(host.AuditLog.Entries).Action);
    }

    [Fact]
    public async Task Risk_refusals_kill_switch_changes_and_order_lifecycle_are_audited()
    {
        await using var host = new ApplicationTestHost();
        host.LatestPrices.Price = 1_000;

        await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 100), Ct);
        var limits = (await host.Dispatcher.SendAsync(new GetRiskLimitsQuery(), Ct)).Value;
        await host.Dispatcher.SendAsync(new UpdateRiskLimitsCommand(limits with { TradingEnabled = false }), Ct);
        host.Broker.Positions.Add(new BrokerPosition("AAPL", 2, 900, 1_000, 2_000, 200, 11));
        var close = await host.Dispatcher.SendAsync(new ClosePositionCommand("AAPL"), Ct);
        await host.Dispatcher.SendAsync(new CancelOrderCommand(close.Value.ClientOrderId), Ct);

        var log = (await host.Dispatcher.SendAsync(new GetAuditLogQuery(), Ct)).Value;

        Assert.Equal(["CancelRequested", "Submitted", "Created", "TradingDisabled", "Refused"], log.Select(e => e.Action));
        Assert.Contains("limit per order", log[^1].Details);
        Assert.Equal(close.Value.ClientOrderId, log[0].ClientOrderId);
    }
}
