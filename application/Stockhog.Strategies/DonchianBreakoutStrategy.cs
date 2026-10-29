using Stockhog.Strategies.Metadata;
using QuantConnect;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;

namespace Stockhog.Strategies;

/// <summary>
/// Trend following on daily bars in the style of the Turtle traders: buys when the close breaks above the highest
/// high of the previous N days, exits when it falls below the lowest low of the previous M days or hits an ATR stop.
/// Positions are sized so that hitting the initial stop costs a fixed share of equity. Long only.
/// </summary>
[Strategy("Donchian breakout (Turtle)", Description = "Daily breakout above the N-day high; exit below the M-day low or an ATR stop. Risk-based sizing. Long only.")]
[StrategyParameter(EntryPeriod, "Entry channel (days)", StrategyParameterType.Integer, 20, Minimum = 5, Maximum = 250,
    Description = "Buy when the close is above the highest high of this many previous days.",
    SuggestedFrom = 10, SuggestedTo = 55, SuggestedStep = 5)]
[StrategyParameter(ExitPeriod, "Exit channel (days)", StrategyParameterType.Integer, 10, Minimum = 2, Maximum = 120,
    Description = "Sell when the close is below the lowest low of this many previous days.",
    SuggestedFrom = 5, SuggestedTo = 25, SuggestedStep = 5)]
[StrategyParameter(AtrStopMultiplier, "ATR stop multiplier", StrategyParameterType.Decimal, 2, Minimum = 0.5, Maximum = 10,
    Description = "Initial stop below the entry, as a multiple of the 20-day ATR.",
    SuggestedFrom = 1, SuggestedTo = 4, SuggestedStep = 0.5)]
[StrategyParameter(RiskPercent, "Risk per trade (%)", StrategyParameterType.Decimal, 1, Minimum = 0.1, Maximum = 10,
    Description = "Equity lost if the initial stop is hit; sets the position size.",
    SuggestedFrom = 0.5, SuggestedTo = 2, SuggestedStep = 0.5)]
public class DonchianBreakoutStrategy : StrategyBase
{
    public const string EntryPeriod = "entry-period";
    public const string ExitPeriod = "exit-period";
    public const string AtrStopMultiplier = "atr-stop-multiplier";
    public const string RiskPercent = "risk-percent";

    private const int AtrPeriod = 20;

    private Symbol _symbol = null!;
    private Maximum _entryHigh = null!;
    private Minimum _exitLow = null!;
    private AverageTrueRange _atr = null!;
    private decimal _atrStopMultiplier;
    private decimal _riskPerTrade;
    private decimal _stopPrice;

    public override void Initialize()
    {
        _symbol = SetUpFromEngineParameters(Resolution.Minute);
        _atrStopMultiplier = DecimalParameter(AtrStopMultiplier);
        _riskPerTrade = DecimalParameter(RiskPercent) / 100m;

        var entryPeriod = IntegerParameter(EntryPeriod);
        _entryHigh = new Maximum(entryPeriod);
        _exitLow = new Minimum(IntegerParameter(ExitPeriod));
        _atr = new AverageTrueRange(AtrPeriod);

        var daily = new TradeBarConsolidator(TimeSpan.FromDays(1));
        daily.DataConsolidated += OnDailyBar;
        SubscriptionManager.AddConsolidator(_symbol, daily);

        SetWarmUpTradingDays(Math.Max(entryPeriod, AtrPeriod) + 1);
    }

    /// <summary>Signals compare the close with the channels of the previous days, then today's bar joins them.</summary>
    private void OnDailyBar(object? sender, TradeBar bar)
    {
        var ready = _entryHigh.IsReady && _exitLow.IsReady && _atr.IsReady;
        var previousHigh = _entryHigh.Current.Value;
        var previousLow = _exitLow.Current.Value;
        var atr = _atr.Current.Value;

        _entryHigh.Update(bar.EndTime, bar.High);
        _exitLow.Update(bar.EndTime, bar.Low);
        _atr.Update(bar);

        if (IsWarmingUp || !ready)
            return;

        if (!Portfolio.Invested)
        {
            if (bar.Close > previousHigh)
                Enter(bar.Close, atr, previousHigh);
        }
        else if (bar.Close < previousLow)
        {
            Liquidate(symbol: _symbol, tag: $"Close below {_exitLow.Period}-day low {previousLow:0.00}");
        }
        else if (bar.Close <= _stopPrice)
        {
            Liquidate(symbol: _symbol, tag: "ATR stop");
        }
    }

    private void Enter(decimal price, decimal atr, decimal breakoutLevel)
    {
        var stopDistance = atr * _atrStopMultiplier;
        if (stopDistance <= 0)
            return;

        // Size by risk, but never more than the cash allows.
        var byRisk = Portfolio.TotalPortfolioValue * _riskPerTrade / stopDistance;
        var byCash = Portfolio.Cash * 0.98m / price;
        var shares = (int)Math.Floor(Math.Min(byRisk, byCash));
        if (shares <= 0)
            return;

        MarketOrder(_symbol, shares, tag: $"Breakout above {_entryHigh.Period}-day high {breakoutLevel:0.00}");
        _stopPrice = price - stopDistance;
    }

    public override void OnEndOfAlgorithm()
    {
        Liquidate();
    }
}
