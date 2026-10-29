using System.ComponentModel;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Trading;

/// <summary>
/// The broker's trading API. Implementations throw <see cref="BrokerException"/> for anything the broker refuses
/// or that cannot be completed.
/// </summary>
public interface IBrokerGateway
{
    Task<BrokerAccount> GetAccountAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BrokerPosition>> GetPositionsAsync(CancellationToken cancellationToken = default);

    /// <returns><c>null</c> if the broker has no order with this id (e.g. the submit never arrived).</returns>
    Task<BrokerOrder?> GetOrderAsync(ClientOrderId clientOrderId, CancellationToken cancellationToken = default);

    /// <summary>Sends the order with its <see cref="Order.Id"/> as the client order id.</summary>
    Task<BrokerOrder> SubmitOrderAsync(Order order, CancellationToken cancellationToken = default);

    Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The broker refused a request or could not be reached.
/// </summary>
/// <param name="isRejection"><c>true</c> when the broker answered and refused (the order certainly does not exist);
/// <c>false</c> when the outcome is unknown, e.g. a timeout, and must be found out by reconciliation.</param>
public sealed class BrokerException(string message, bool isRejection, Exception? innerException = null) : Exception(message, innerException)
{
    public bool IsRejection { get; } = isRejection;
}

/// <param name="LastEquity">Equity at the previous trading day's close.</param>
[ImmutableObject(true)]
public sealed record BrokerAccount(
    string AccountNumber,
    string Currency,
    decimal Equity,
    decimal LastEquity,
    decimal Cash,
    decimal BuyingPower,
    bool TradingBlocked,
    bool IsPaper)
{
    public decimal DailyProfitLoss => Equity - LastEquity;

    public decimal DailyProfitLossPercent => LastEquity == 0 ? 0 : DailyProfitLoss / LastEquity * 100;
}

/// <param name="Quantity">Signed: negative for a short position.</param>
public sealed record BrokerPosition(
    string Symbol,
    decimal Quantity,
    decimal AverageEntryPrice,
    decimal CurrentPrice,
    decimal MarketValue,
    decimal UnrealizedProfitLoss,
    decimal UnrealizedProfitLossPercent);

/// <summary>The broker's view of an order, mapped onto the app's statuses.</summary>
public sealed record BrokerOrder(
    string BrokerOrderId,
    string? ClientOrderId,
    string Symbol,
    OrderStatus Status,
    decimal Quantity,
    decimal FilledQuantity,
    decimal? AverageFillPrice,
    DateTimeOffset UpdatedAt,
    string? Reason = null);

/// <param name="Event">The broker's event name, e.g. <c>fill</c>, <c>partial_fill</c>, <c>canceled</c>.</param>
public sealed record TradeUpdate(string Event, BrokerOrder Order, DateTimeOffset OccurredAt);

/// <summary>Latest prices from the live stream, written by its reader and read by anyone.</summary>
public interface IQuoteCache
{
    bool TryGet(Symbol symbol, out Quote quote);

    IReadOnlyList<Quote> Snapshot();
}

/// <param name="Last">Price of the latest trade, if any.</param>
public sealed record Quote(string Symbol, decimal? Bid, decimal? Ask, decimal? Last, DateTimeOffset Time)
{
    /// <summary>The best single price: the last trade, else the bid/ask midpoint, else whichever side exists.</summary>
    public decimal? Price => Last ?? (Bid is { } bid && Ask is { } ask && bid > 0 && ask > 0 ? (bid + ask) / 2 : Bid ?? Ask);
}

/// <summary>The latest traded price when the live stream has none, e.g. before the first quote arrives.</summary>
public interface ILatestPriceSource
{
    Task<decimal?> GetLatestPriceAsync(Symbol symbol, CancellationToken cancellationToken = default);
}

/// <summary>The risk limits in force; the kill switch can be flipped while the app runs.</summary>
public interface IRiskLimitsStore
{
    RiskLimits Current { get; }

    void Update(RiskLimits limits);
}

public interface IOrderRepository
{
    void Add(Order order);

    Task<Order?> GetAsync(ClientOrderId id, CancellationToken cancellationToken = default);

    /// <summary>Orders not yet in a final state: new (maybe never submitted), submitted or partially filled.</summary>
    Task<IReadOnlyList<Order>> ListUnfinishedAsync(CancellationToken cancellationToken = default);
}

public interface IOrderReader
{
    /// <returns>Newest first.</returns>
    Task<IReadOnlyList<OrderSummary>> ListAsync(bool openOnly, int take, CancellationToken cancellationToken = default);
}

public sealed record OrderSummary(
    string ClientOrderId,
    string? BrokerOrderId,
    string Symbol,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    decimal? LimitPrice,
    decimal? StopPrice,
    TimeInForce TimeInForce,
    OrderStatus Status,
    decimal FilledQuantity,
    decimal? AverageFillPrice,
    string? RejectReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsOpen => Status is OrderStatus.Submitted or OrderStatus.PartiallyFilled;

    /// <summary>Can be cancelled: open at the broker, or new and maybe still on its way there.</summary>
    public bool IsCancellable => IsOpen || Status == OrderStatus.New;
}

public static class TradingErrors
{
    public static Error RiskRejected(IReadOnlyList<string> violations) =>
        new("Trading.RiskRejected", string.Join(" ", violations), ErrorType.Conflict) { Details = violations };

    public static Error BrokerRejected(string reason) =>
        Error.Failure("Trading.BrokerRejected", $"The broker rejected the order: {reason}");

    public static Error BrokerUnavailable(string reason) =>
        Error.Failure("Trading.BrokerUnavailable", $"The broker could not be reached: {reason}");

    public static Error SubmitUnknown(string clientOrderId, string reason) =>
        Error.Failure("Trading.SubmitUnknown", $"Order {clientOrderId} may not have reached the broker ({reason}); it will be checked automatically.");

    public static Error NoPrice(string symbol) =>
        Error.Failure("Trading.NoPrice", $"No current price for {symbol}; the risk checks need one.");

    public static Error OrderNotFound(string id) =>
        Error.NotFound("Trading.OrderNotFound", $"Order {id} does not exist.");

    public static Error OrderNotCancellable(string id, OrderStatus status) =>
        new("Trading.NotCancellable", $"Order {id} is {status} and cannot be cancelled.", ErrorType.Conflict);

    public static Error NoPosition(string symbol) =>
        Error.NotFound("Trading.NoPosition", $"There is no open {symbol} position.");
}

/// <summary>Cache keys shared by the account queries and the handlers that invalidate them.</summary>
internal static class TradingCacheKeys
{
    public const string AccountTag = "trading:account";
}
