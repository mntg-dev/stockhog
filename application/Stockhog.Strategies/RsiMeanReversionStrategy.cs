using Stockhog.Strategies.Metadata;
using QuantConnect;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;

namespace Stockhog.Strategies;

/// <summary>
/// Long-only mean reversion on hourly bars: buys when RSI drops below the oversold level and sells when it
/// rises above the overbought level, or when the price falls by the stop-loss percentage from the entry.
/// </summary>
[Strategy("RSI mean reversion", Description = "Hourly RSI: buy oversold, sell overbought or on a fixed stop loss. Long only.")]
[StrategyParameter(RsiPeriod, "RSI period", StrategyParameterType.Integer, 14, Minimum = 2, Maximum = 100,
    Description = "In hourly bars.", SuggestedFrom = 7, SuggestedTo = 21, SuggestedStep = 7)]
[StrategyParameter(Oversold, "Oversold level", StrategyParameterType.Decimal, 30, Minimum = 1, Maximum = 50,
    Description = "Buy when the RSI closes below this level.", SuggestedFrom = 20, SuggestedTo = 35, SuggestedStep = 5)]
[StrategyParameter(Overbought, "Overbought level", StrategyParameterType.Decimal, 70, Minimum = 50, Maximum = 99,
    MustExceed = Oversold, Description = "Sell when the RSI closes above this level.", SuggestedFrom = 65, SuggestedTo = 80, SuggestedStep = 5)]
[StrategyParameter(StopLossPercent, "Stop loss (%)", StrategyParameterType.Decimal, 5, Minimum = 0.5, Maximum = 50,
    Description = "Exit when the price falls this far below the entry.", SuggestedFrom = 2, SuggestedTo = 8, SuggestedStep = 2)]
[StrategyParameter(AllocationPercent, "Allocation (%)", StrategyParameterType.Decimal, 90, Minimum = 1, Maximum = 100,
    Description = "Share of equity invested per position.")]
public class RsiMeanReversionStrategy : StrategyBase
{
    public const string RsiPeriod = "rsi-period";
    public const string Oversold = "oversold";
    public const string Overbought = "overbought";
    public const string StopLossPercent = "stop-loss-percent";
    public const string AllocationPercent = "allocation-percent";

    private Symbol _symbol = null!;
    private RelativeStrengthIndex _rsi = null!;
    private decimal _oversold;
    private decimal _overbought;
    private decimal _stopLoss;
    private decimal _allocation;
    private decimal _entryPrice;

    public override void Initialize()
    {
        _symbol = SetUpFromEngineParameters(Resolution.Minute);
        _oversold = DecimalParameter(Oversold);
        _overbought = DecimalParameter(Overbought);
        _stopLoss = DecimalParameter(StopLossPercent) / 100m;
        _allocation = DecimalParameter(AllocationPercent) / 100m;

        var hourly = new TradeBarConsolidator(TimeSpan.FromHours(1));
        var period = IntegerParameter(RsiPeriod);
        _rsi = new RelativeStrengthIndex(period, MovingAverageType.Wilders);
        RegisterIndicator(_symbol, _rsi, hourly);

        hourly.DataConsolidated += OnHourlyBar;
        SubscriptionManager.AddConsolidator(_symbol, hourly);

        // Roughly two RSI periods of trading hours.
        SetWarmUp(TimeSpan.FromDays(Math.Max(5, period / 2)));
    }

    private void OnHourlyBar(object? sender, TradeBar bar)
    {
        if (IsWarmingUp || !_rsi.IsReady)
            return;

        var rsi = _rsi.Current.Value;
        if (!Portfolio.Invested)
        {
            if (rsi < _oversold)
            {
                SetHoldings(_symbol, _allocation, tag: $"RSI {rsi:0.0} below {_oversold}");
                _entryPrice = bar.Close;
            }
        }
        else if (rsi > _overbought)
        {
            Liquidate(symbol: _symbol, tag: $"RSI {rsi:0.0} above {_overbought}");
        }
        else if (bar.Close <= _entryPrice * (1 - _stopLoss))
        {
            Liquidate(symbol: _symbol, tag: "Stop loss");
        }
    }

    public override void OnEndOfAlgorithm()
    {
        Liquidate();
    }
}
