using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Domain.Trading;

/// <summary>
/// An order sent to the broker. The app creates it as <see cref="OrderStatus.New"/> and saves it before submitting,
/// so a crash during the submit leaves a record to reconcile. After that its state follows the broker's reports
/// (trade updates and reconciliation), which may arrive more than once and out of order: fills are cumulative
/// and never move backwards, and a final state is never left.
/// </summary>
public sealed class Order : AggregateRoot<ClientOrderId>
{
    private Order(
        ClientOrderId id,
        Symbol symbol,
        OrderSide side,
        OrderType type,
        decimal quantity,
        decimal? limitPrice,
        decimal? stopPrice,
        TimeInForce timeInForce,
        DateTimeOffset createdAt) : base(id)
    {
        Symbol = symbol;
        Side = side;
        Type = type;
        Quantity = quantity;
        LimitPrice = limitPrice;
        StopPrice = stopPrice;
        TimeInForce = timeInForce;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Status = OrderStatus.New;
    }

#pragma warning disable CS8618 // Persistence materializes orders through this constructor and then sets every property.
    private Order() : base(default!)
    {
    }
#pragma warning restore CS8618

    public Symbol Symbol { get; }
    public OrderSide Side { get; }
    public OrderType Type { get; }
    public decimal Quantity { get; }
    public decimal? LimitPrice { get; }
    public decimal? StopPrice { get; }
    public TimeInForce TimeInForce { get; }

    public OrderStatus Status { get; private set; }

    /// <summary>The broker's id, known once the broker has accepted the order.</summary>
    public string? BrokerOrderId { get; private set; }

    public decimal FilledQuantity { get; private set; }
    public decimal? AverageFillPrice { get; private set; }
    public string? RejectReason { get; private set; }

    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Increases with every state change. Storage uses it to refuse a save based on an outdated copy, e.g. when a trade
    /// update and the submitting handler change the same order at the same time.
    /// </summary>
    public int Version { get; private set; }

    public bool IsFinal => Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired;

    /// <summary>Accepted by the broker and still able to fill.</summary>
    public bool IsOpen => Status is OrderStatus.Submitted or OrderStatus.PartiallyFilled;

    public decimal RemainingQuantity => Quantity - FilledQuantity;

    public static Order Create(
        ClientOrderId id,
        Symbol symbol,
        OrderSide side,
        OrderType type,
        decimal quantity,
        decimal? limitPrice,
        decimal? stopPrice,
        TimeInForce timeInForce,
        DateTimeOffset createdAt)
    {
        if (quantity <= 0)
            throw new DomainException("Quantity must be positive.");
        if (quantity != decimal.Truncate(quantity) && type != OrderType.Market)
            throw new DomainException("Fractional quantities are allowed only for market orders.");
        if (quantity != decimal.Truncate(quantity) && timeInForce != TimeInForce.Day)
            throw new DomainException("Fractional orders must be day orders.");

        var needsLimit = type is OrderType.Limit or OrderType.StopLimit;
        var needsStop = type is OrderType.Stop or OrderType.StopLimit;
        if (needsLimit != limitPrice.HasValue)
            throw new DomainException(needsLimit ? $"A {type} order needs a limit price." : $"A {type} order has no limit price.");
        if (needsStop != stopPrice.HasValue)
            throw new DomainException(needsStop ? $"A {type} order needs a stop price." : $"A {type} order has no stop price.");
        if (limitPrice <= 0 || stopPrice <= 0)
            throw new DomainException("Prices must be positive.");

        var order = new Order(id, symbol, side, type, quantity, limitPrice, stopPrice, timeInForce, createdAt);
        order.Raise(new OrderCreated(id, symbol, side, type, quantity, limitPrice, stopPrice, createdAt));
        return order;
    }

    /// <summary>The broker accepted the order.</summary>
    public void MarkSubmitted(string brokerOrderId, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
            throw new DomainException("The broker order id is required.");

        // A trade update may already have moved the order on; only the id is new information then.
        BrokerOrderId ??= brokerOrderId;
        SubmittedAt ??= at;
        if (Status != OrderStatus.New)
            return;

        Status = OrderStatus.Submitted;
        UpdatedAt = at;
        Version++;
        Raise(new OrderSubmitted(Id, Symbol, Side, Quantity, at));
    }

    /// <summary>
    /// Applies the broker's cumulative fill. Repeated or stale reports (a smaller filled quantity) are ignored.
    /// </summary>
    /// <returns><c>true</c> if the order changed.</returns>
    public bool ApplyFill(decimal cumulativeQuantity, decimal averagePrice, DateTimeOffset at)
    {
        // Report the final fill of a cancelled order with ApplyFill first, then Cancel.
        if (IsFinal || cumulativeQuantity <= FilledQuantity)
            return false;
        if (cumulativeQuantity > Quantity)
            throw new DomainException($"Order {Id} cannot fill {cumulativeQuantity} of {Quantity}.");
        if (averagePrice <= 0)
            throw new DomainException("The fill price must be positive.");

        FilledQuantity = cumulativeQuantity;
        AverageFillPrice = averagePrice;
        SubmittedAt ??= at;
        UpdatedAt = at;
        Version++;

        if (FilledQuantity == Quantity)
        {
            Status = OrderStatus.Filled;
            Raise(new OrderFilled(Id, Symbol, Side, FilledQuantity, averagePrice, at));
        }
        else
        {
            Status = OrderStatus.PartiallyFilled;
            Raise(new OrderPartiallyFilled(Id, Symbol, Side, FilledQuantity, averagePrice, at));
        }

        return true;
    }

    /// <returns><c>true</c> if the order changed; an order that is already final stays as it is.</returns>
    public bool Cancel(DateTimeOffset at) => Close(OrderStatus.Cancelled, null, at);

    /// <returns><c>true</c> if the order changed; an order that is already final stays as it is.</returns>
    public bool Expire(DateTimeOffset at) => Close(OrderStatus.Expired, null, at);

    /// <returns><c>true</c> if the order changed; an order that is already final stays as it is.</returns>
    public bool Reject(string reason, DateTimeOffset at) =>
        Close(OrderStatus.Rejected, string.IsNullOrWhiteSpace(reason) ? "Rejected by the broker." : reason.Trim(), at);

    private bool Close(OrderStatus status, string? reason, DateTimeOffset at)
    {
        if (IsFinal)
            return false;

        Status = status;
        RejectReason = reason;
        UpdatedAt = at;
        Version++;
        Raise(new OrderClosed(Id, Symbol, status, FilledQuantity, reason, at));
        return true;
    }
}

public sealed record OrderCreated(
    ClientOrderId OrderId,
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    decimal? LimitPrice,
    decimal? StopPrice,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderSubmitted(ClientOrderId OrderId, Symbol Symbol, OrderSide Side, decimal Quantity, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderPartiallyFilled(ClientOrderId OrderId, Symbol Symbol, OrderSide Side, decimal FilledQuantity, decimal AveragePrice, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderFilled(ClientOrderId OrderId, Symbol Symbol, OrderSide Side, decimal Quantity, decimal AveragePrice, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Cancelled, rejected or expired; <paramref name="FilledQuantity"/> may be above zero after a partial fill.</summary>
public sealed record OrderClosed(ClientOrderId OrderId, Symbol Symbol, OrderStatus Status, decimal FilledQuantity, string? Reason, DateTimeOffset OccurredAt) : IDomainEvent;
