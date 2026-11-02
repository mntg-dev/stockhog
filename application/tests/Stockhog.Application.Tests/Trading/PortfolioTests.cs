using Stockhog.Application.Proposals;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Orders;
using Stockhog.Application.Trading.Portfolio;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Tests.Trading;

public class PortfolioTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_equity_high_moves_the_peak_and_a_lower_equity_does_not()
    {
        await using var host = new ApplicationTestHost();
        host.Broker.Account = host.Broker.Account with { Equity = 110_000 };
        var high = await host.Dispatcher.SendAsync(new GetPortfolioRiskQuery(), Ct);

        host.Broker.Account = host.Broker.Account with { Equity = 99_000 };
        host.Clock.Advance(TimeSpan.FromHours(1));
        var lower = await host.Dispatcher.SendAsync(new GetPortfolioRiskQuery(), Ct);

        Assert.Equal((110_000m, 0m), (high.Value.EquityPeak, high.Value.DrawdownPercent));
        Assert.Equal((110_000m, 10m, TestData.Now), (lower.Value.EquityPeak, lower.Value.DrawdownPercent, lower.Value.PeakAt));
        Assert.Equal(110_000m, host.EquityPeaks.Peaks["PA123"].Equity);
    }

    [Fact]
    public async Task An_order_is_refused_once_equity_is_too_far_below_its_peak()
    {
        await using var host = new ApplicationTestHost();
        host.EquityPeaks.Peaks["PA123"] = new EquityPeak("PA123", 120_000m, TestData.Now.AddDays(-20));   // equity 100k: 16.7% down

        var placed = await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 10), Ct);

        Assert.Equal("Trading.RiskRejected", placed.Error?.Code);
        Assert.Contains("below its peak", placed.Error!.Message);
        Assert.Empty(host.Broker.Orders);
    }

    [Fact]
    public async Task Orders_of_the_day_count_towards_the_daily_order_limit()
    {
        await using var host = new ApplicationTestHost();
        host.RiskLimits.Update(RiskLimits.Default with { MaxOrdersPerDay = 2 });

        var first = await host.Dispatcher.SendAsync(new PlaceOrderCommand("AAPL", OrderSide.Buy, OrderType.Market, 1), Ct);
        var second = await host.Dispatcher.SendAsync(new PlaceOrderCommand("MSFT", OrderSide.Buy, OrderType.Market, 1), Ct);
        var third = await host.Dispatcher.SendAsync(new PlaceOrderCommand("SPY", OrderSide.Buy, OrderType.Market, 1), Ct);

        Assert.True(first.IsSuccess && second.IsSuccess, first.Error?.Message ?? second.Error?.Message);
        Assert.Contains("2 orders were placed today", third.Error?.Message);
    }

    [Fact]
    public async Task Hitting_the_drawdown_limit_switches_trading_off_and_can_close_every_position()
    {
        await using var host = new ApplicationTestHost();
        host.RiskLimits.Update(RiskLimits.Default with { FlattenOnDrawdown = true });
        host.Broker.Positions.Add(new BrokerPosition("AAPL", 10, 200, 180, 1_800, -200, -10));
        host.EquityPeaks.Peaks["PA123"] = new EquityPeak("PA123", 100_000m, TestData.Now.AddDays(-5));

        host.Broker.Account = host.Broker.Account with { Equity = 90_000 };
        var within = await host.Dispatcher.SendAsync(new EnforcePortfolioLimitsCommand(), Ct);
        host.Broker.Account = host.Broker.Account with { Equity = 84_000 };
        var halted = await host.Dispatcher.SendAsync(new EnforcePortfolioLimitsCommand(), Ct);
        var again = await host.Dispatcher.SendAsync(new EnforcePortfolioLimitsCommand(), Ct);

        Assert.Equal((false, true, false), (within.Value, halted.Value, again.Value));
        Assert.False(host.RiskLimits.Current.TradingEnabled);
        Assert.Contains(host.AuditLog.Entries, e => e.Action == "Halted" && (e.Details.Contains("16.00%") || e.Details.Contains("16,00%")));

        // Closing is still allowed with trading off: a sell of the whole AAPL position went out.
        var close = Assert.Single(host.Database.Orders);
        Assert.Equal((Symbol.Create("AAPL"), OrderSide.Sell, 10m), (close.Symbol, close.Side, close.Quantity));
    }

    [Fact]
    public async Task Proposals_stop_at_the_position_limit_counting_the_ones_still_pending()
    {
        await using var host = new ApplicationTestHost();
        host.RiskLimits.Update(new RiskLimits(true, 1_000_000m, 1_000_000m, 10_000m, 10, MaxOpenPositions: 2));
        host.Broker.Positions.Add(new BrokerPosition("NVDA", 10, 180, 200, 2_000, 200, 11));
        var scan = ProposalScans.Given(host,
            ("SPY", "RsiPullbackStrategy", 3.0m, ProposalScans.Entry(200m, 190m)),
            ("AMD", "RsiPullbackStrategy", 2.0m, ProposalScans.Entry(150m, 140m)));

        var created = await host.Dispatcher.SendAsync(new CreateProposalsCommand(scan.Id.Value), Ct);

        Assert.True(created.IsSuccess, created.Error?.Message);
        Assert.Equal(["SPY"], host.Proposals.Proposals.Select(p => p.Symbol.Value));   // NVDA held + SPY proposed = 2
        Assert.Contains("2 symbols are already held", Assert.Single(created.Value.Skipped).Reason);
    }
}
