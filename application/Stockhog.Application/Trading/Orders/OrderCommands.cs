using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Common;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace Stockhog.Application.Trading.Orders;

/// <summary>
/// Asks the broker to cancel an order. The broker confirms asynchronously; the trade update stream (or the next
/// reconciliation) then marks the order as cancelled, or as filled if the fill won the race.
/// </summary>
public sealed record CancelOrderCommand(string ClientOrderId) : ICommand<OrderStatus>;

internal sealed class CancelOrderCommandValidator : AbstractValidator<CancelOrderCommand>
{
    public CancelOrderCommandValidator() =>
        RuleFor(c => c.ClientOrderId).Must(ClientOrderId.IsValid).WithMessage("'{PropertyValue}' is not a valid order id.");
}

internal sealed class CancelOrderCommandHandler(IBrokerGateway broker, IOrderRepository orders, Audit audit, TimeProvider clock)
    : ICommandHandler<CancelOrderCommand, OrderStatus>
{
    public async Task<Result<OrderStatus>> HandleAsync(CancelOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(ClientOrderId.From(command.ClientOrderId), cancellationToken);
        if (order is null)
            return TradingErrors.OrderNotFound(command.ClientOrderId);
        if (order.IsFinal)
            return TradingErrors.OrderNotCancellable(command.ClientOrderId, order.Status);

        await audit.RecordAsync(AuditCategory.Order, "CancelRequested", $"Cancel requested while {order.Status}.", order.Id.Value, order.Symbol.Value);

        if (order.BrokerOrderId is null)
        {
            // Never confirmed as submitted: ask the broker whether it has it.
            var reported = await broker.GetOrderAsync(order.Id, cancellationToken);
            if (reported is null)
            {
                order.Cancel(clock.GetUtcNow());
                return order.Status;
            }

            OrderStateSync.Apply(order, reported, clock.GetUtcNow());
            if (order.IsFinal)
                return order.Status;
        }

        try
        {
            await broker.CancelOrderAsync(order.BrokerOrderId!, cancellationToken);
        }
        catch (BrokerException ex) when (ex.IsRejection)
        {
            // Typically already filled or cancelled: the broker's own state settles it.
            if (await broker.GetOrderAsync(order.Id, cancellationToken) is { } reported)
                OrderStateSync.Apply(order, reported, clock.GetUtcNow());
            return order.IsFinal ? order.Status : TradingErrors.BrokerRejected(ex.Message);
        }

        return order.Status;
    }
}

/// <summary>
/// Closes the position in one symbol with a market order. Open orders in the symbol are cancelled first: they may
/// hold the shares the closing order needs.
/// </summary>
public sealed record ClosePositionCommand(string Symbol) : ICommand<PlacedOrder>
{
    public static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(5);
}

internal sealed class ClosePositionCommandValidator : AbstractValidator<ClosePositionCommand>
{
    public ClosePositionCommandValidator() => RuleFor(c => c.Symbol).MustBeSymbol();
}

internal sealed class ClosePositionCommandHandler(IBrokerGateway broker, IOrderRepository orders, IDispatcher dispatcher, TimeProvider clock)
    : ICommandHandler<ClosePositionCommand, PlacedOrder>
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public async Task<Result<PlacedOrder>> HandleAsync(ClosePositionCommand command, CancellationToken cancellationToken)
    {
        var symbol = Symbol.Create(command.Symbol);
        var position = (await broker.GetPositionsAsync(cancellationToken)).FirstOrDefault(p => p.Symbol == symbol.Value);
        if (position is null || position.Quantity == 0)
            return TradingErrors.NoPosition(symbol.Value);

        var working = (await orders.ListUnfinishedAsync(cancellationToken)).Where(o => o.Symbol == symbol).Select(o => o.Id).ToList();
        foreach (var id in working)
            await dispatcher.SendAsync(new CancelOrderCommand(id.Value), cancellationToken);
        await WaitUntilClosedAsync(working, cancellationToken);

        var side = position.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy;
        return await dispatcher.SendAsync(
            new PlaceOrderCommand(symbol.Value, side, OrderType.Market, Math.Abs(position.Quantity)), cancellationToken);
    }

    private async Task WaitUntilClosedAsync(IReadOnlyList<ClientOrderId> ids, CancellationToken cancellationToken)
    {
        var deadline = clock.GetUtcNow() + ClosePositionCommand.CancelTimeout;
        var pending = ids.ToList();
        while (pending.Count > 0 && clock.GetUtcNow() < deadline)
        {
            var states = await Task.WhenAll(pending.Select(id => broker.GetOrderAsync(id, cancellationToken)));
            pending = pending.Where((_, i) => states[i] is { Status: OrderStatus.Submitted or OrderStatus.PartiallyFilled }).ToList();
            if (pending.Count > 0)
                await Task.Delay(PollInterval, clock, cancellationToken);
        }
    }
}

