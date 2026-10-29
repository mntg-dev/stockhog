using Stockhog.Strategies.Metadata;
using QuantConnect;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;

namespace Stockhog.Strategies;

/// <summary>
/// Mean reversion on hourly bars: buys when the close falls below the lower Bollinger band (an unusually large move
/// down) and sells when it returns to the middle band, or on a fixed stop loss. Long only.
/// </summary>
[Strategy("Bollinger band reversion", Description = "Hourly: buy a close below the lower band, sell at the middle band or on a % stop. Long only.")]
[StrategyParameter(Period, "Band period (hours)", StrategyParameterType.Integer, 20, Minimum = 5, Maximum = 200,
    Description = "Moving average and standard deviation over this many hourly bars.",
    SuggestedFrom = 10, SuggestedTo = 40, SuggestedStep = 5)]
[StrategyParameter(Width, "Band width (σ)", StrategyParameterType.Decimal, 2, Minimum = 0.5, Maximum = 4,
    Description = "How many standard deviations below the average the lower band is.",
    SuggestedFrom = 1.5, SuggestedTo = 3, SuggestedStep = 0.5)]
[StrategyParameter(StopLossPercent, "Stop loss (%)", StrategyParameterType.Decimal, 4, Minimum = 0.5, Maximum = 30,
    Description = "Exit when the price falls this far below the entry.",
    SuggestedFrom = 2, SuggestedTo = 8, SuggestedStep = 2)]
[StrategyParameter(AllocationPercent, "Allocation (%)", StrategyParameterType.Decimal, 90, Minimum = 1, Maximum = 100,
    Description = "Share of equity invested per position.")]
public class BollingerReversionStrategy : StrategyBase
{
    public const string Period = "band-period";
    public const string Width = "band-width";
    public const string StopLossPercent = "stop-loss-percent";
    public const string AllocationPercent = "allocation-percent";

    private Symbol _symbol = null!;
    private BollingerBands _bands = null!;
    private decimal _stopLoss;
    private decimal _allocation;
    private decimal _entryPrice;

    public override void Initialize()
    {
        _symbol = SetUpFromEngineParameters(Resolution.Minute);
        _stopLoss = DecimalParameter(StopLossPercent) / 100m;
        _allocation = DecimalParameter(AllocationPercent) / 100m;

        var period = IntegerParameter(Period);
        _bands = new BollingerBands(period, DecimalParameter(Width), MovingAverageType.Simple);

        var hourly = new TradeBarConsolidator(TimeSpan.FromHours(1));
        hourly.DataConsolidated += OnHourlyBar;
        SubscriptionManager.AddConsolidator(_symbol, hourly);

        // About 7 trading hours a day.
        SetWarmUp(TimeSpan.FromDays(Math.Max(5, period / 3 + 3)));
    }

    private void OnHourlyBar(object? sender, TradeBar bar)
    {
        _bands.Update(bar.EndTime, bar.Close);
        if (IsWarmingUp || !_bands.IsReady)
            return;

        var lower = _bands.LowerBand.Current.Value;
        var middle = _bands.MiddleBand.Current.Value;

        if (!Portfolio.Invested)
        {
            if (bar.Close < lower)
            {
                SetHoldings(_symbol, _allocation, tag: $"Close {bar.Close:0.00} below lower band {lower:0.00}");
                _entryPrice = bar.Close;
            }
        }
        else if (bar.Close >= middle)
        {
            Liquidate(symbol: _symbol, tag: $"Back at middle band {middle:0.00}");
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
