using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Proposals;
using Stockhog.Domain.Signals;
using Stockhog.Domain.Trading;

namespace Stockhog.Domain.Tests;

public class ProposalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 14, 0, 0, TimeSpan.Zero);
    private static readonly RiskLimits Generous = new(true, 1_000_000m, 1_000_000m, 10_000m, 10);

    [Fact]
    public void Size_risks_the_set_share_of_equity_down_to_the_stop()
    {
        // 1% of 100k = 1,000 risked; 5 per share down to the stop: 200 shares, exactly the 20% position cap too.
        var size = PositionSizer.Size(100_000m, 500_000m, 100m, 95m, SizingRules.Default with { MaxPositionPercent = 50 }, Generous);

        Assert.Equal((200m, 95m, 1_000m, SizeLimit.Risk, false), (size.Quantity, size.StopPrice, size.RiskAmount, size.LimitedBy, size.UsesFallbackStop));
    }

    [Theory]
    [InlineData(99, 500_000, 1_000_000, 20, 200, SizeLimit.PositionShare)]   // a 1 USD stop would buy 1,000 shares; 20% of equity caps at 200
    [InlineData(95, 500_000, 5_000, 100, 50, SizeLimit.RiskLimits)]          // 5,000 USD per order at 100
    [InlineData(95, 3_000, 1_000_000, 100, 30, SizeLimit.BuyingPower)]       // 3,000 USD to spend
    public void Size_is_capped_by_position_share_risk_limits_and_buying_power(
        decimal stop, decimal buyingPower, decimal maxNotional, decimal maxPositionPercent, int expected, SizeLimit limit)
    {
        var limits = Generous with { MaxOrderNotional = maxNotional, MaxPositionNotional = maxNotional };

        var size = PositionSizer.Size(100_000m, buyingPower, 100m, stop, SizingRules.Default with { MaxPositionPercent = maxPositionPercent }, limits);

        Assert.Equal((expected, limit), ((int)size.Quantity, size.LimitedBy));
    }

    [Theory]
    [InlineData(null)]      // the strategy has no stop
    [InlineData(105.0)]     // above the price
    [InlineData(60.0)]      // 40% away: mostly unprotected
    public void A_missing_or_unusable_stop_is_replaced_by_the_fallback(double? stop)
    {
        var size = PositionSizer.Size(100_000m, 500_000m, 100m, (decimal?)stop, SizingRules.Default, Generous);

        Assert.True(size.UsesFallbackStop);
        Assert.Equal(92m, size.StopPrice);   // 8% below
    }

    [Fact]
    public void Sizing_rules_stay_within_sane_bounds()
    {
        Assert.Throws<DomainException>(() => SizingRules.Create(0, 20, 8));
        Assert.Throws<DomainException>(() => SizingRules.Create(11, 20, 8));
        Assert.Throws<DomainException>(() => SizingRules.Create(1, 0, 8));
        Assert.Throws<DomainException>(() => SizingRules.Create(1, 20, 31));
        Assert.Equal(SizingRules.Default, SizingRules.Create(1, 20, 8));
    }

    [Fact]
    public void Only_a_fresh_entry_with_a_stop_below_the_price_can_be_proposed()
    {
        var size = new PositionSize(10, 95m, 50m, SizeLimit.Risk, false);

        Assert.Throws<DomainException>(() => Create(Signal(SignalKind.Holding), size));
        Assert.Throws<DomainException>(() => Create(Signal(SignalKind.Entry), size with { Quantity = 0 }));
        Assert.Throws<DomainException>(() => Create(Signal(SignalKind.Entry), size with { Quantity = 2.5m }));
        Assert.Throws<DomainException>(() => Create(Signal(SignalKind.Entry), size with { StopPrice = 100m }));
        Assert.Throws<DomainException>(() => Create(Signal(SignalKind.Entry), size, expiresAt: T0));
    }

    [Fact]
    public void A_pending_proposal_is_approved_only_near_its_reference_price_and_above_its_stop()
    {
        var proposal = Create(Signal(SignalKind.Entry), new PositionSize(10, 95m, 50m, SizeLimit.Risk, false));

        Assert.Null(proposal.ApprovalProblem(102.5m));                         // +2.5%
        Assert.Contains("+4.00%", proposal.ApprovalProblem(104m));             // too far up
        Assert.Contains("-3.50%", proposal.ApprovalProblem(96.5m));            // too far down
        Assert.Contains("stop", proposal.ApprovalProblem(95m));
        Assert.Equal((1_000m, 0.05m), (proposal.Notional, proposal.RiskPercentOfEquity));
    }

    [Fact]
    public void A_proposal_is_decided_once()
    {
        var submitted = Create(Signal(SignalKind.Entry), new PositionSize(10, 95m, 50m, SizeLimit.Risk, false));
        submitted.Submit(ClientOrderId.New(T0), T0.AddMinutes(5));
        Assert.Equal(ProposalStatus.Submitted, submitted.Status);
        Assert.NotNull(submitted.OrderId);
        Assert.Throws<DomainException>(() => submitted.Reject("late", T0));

        var rejected = Create(Signal(SignalKind.Entry), new PositionSize(10, 95m, 50m, SizeLimit.Risk, false));
        rejected.Reject("  not today ", T0);
        Assert.Equal((ProposalStatus.Rejected, "not today"), (rejected.Status, rejected.Reason));

        var expiring = Create(Signal(SignalKind.Entry), new PositionSize(10, 95m, 50m, SizeLimit.Risk, false));
        Assert.False(expiring.ExpireIfDue(T0.AddHours(1)));
        Assert.True(expiring.ExpireIfDue(T0.AddDays(1)));
        Assert.Equal(ProposalStatus.Expired, expiring.Status);
        Assert.Throws<DomainException>(() => Create(Signal(SignalKind.Entry), new PositionSize(10, 95m, 50m, SizeLimit.Risk, false))
            .Submit(ClientOrderId.New(T0), T0.AddDays(1)));
    }

    private static StrategySignal Signal(SignalKind kind) =>
        new(kind, new DateOnly(2026, 10, 30), 100m, 10m, 100m, new DateTime(2026, 10, 30, 16, 0, 0), "Entry", 95m, null, null, 0, "run-1");

    private static TradeProposal Create(StrategySignal signal, PositionSize size, DateTimeOffset? expiresAt = null) =>
        TradeProposal.Create(ProposalId.New(Symbol.Create("SPY"), T0), Symbol.Create("SPY"), StrategyName.Create("RsiPullbackStrategy"),
            signal, size, 100_000m, 2.5m, null, T0, expiresAt ?? T0.AddHours(6));
}
