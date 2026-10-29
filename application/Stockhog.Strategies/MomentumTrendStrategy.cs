using Stockhog.Strategies.Metadata;
using QuantConnect;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;

namespace Stockhog.Strategies;

/// <summary>
/// Time-series momentum on daily bars: holds the stock while its return over the lookback is positive and the price
/// is above its long moving average, otherwise stays in cash. The decision is re-checked only every few days, which
/// keeps trading (and costs) low. Long only.
/// </summary>
[Strategy("Time-series momentum", Description = "Holds while the N-day return is positive and the price is above its long SMA; re-checked every K days. Long only.")]
[StrategyParameter(LookbackDays, "Momentum lookback (days)", StrategyParameterType.Integer, 126, Minimum = 10, Maximum = 400,
    Description = "Return measured over this many trading days (126 ≈ 6 months).",
    SuggestedFrom = 63, SuggestedTo = 252, SuggestedStep = 21)]
[StrategyParameter(TrendPeriod, "Trend SMA (days)", StrategyParameterType.Integer, 200, Minimum = 20, Maximum = 400,
    Description = "Holds only while the close is above this moving average.",
    SuggestedFrom = 100, SuggestedTo = 250, SuggestedStep = 50)]
[StrategyParameter(RebalanceDays, "Re-check every (days)", StrategyParameterType.Integer, 21, Minimum = 1, Maximum = 63,
    Description = "Trading days between decisions (21 ≈ monthly).",
    SuggestedFrom = 5, SuggestedTo = 21, SuggestedStep = 8)]
[StrategyParameter(AllocationPercent, "Allocation (%)", StrategyParameterType.Decimal, 95, Minimum = 1, Maximum = 100,
    Description = "Share of equity invested while holding.")]
public class MomentumTrendStrategy : StrategyBase
{
    public const string LookbackDays = "lookback-days";
    public const string TrendPeriod = "trend-period";
    public const string RebalanceDays = "rebalance-days";
    public const string AllocationPercent = "allocation-percent";

    private Symbol _symbol = null!;
    private RateOfChange _momentum = null!;
    private SimpleMovingAverage _trend = null!;
    private int _rebalanceDays;
    private decimal _allocation;
    private int _daysSinceDecision = int.MaxValue;

    public override void Initialize()
    {
        _symbol = SetUpFromEngineParameters(Resolution.Minute);
        _rebalanceDays = IntegerParameter(RebalanceDays);
        _allocation = DecimalParameter(AllocationPercent) / 100m;

        var lookback = IntegerParameter(LookbackDays);
        var trend = IntegerParameter(TrendPeriod);
        _momentum = new RateOfChange(lookback);
        _trend = new SimpleMovingAverage(trend);

        var daily = new TradeBarConsolidator(TimeSpan.FromDays(1));
        daily.DataConsolidated += OnDailyBar;
        SubscriptionManager.AddConsolidator(_symbol, daily);

        SetWarmUpTradingDays(Math.Max(lookback, trend) + 1);
    }

    private void OnDailyBar(object? sender, TradeBar bar)
    {
        _momentum.Update(bar.EndTime, bar.Close);
        _trend.Update(bar.EndTime, bar.Close);

        if (IsWarmingUp || !_momentum.IsReady || !_trend.IsReady)
            return;

        if (_daysSinceDecision != int.MaxValue && ++_daysSinceDecision < _rebalanceDays)
            return;
        _daysSinceDecision = 0;

        var momentum = _momentum.Current.Value;
        var aboveTrend = bar.Close > _trend.Current.Value;

        if (momentum > 0 && aboveTrend)
        {
            if (!Portfolio.Invested)
                SetHoldings(_symbol, _allocation, tag: $"Momentum {momentum:P1}, above trend");
        }
        else if (Portfolio.Invested)
        {
            Liquidate(symbol: _symbol, tag: momentum <= 0 ? $"Momentum {momentum:P1}" : "Below trend SMA");
        }
    }

    public override void OnEndOfAlgorithm()
    {
        Liquidate();
    }
}
