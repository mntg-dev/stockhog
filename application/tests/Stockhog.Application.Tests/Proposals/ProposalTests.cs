using Stockhog.Application.Proposals;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Proposals;
using Stockhog.Domain.Scanning;
using Stockhog.Domain.Signals;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Tests.Proposals;

public class ProposalTests
{
    // TestData.Now is 2026-10-15 08:00 in New York: a signal of the 14th is for today's session, which closes at 20:00 UTC.
    private static readonly DateOnly LastSession = new(2026, 10, 14);
    private static readonly RiskLimits Generous = new(true, 1_000_000m, 1_000_000m, 10_000m, 10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_signal_is_valid_until_the_close_of_the_next_session()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 20, 0, 0, TimeSpan.Zero), Common.ExchangeCalendar.NextSessionClose(LastSession));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 21, 0, 0, TimeSpan.Zero), Common.ExchangeCalendar.NextSessionClose(new DateOnly(2026, 10, 30)));  // Friday → Monday, after the clock change
    }

    [Fact]
    public async Task Creates_one_sized_proposal_per_symbol_from_fresh_entries()
    {
        await using var host = new ApplicationTestHost();
        host.RiskLimits.Update(Generous);
        var scan = GivenScan(host,
            ("SPY", "RsiPullbackStrategy", 3.0m, Signal(SignalKind.Entry, price: 200m, stop: 190m)),
            ("SPY", "BollingerReversionStrategy", 1.0m, Signal(SignalKind.Entry, price: 200m, stop: 195m)),   // same symbol, lower score
            ("AAPL", "DonchianBreakoutStrategy", 2.0m, Signal(SignalKind.Holding, price: 250m, stop: 230m)),  // not fresh
            ("MSFT", "OpeningRangeBreakoutStrategy", 1.5m, Signal(SignalKind.Entry, price: 500m, stop: 495m)), // intraday
            ("NVDA", "MomentumTrendStrategy", 1.2m, Signal(SignalKind.Entry, price: 200m, stop: null)),       // held already
            ("AMD", "RsiPullbackStrategy", 1.1m, Signal(SignalKind.Entry, price: 150m, stop: null)));
        host.Broker.Positions.Add(new BrokerPosition("NVDA", 10, 180, 200, 2_000, 200, 11));

        var created = await host.Dispatcher.SendAsync(new CreateProposalsCommand(scan.Id.Value), Ct);

        Assert.True(created.IsSuccess, created.Error?.Message);
        var proposals = host.Proposals.Proposals;
        Assert.Equal(["SPY", "AMD"], proposals.Select(p => p.Symbol.Value));

        // 1% of 100k risked over 10 USD down to the stop: 100 shares, also the 20% position cap at 200 USD.
        var spy = proposals[0];
        Assert.Equal(("RsiPullbackStrategy", 100m, 190m, 1_000m), (spy.Strategy.Value, spy.Quantity, spy.StopPrice, spy.RiskAmount));
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 20, 0, 0, TimeSpan.Zero), spy.ExpiresAt);
        Assert.Equal(scan.Id, spy.ScanId);

        // No stop from the strategy: 8% below the price.
        Assert.Equal(138m, proposals[1].StopPrice);

        Assert.Equal(
            // In score order: the pairs are considered best first.
            ["MSFT: Intraday strategy: its signals are not followed by proposals.", "NVDA: Already held in the account.", "SPY: Already proposed."],
            created.Value.Skipped.Select(s => $"{s.Symbol}: {s.Reason}"));
        Assert.Equal(2, host.AuditLog.Entries.Count(e => e.Category == AuditCategory.Proposal && e.Action == "Proposed"));
        Assert.Empty(host.Broker.Orders);
    }

    [Fact]
    public async Task Approving_places_the_buy_with_its_stop_loss_through_the_order_checks()
    {
        await using var host = new ApplicationTestHost();
        host.RiskLimits.Update(Generous);
        var proposal = await GivenProposalAsync(host);
        host.LatestPrices.Price = 203m;   // +1.5%

        var placed = await host.Dispatcher.SendAsync(new ApproveProposalCommand(proposal.Id.Value), Ct);

        Assert.True(placed.IsSuccess, placed.Error?.Message);
        var order = Assert.Single(host.Database.Orders);
        Assert.Equal((OrderSide.Buy, OrderType.Market, 100m, 190m), (order.Side, order.Type, order.Quantity, order.StopLossPrice));
        Assert.Equal((ProposalStatus.Submitted, order.Id), (proposal.Status, proposal.OrderId));
        Assert.Contains(host.AuditLog.Entries, e => e.Category == AuditCategory.Proposal && e.Action == "Approved");
    }

    [Fact]
    public async Task A_price_that_moved_too_far_leaves_the_proposal_pending_and_sends_nothing()
    {
        await using var host = new ApplicationTestHost();
        host.RiskLimits.Update(Generous);
        var proposal = await GivenProposalAsync(host);
        host.LatestPrices.Price = 210m;   // +5%

        var placed = await host.Dispatcher.SendAsync(new ApproveProposalCommand(proposal.Id.Value), Ct);

        Assert.Equal("Proposal.PriceMoved", placed.Error?.Code);
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
        Assert.Empty(host.Broker.Orders);
    }

    [Fact]
    public async Task An_order_refused_by_the_risk_limits_fails_the_proposal()
    {
        await using var host = new ApplicationTestHost();
        host.RiskLimits.Update(Generous);
        var proposal = await GivenProposalAsync(host);
        host.RiskLimits.Update(Generous with { TradingEnabled = false });
        host.LatestPrices.Price = 200m;

        var placed = await host.Dispatcher.SendAsync(new ApproveProposalCommand(proposal.Id.Value), Ct);

        Assert.Equal("Trading.RiskRejected", placed.Error?.Code);
        Assert.Equal(ProposalStatus.Failed, proposal.Status);
        Assert.Contains("kill switch", proposal.Reason);
        Assert.Empty(host.Broker.Orders);
    }

    [Fact]
    public async Task Expired_proposals_cannot_be_approved_and_are_swept()
    {
        await using var host = new ApplicationTestHost();
        host.RiskLimits.Update(Generous);
        var first = await GivenProposalAsync(host);
        host.Clock.Advance(TimeSpan.FromHours(9));   // past 20:00 UTC

        var approve = await host.Dispatcher.SendAsync(new ApproveProposalCommand(first.Id.Value), Ct);
        Assert.Equal("Proposal.Expired", approve.Error?.Code);
        Assert.Equal(ProposalStatus.Expired, first.Status);

        var swept = await host.Dispatcher.SendAsync(new ExpireProposalsCommand(), Ct);
        Assert.Equal(0, swept.Value);
    }

    [Fact]
    public async Task A_rejected_proposal_stays_rejected()
    {
        await using var host = new ApplicationTestHost();
        host.RiskLimits.Update(Generous);
        var proposal = await GivenProposalAsync(host);

        var rejected = await host.Dispatcher.SendAsync(new RejectProposalCommand(proposal.Id.Value, "Earnings tomorrow"), Ct);
        var again = await host.Dispatcher.SendAsync(new RejectProposalCommand(proposal.Id.Value), Ct);
        var listed = await host.Dispatcher.SendAsync(new GetProposalsQuery(), Ct);

        Assert.True(rejected.Value);
        Assert.Equal("Proposal.NotPending", again.Error?.Code);
        Assert.Equal((ProposalStatus.Rejected, "Earnings tomorrow"), (listed.Value[0].Status, listed.Value[0].Reason));
    }

    [Fact]
    public async Task A_stale_signal_gives_no_proposal()
    {
        await using var host = new ApplicationTestHost();
        host.RiskLimits.Update(Generous);
        var scan = GivenScan(host, ("SPY", "RsiPullbackStrategy", 3.0m, Signal(SignalKind.Entry, price: 200m, stop: 190m, asOf: LastSession.AddDays(-7))));

        var created = await host.Dispatcher.SendAsync(new CreateProposalsCommand(scan.Id.Value), Ct);

        Assert.Empty(created.Value.ProposalIds);
        Assert.Contains("session that is over", Assert.Single(created.Value.Skipped).Reason);
    }

    private static async Task<TradeProposal> GivenProposalAsync(ApplicationTestHost host)
    {
        var scan = GivenScan(host, ("SPY", "RsiPullbackStrategy", 3.0m, Signal(SignalKind.Entry, price: 200m, stop: 190m)));
        var created = await host.Dispatcher.SendAsync(new CreateProposalsCommand(scan.Id.Value), Ct);
        Assert.True(created.IsSuccess, created.Error?.Message);
        return Assert.Single(host.Proposals.Proposals);
    }

    private static ScanRun GivenScan(ApplicationTestHost host, params (string Symbol, string Strategy, decimal Score, StrategySignal Signal)[] pairs) =>
        ProposalScans.Given(host, pairs);

    private static StrategySignal Signal(SignalKind kind, decimal price, decimal? stop, DateOnly? asOf = null) =>
        ProposalScans.Signal(kind, price, stop, asOf);
}
