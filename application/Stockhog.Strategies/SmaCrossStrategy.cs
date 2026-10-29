using Stockhog.Strategies.Metadata;
using QuantConnect;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Orders;

namespace Stockhog.Strategies;

/// <summary>
/// Trend following on 15-minute bars: EMA fast/slow cross, filtered by ADX and by the price relative to the
/// 100 and 200 day closes. Positions are sized by risk and exited by an ATR trailing stop.
/// </summary>
/// <remarks>The daily filter needs 200 trading days of data before the start date for warm-up.</remarks>
[Strategy("EMA Cross + ADX trend", Description = "15-minute EMA cross filtered by ADX and the 100/200-day closes; ATR trailing stop.")]
[StrategyParameter(FastPeriod, "Fast EMA period", StrategyParameterType.Integer, 26, Minimum = 2, Maximum = 500,
    Description = "In 15-minute bars.", SuggestedFrom = 16, SuggestedTo = 46, SuggestedStep = 5)]
[StrategyParameter(SlowPeriod, "Slow EMA period", StrategyParameterType.Integer, 130, Minimum = 3, Maximum = 2000,
    MustExceed = FastPeriod, Description = "In 15-minute bars.", SuggestedFrom = 78, SuggestedTo = 234, SuggestedStep = 26)]
[StrategyParameter(AdxThreshold, "ADX threshold", StrategyParameterType.Decimal, 24, Minimum = 0, Maximum = 100,
    Description = "Trades only when the trend is at least this strong.", SuggestedFrom = 18, SuggestedTo = 30, SuggestedStep = 4)]
[StrategyParameter(AtrStopMultiplier, "ATR stop multiplier", StrategyParameterType.Decimal, 1.5, Minimum = 0.1, Maximum = 10,
    Description = "Stop distance as a multiple of the 14-bar ATR.", SuggestedFrom = 1, SuggestedTo = 3, SuggestedStep = 0.5)]
[StrategyParameter(RiskPercent, "Risk per trade (%)", StrategyParameterType.Decimal, 1, Minimum = 0.1, Maximum = 10,
    Description = "Equity lost if the initial stop is hit.", SuggestedFrom = 0.5, SuggestedTo = 2, SuggestedStep = 0.5)]
public class SmaCrossStrategy : StrategyBase
{
    public const string FastPeriod = "fast-period";
    public const string SlowPeriod = "slow-period";
    public const string AdxThreshold = "adx-threshold";
    public const string AtrStopMultiplier = "atr-stop-multiplier";
    public const string RiskPercent = "risk-percent";

    private const int DailyLookback = 200;

    private Symbol _symbol = null!;
    private decimal _adxThreshold;
    private decimal _atrStopMultiplier;
    private decimal _riskPerTrade;

    // 15-minute indicators
    private ExponentialMovingAverage _emaFast = null!;
    private ExponentialMovingAverage _emaSlow = null!;
    private AverageDirectionalIndex _adx = null!;
    private AverageTrueRange _atr = null!;

    // Daily closes for the trend filter
    private RollingWindow<decimal> _dailyCloses = null!;

    // Trailing stop state
    private decimal _stopLossPrice;
    private bool _isLong;

    public override void Initialize()
    {
        _symbol = SetUpFromEngineParameters(Resolution.Minute);
        _adxThreshold = DecimalParameter(AdxThreshold);
        _atrStopMultiplier = DecimalParameter(AtrStopMultiplier);
        _riskPerTrade = DecimalParameter(RiskPercent) / 100m;

        _dailyCloses = new RollingWindow<decimal>(DailyLookback + 1);
        var dailyConsolidator = new TradeBarConsolidator(TimeSpan.FromDays(1));
        dailyConsolidator.DataConsolidated += (_, bar) => _dailyCloses.Add(bar.Close);
        SubscriptionManager.AddConsolidator(_symbol, dailyConsolidator);

        var consolidator15M = new TradeBarConsolidator(TimeSpan.FromMinutes(15));
        _emaFast = new ExponentialMovingAverage(IntegerParameter(FastPeriod));
        _emaSlow = new ExponentialMovingAverage(IntegerParameter(SlowPeriod));
        _adx = new AverageDirectionalIndex(14);
        _atr = new AverageTrueRange(14);

        RegisterIndicator(_symbol, _emaFast, consolidator15M);
        RegisterIndicator(_symbol, _emaSlow, consolidator15M);
        RegisterIndicator(_symbol, _adx, consolidator15M);
        RegisterIndicator(_symbol, _atr, consolidator15M);

        consolidator15M.DataConsolidated += On15MinuteBar;
        SubscriptionManager.AddConsolidator(_symbol, consolidator15M);

        SetWarmUpTradingDays(DailyLookback + 1);
    }

    private void On15MinuteBar(object? sender, TradeBar bar)
    {
        if (IsWarmingUp || !_dailyCloses.IsReady || !_adx.IsReady || !_emaSlow.IsReady)
            return;

        var price = bar.Close;
        var close100 = _dailyCloses[100];
        var close200 = _dailyCloses[DailyLookback];

        var dailyLongFilter = price > close100 && price > close200;
        var dailyShortFilter = price < close100 && price < close200;
        var adxFilter = _adx.Current.Value > _adxThreshold;

        if (!Portfolio.Invested)
        {
            if (dailyLongFilter && adxFilter && _emaFast.Current.Value > _emaSlow.Current.Value)
                EnterPosition(OrderDirection.Buy, price);
            else if (dailyShortFilter && adxFilter && _emaFast.Current.Value < _emaSlow.Current.Value)
                EnterPosition(OrderDirection.Sell, price);
        }
        else
        {
            UpdateTrailingStop(price);
        }
    }

    private void EnterPosition(OrderDirection direction, decimal currentPrice)
    {
        var stopDistance = _atr.Current.Value * _atrStopMultiplier;
        if (stopDistance <= 0)
            return;

        var riskAmount = Portfolio.TotalPortfolioValue * _riskPerTrade;
        var shares = (int)Math.Floor(riskAmount / stopDistance);
        if (shares == 0)
            return;

        if (direction == OrderDirection.Buy)
        {
            MarketOrder(_symbol, shares);
            _stopLossPrice = currentPrice - stopDistance;
            _isLong = true;
        }
        else
        {
            MarketOrder(_symbol, -shares);
            _stopLossPrice = currentPrice + stopDistance;
            _isLong = false;
        }
    }

    private void UpdateTrailingStop(decimal currentPrice)
    {
        var stopDistance = _atr.Current.Value * _atrStopMultiplier;

        if (_isLong)
        {
            _stopLossPrice = Math.Max(_stopLossPrice, currentPrice - stopDistance);
            if (currentPrice <= _stopLossPrice)
                Liquidate(symbol: _symbol, tag: "Trailing Stop Long");
        }
        else
        {
            _stopLossPrice = Math.Min(_stopLossPrice, currentPrice + stopDistance);
            if (currentPrice >= _stopLossPrice)
                Liquidate(symbol: _symbol, tag: "Trailing Stop Short");
        }
    }

    public override void OnEndOfAlgorithm()
    {
        Liquidate();
    }
}
