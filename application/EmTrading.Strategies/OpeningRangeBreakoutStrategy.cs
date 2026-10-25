using EmTrading.Strategies.Metadata;
using QuantConnect;
using QuantConnect.Data;
using QuantConnect.Data.Market;

namespace EmTrading.Strategies;

/// <summary>
/// Intraday opening range breakout on minute bars: records the high and low of the first minutes of the session,
/// buys once if the price breaks above that range, stops out below the range low, and is always flat before the
/// close, so no position is held overnight. Long only.
/// The 60-minute default range held up best out of sample on AAPL and SPY, 2024–2026 (MSFT preferred 15).
/// </summary>
[Strategy("Opening range breakout", Description = "Intraday: buy a break above the first N minutes' high, stop at the range low, flat before the close. Long only.")]
[StrategyParameter(RangeMinutes, "Opening range (minutes)", StrategyParameterType.Integer, 60, Minimum = 5, Maximum = 120,
    Description = "Minutes after the 9:30 open that define the range.",
    SuggestedFrom = 15, SuggestedTo = 60, SuggestedStep = 15)]
[StrategyParameter(ExitMinutesBeforeClose, "Exit before close (minutes)", StrategyParameterType.Integer, 5, Minimum = 1, Maximum = 120,
    Description = "Sell this many minutes before the 16:00 close at the latest.",
    SuggestedFrom = 5, SuggestedTo = 60, SuggestedStep = 15)]
[StrategyParameter(MinRangePercent, "Minimum range (%)", StrategyParameterType.Decimal, 0.3, Minimum = 0, Maximum = 5,
    Description = "Skip days whose opening range is narrower than this share of the price (too little movement).",
    SuggestedFrom = 0, SuggestedTo = 1, SuggestedStep = 0.25)]
[StrategyParameter(AllocationPercent, "Allocation (%)", StrategyParameterType.Decimal, 90, Minimum = 1, Maximum = 100,
    Description = "Share of equity invested per position.")]
public class OpeningRangeBreakoutStrategy : StrategyBase
{
    public const string RangeMinutes = "range-minutes";
    public const string ExitMinutesBeforeClose = "exit-minutes-before-close";
    public const string MinRangePercent = "min-range-percent";
    public const string AllocationPercent = "allocation-percent";

    private static readonly TimeSpan MarketOpen = new(9, 30, 0);
    private static readonly TimeSpan MarketClose = new(16, 0, 0);

    private Symbol _symbol = null!;
    private TimeSpan _rangeEnd;
    private TimeSpan _exitTime;
    private decimal _minRange;
    private decimal _allocation;

    private DateTime _day;
    private decimal _rangeHigh;
    private decimal _rangeLow;
    private bool _tradedToday;

    public override void Initialize()
    {
        _symbol = SetUpFromEngineParameters(Resolution.Minute);
        _rangeEnd = MarketOpen + TimeSpan.FromMinutes(IntegerParameter(RangeMinutes));
        _exitTime = MarketClose - TimeSpan.FromMinutes(IntegerParameter(ExitMinutesBeforeClose));
        _minRange = DecimalParameter(MinRangePercent) / 100m;
        _allocation = DecimalParameter(AllocationPercent) / 100m;
    }

    /// <summary>Algorithm time is New York time, so the session runs 9:30 to 16:00.</summary>
    public override void OnData(Slice slice)
    {
        if (!slice.Bars.TryGetValue(_symbol, out var bar))
            return;

        var time = bar.Time.TimeOfDay;
        if (bar.Time.Date != _day)
            StartDay(bar.Time.Date);

        if (time < MarketOpen)
            return;

        if (time < _rangeEnd)
        {
            _rangeHigh = Math.Max(_rangeHigh, bar.High);
            _rangeLow = Math.Min(_rangeLow, bar.Low);
            return;
        }

        if (Portfolio.Invested)
        {
            if (bar.Close <= _rangeLow)
                Liquidate(symbol: _symbol, tag: $"Below range low {_rangeLow:0.00}");
            else if (time >= _exitTime)
                Liquidate(symbol: _symbol, tag: "End of day");
            return;
        }

        var wideEnough = _rangeLow > 0 && (_rangeHigh - _rangeLow) / _rangeLow >= _minRange;
        if (!_tradedToday && wideEnough && time < _exitTime && bar.Close > _rangeHigh)
        {
            SetHoldings(_symbol, _allocation, tag: $"Break above opening range {_rangeHigh:0.00}");
            _tradedToday = true;
        }
    }

    private void StartDay(DateTime day)
    {
        _day = day;
        _rangeHigh = decimal.MinValue;
        _rangeLow = decimal.MaxValue;
        _tradedToday = false;
    }

    public override void OnEndOfAlgorithm()
    {
        Liquidate();
    }
}
