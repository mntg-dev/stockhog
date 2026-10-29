using Stockhog.Strategies.Metadata;
using QuantConnect;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;

namespace Stockhog.Strategies;

/// <summary>
/// Short-term pullbacks in a long-term uptrend, after Larry Connors' RSI(2) method: while the close is above its long
/// moving average, buys when a very short RSI shows a sharp dip, and sells when the price recovers above a short moving
/// average or after a maximum number of days. Daily bars, long only.
/// The defaults (RSI 4, entry 15) held up best out of sample on AAPL and SPY, 2024–2026; Connors' original is 2 / 10.
/// </summary>
[Strategy("RSI pullback (Connors)", Description = "In an uptrend (above the long SMA), buys a sharp dip in a short (2–5 day) RSI; exits above the short SMA or after N days. Long only.")]
[StrategyParameter(RsiPeriod, "RSI period (days)", StrategyParameterType.Integer, 4, Minimum = 2, Maximum = 14,
    Description = "A very short RSI reacts to a few down days.",
    SuggestedFrom = 2, SuggestedTo = 5, SuggestedStep = 1)]
[StrategyParameter(EntryLevel, "Entry RSI below", StrategyParameterType.Decimal, 15, Minimum = 1, Maximum = 50,
    Description = "Buy when the RSI closes below this level.",
    SuggestedFrom = 5, SuggestedTo = 25, SuggestedStep = 5)]
[StrategyParameter(TrendPeriod, "Trend SMA (days)", StrategyParameterType.Integer, 200, Minimum = 20, Maximum = 400,
    MustExceed = ExitPeriod, Description = "Buys only while the close is above this moving average.",
    SuggestedFrom = 100, SuggestedTo = 200, SuggestedStep = 50)]
[StrategyParameter(ExitPeriod, "Exit SMA (days)", StrategyParameterType.Integer, 5, Minimum = 2, Maximum = 50,
    Description = "Sell when the close rises above this short moving average.",
    SuggestedFrom = 3, SuggestedTo = 10, SuggestedStep = 1)]
[StrategyParameter(MaxHoldDays, "Max holding (days)", StrategyParameterType.Integer, 10, Minimum = 1, Maximum = 60,
    Description = "Sell after this many days if the exit has not triggered.",
    SuggestedFrom = 5, SuggestedTo = 20, SuggestedStep = 5)]
[StrategyParameter(AllocationPercent, "Allocation (%)", StrategyParameterType.Decimal, 90, Minimum = 1, Maximum = 100,
    Description = "Share of equity invested per position.")]
public class RsiPullbackStrategy : StrategyBase
{
    public const string RsiPeriod = "rsi-period";
    public const string EntryLevel = "entry-level";
    public const string TrendPeriod = "trend-period";
    public const string ExitPeriod = "exit-period";
    public const string MaxHoldDays = "max-hold-days";
    public const string AllocationPercent = "allocation-percent";

    private Symbol _symbol = null!;
    private RelativeStrengthIndex _rsi = null!;
    private int _rsiPeriod;
    private SimpleMovingAverage _trend = null!;
    private SimpleMovingAverage _exit = null!;
    private decimal _entryLevel;
    private int _maxHoldDays;
    private decimal _allocation;
    private int _daysHeld;

    public override void Initialize()
    {
        _symbol = SetUpFromEngineParameters(Resolution.Minute);
        _entryLevel = DecimalParameter(EntryLevel);
        _maxHoldDays = IntegerParameter(MaxHoldDays);
        _allocation = DecimalParameter(AllocationPercent) / 100m;

        var trend = IntegerParameter(TrendPeriod);
        _rsiPeriod = IntegerParameter(RsiPeriod);
        _rsi = new RelativeStrengthIndex(_rsiPeriod, MovingAverageType.Wilders);
        _trend = new SimpleMovingAverage(trend);
        _exit = new SimpleMovingAverage(IntegerParameter(ExitPeriod));

        var daily = new TradeBarConsolidator(TimeSpan.FromDays(1));
        daily.DataConsolidated += OnDailyBar;
        SubscriptionManager.AddConsolidator(_symbol, daily);

        SetWarmUpTradingDays(trend + 1);
    }

    private void OnDailyBar(object? sender, TradeBar bar)
    {
        _rsi.Update(bar.EndTime, bar.Close);
        _trend.Update(bar.EndTime, bar.Close);
        _exit.Update(bar.EndTime, bar.Close);

        if (IsWarmingUp || !_rsi.IsReady || !_trend.IsReady || !_exit.IsReady)
            return;

        var rsi = _rsi.Current.Value;
        if (!Portfolio.Invested)
        {
            if (bar.Close > _trend.Current.Value && rsi < _entryLevel)
            {
                SetHoldings(_symbol, _allocation, tag: $"RSI({_rsiPeriod}) {rsi:0.0} in uptrend");
                _daysHeld = 0;
            }
        }
        else if (bar.Close > _exit.Current.Value)
        {
            Liquidate(symbol: _symbol, tag: $"Close above {_exit.Period}-day SMA");
        }
        else if (++_daysHeld >= _maxHoldDays)
        {
            Liquidate(symbol: _symbol, tag: $"Held {_daysHeld} days");
        }
    }

    public override void OnEndOfAlgorithm()
    {
        Liquidate();
    }
}
