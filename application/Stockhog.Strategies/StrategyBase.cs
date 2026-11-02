using System.Globalization;
using System.Reflection;
using Stockhog.Strategies.Metadata;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Orders;

namespace Stockhog.Strategies;

/// <summary>
/// Common setup for the app's strategies: period, cash and symbol come from <see cref="EngineParameters"/>,
/// and strategy inputs fall back to the defaults declared in their <see cref="StrategyParameterAttribute"/>.
/// At the end of the run it records the strategy's position (<see cref="StrategyState"/>), then closes it.
/// </summary>
public abstract class StrategyBase : QCAlgorithm
{
    private Symbol? _tradedSymbol;
    private DateTime? _entryTime;
    private string? _entryTag;
    private DateTime? _lastExitTime;
    private string? _lastExitTag;

    /// <summary>
    /// The price the strategy would exit its position at, if it uses a stop; read at the end of the run, only when the
    /// strategy holds or is about to enter a position. Set when entering, so it is known for an entry still pending.
    /// </summary>
    protected virtual decimal? CurrentStopPrice => null;

    /// <summary>
    /// Applies the engine parameters and subscribes to the requested equity.
    /// </summary>
    protected Symbol SetUpFromEngineParameters(Resolution resolution)
    {
        SetStartDate(GetDateParameter(EngineParameters.StartDate, new DateTime(2025, 1, 1)));
        SetEndDate(GetDateParameter(EngineParameters.EndDate, DateTime.Today));
        SetCash(GetParameter(EngineParameters.Cash, 100_000m));

        var ticker = GetParameter(EngineParameters.Symbol, "AAPL").Trim().ToUpperInvariant();
        _tradedSymbol = AddEquity(ticker, resolution).Symbol;
        return _tradedSymbol;
    }

    /// <summary>Remembers when the position was opened and last closed. Holdings already include the fill here.</summary>
    public override void OnOrderEvent(OrderEvent orderEvent)
    {
        if (orderEvent.Status is not (OrderStatus.Filled or OrderStatus.PartiallyFilled) || orderEvent.Symbol != _tradedSymbol)
            return;

        var quantityAfter = Portfolio[orderEvent.Symbol].Quantity;
        var quantityBefore = quantityAfter - orderEvent.FillQuantity;
        var tag = Transactions.GetOrderById(orderEvent.OrderId)?.Tag;

        if (quantityBefore == 0 && quantityAfter != 0)
        {
            _entryTime = Time;
            _entryTag = tag;
        }
        else if (quantityBefore != 0 && quantityAfter == 0)
        {
            _lastExitTime = Time;
            _lastExitTag = tag;
        }
    }

    /// <summary>
    /// Records where the strategy stands, then closes any position so the run's result counts every trade.
    /// Sealed: strategies describe their stop through <see cref="CurrentStopPrice"/> instead.
    /// </summary>
    public sealed override void OnEndOfAlgorithm()
    {
        if (string.Equals(GetParameter(EngineParameters.SignalMode), "true", StringComparison.OrdinalIgnoreCase))
            CloseOpenBars();

        WriteState();
        Liquidate();
    }

    /// <summary>
    /// A bar is only emitted once data of the next period arrives, so the run ends before the strategy has seen the
    /// last session's daily (or last hour's) bar. Emitting them now lets it decide on the latest close.
    /// </summary>
    private void CloseOpenBars()
    {
        if (_tradedSymbol is null)
            return;

        var nextDay = Time.Date.AddDays(1);
        foreach (var subscription in SubscriptionManager.Subscriptions.Where(s => s.Symbol == _tradedSymbol).ToList())
        foreach (var consolidator in subscription.Consolidators.ToList())
            consolidator.Scan(nextDay);
    }

    private void WriteState()
    {
        var path = GetParameter(EngineParameters.StateFile);
        if (string.IsNullOrWhiteSpace(path) || _tradedSymbol is null)
            return;

        try
        {
            var security = Securities[_tradedSymbol];
            var holding = Portfolio[_tradedSymbol];

            // Orders still waiting (placed after the close, e.g. by CloseOpenBars) are what the strategy intends next.
            var pending = Transactions.GetOpenOrders(_tradedSymbol);
            var quantity = holding.Quantity + pending.Sum(o => o.Quantity);

            var entryTime = _entryTime;
            var entryTag = _entryTag;
            var averagePrice = holding.AveragePrice;
            var exitTime = _lastExitTime;
            var exitTag = _lastExitTag;
            if (holding.Quantity == 0 && quantity != 0)
            {
                entryTime = Time;
                entryTag = pending.LastOrDefault()?.Tag;
                averagePrice = security.Price;
            }
            else if (holding.Quantity != 0 && quantity == 0)
            {
                exitTime = Time;
                exitTag = pending.LastOrDefault()?.Tag;
            }

            var invested = quantity != 0;
            new StrategyState(
                Time,
                security.GetLastData()?.EndTime,
                security.Price,
                quantity,
                invested ? averagePrice : 0m,
                invested ? entryTime : null,
                invested ? entryTag : null,
                invested ? CurrentStopPrice : null,
                exitTime,
                exitTag).Save(path);
        }
        catch (Exception ex)
        {
            // The backtest result matters more than the state file: report and carry on.
            Error($"Could not write the strategy state to {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Warms up indicators built from daily bars with the minute data the strategy subscribes to. The app stores minute
    /// data, so <c>SetWarmUp(n, Resolution.Daily)</c> would ask for daily files that do not exist and warm up nothing.
    /// Trading days are converted to calendar days with room for weekends and holidays.
    /// </summary>
    protected void SetWarmUpTradingDays(int tradingDays) =>
        SetWarmUp(TimeSpan.FromDays(Math.Ceiling(tradingDays * 7 / 5.0 * 1.05) + 7));

    protected int IntegerParameter(string name) => GetParameter(name, (int)Describe(name).DefaultValue);

    protected decimal DecimalParameter(string name) => GetParameter(name, (decimal)Describe(name).DefaultValue);

    private StrategyParameterAttribute Describe(string name) =>
        GetType().GetCustomAttributes<StrategyParameterAttribute>().SingleOrDefault(a => a.Name == name)
        ?? throw new InvalidOperationException($"{GetType().Name} does not declare parameter '{name}'.");

    private DateTime GetDateParameter(string name, DateTime defaultValue) =>
        DateTime.TryParseExact(GetParameter(name), EngineParameters.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : defaultValue;
}