/// <summary>Cancels every open order and closes every position, all symbols at the same time.</summary>
public sealed record CloseAllPositionsCommand : ICommand<CloseAllResult>;

/// <param name="Failures">Symbol and reason for each position that could not be closed.</param>
public sealed record CloseAllResult(IReadOnlyList<PlacedOrder> Orders, IReadOnlyList<string> Failures);

internal sealed class CloseAllPositionsCommandHandler(
    IBrokerGateway broker,
    IOrderRepository orders,
    IDispatcher dispatcher,
    ILogger<CloseAllPositionsCommandHandler> logger) : ICommandHandler<CloseAllPositionsCommand, CloseAllResult>
{
    public async Task<Result<CloseAllResult>> HandleAsync(CloseAllPositionsCommand command, CancellationToken cancellationToken)
    {
        var positions = await broker.GetPositionsAsync(cancellationToken);
        var held = positions.Select(p => p.Symbol).ToHashSet();

        // Orders in symbols without a position are cancelled here; ClosePosition cancels the rest itself.
        foreach (var order in (await orders.ListUnfinishedAsync(cancellationToken)).Where(o => !held.Contains(o.Symbol.Value)))
            await dispatcher.SendAsync(new CancelOrderCommand(order.Id.Value), cancellationToken);

        // Each symbol in its own scope, all at once; the broker client's rate limiter paces the requests.
        var results = await Task.WhenAll(positions.Select(p => dispatcher.SendAsync(new ClosePositionCommand(p.Symbol), cancellationToken)));

        var placed = results.Where(r => r.IsSuccess).Select(r => r.Value).ToList();
        var failures = positions.Zip(results)
            .Where(x => x.Second.IsFailure)
            .Select(x => $"{x.First.Symbol}: {x.Second.Error!.Message}")
            .ToList();
        foreach (var failure in failures)
            logger.LogWarning("Could not close position {Failure}", failure);

        return new CloseAllResult(placed, failures);
    }
}

/// <summary>Applies one event of the broker's trade update stream. Repeated or stale events change nothing.</summary>
/// <returns>Whether the order changed; <c>false</c> also for orders the app did not place.</returns>
public sealed record ApplyTradeUpdateCommand(TradeUpdate Update) : ICommand<bool>;

internal sealed class ApplyTradeUpdateCommandHandler(IOrderRepository orders) : ICommandHandler<ApplyTradeUpdateCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(ApplyTradeUpdateCommand command, CancellationToken cancellationToken)
    {
        if (!ClientOrderId.IsValid(command.Update.Order.ClientOrderId))
            return false;

        var order = await orders.GetAsync(ClientOrderId.From(command.Update.Order.ClientOrderId!), cancellationToken);
        return order is not null && OrderStateSync.Apply(order, command.Update.Order, command.Update.OccurredAt);
    }
}

/// <summary>
/// Compares every unfinished order with the broker and catches up on anything the stream missed, e.g. while
/// disconnected or closed. An order the broker has never seen is given up once it is older than
/// <see cref="LostAfter"/>.
/// </summary>
public sealed record ReconcileOrdersCommand : ICommand<ReconciliationResult>
{
    public static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(2);
}

public sealed record ReconciliationResult(int Checked, int Changed);

internal sealed class ReconcileOrdersCommandHandler(IBrokerGateway broker, IOrderRepository orders, TimeProvider clock)
    : ICommandHandler<ReconcileOrdersCommand, ReconciliationResult>
{
    public async Task<Result<ReconciliationResult>> HandleAsync(ReconcileOrdersCommand command, CancellationToken cancellationToken)
    {
        var unfinished = await orders.ListUnfinishedAsync(cancellationToken);
        var changed = 0;

        foreach (var order in unfinished)
        {
            var now = clock.GetUtcNow();
            var reported = await broker.GetOrderAsync(order.Id, cancellationToken);
            if (reported is not null)
            {
                if (OrderStateSync.Apply(order, reported, now))
                    changed++;
            }
            else if (order.Status == OrderStatus.New && now - order.CreatedAt > ReconcileOrdersCommand.LostAfter)
            {
                order.Reject("The order never reached the broker.", now);
                changed++;
            }
        }

        // Saved by UnitOfWorkBehavior.
        return new ReconciliationResult(unfinished.Count, changed);
    }
}
