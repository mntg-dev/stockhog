using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Common;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;
using FluentValidation;

namespace Stockhog.Application.Trading.Orders;

/// <summary>
/// Checks the order against the risk limits, saves it, then submits it to the broker. The order is saved before the
/// submit, so if the app dies or the connection drops mid-submit, reconciliation finds out what happened.
/// </summary>
public sealed record PlaceOrderCommand(
    string Symbol,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    decimal? LimitPrice = null,
    decimal? StopPrice = null,
    TimeInForce TimeInForce = TimeInForce.Day) : ICommand<PlacedOrder>
{
    public const decimal MaxQuantity = 1_000_000;
}

public sealed record PlacedOrder(string ClientOrderId, string? BrokerOrderId, OrderStatus Status, decimal FilledQuantity, decimal? AverageFillPrice);

internal sealed class PlaceOrderCommandValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderCommandValidator()
    {
        RuleFor(c => c.Symbol).MustBeSymbol();
        RuleFor(c => c.Side).IsInEnum();
        RuleFor(c => c.Type).IsInEnum();
        RuleFor(c => c.TimeInForce).IsInEnum();
        RuleFor(c => c.Quantity)
            .GreaterThan(0).WithMessage("Quantity must be positive.")
            .LessThanOrEqualTo(PlaceOrderCommand.MaxQuantity).WithMessage($"Quantity must be at most {PlaceOrderCommand.MaxQuantity:N0}.")
            .Must(q => q == decimal.Round(q, 9)).WithMessage("Quantity has at most 9 decimal places.");
        RuleFor(c => c.Quantity)
            .Must(q => q == decimal.Truncate(q)).When(c => c.Type != OrderType.Market || c.TimeInForce != TimeInForce.Day)
            .WithMessage("Fractional quantities are allowed only for market day orders.");

        RuleFor(c => c.LimitPrice)
            .NotNull().WithMessage("A limit price above zero is required.")
            .GreaterThan(0).WithMessage("A limit price above zero is required.")
            .When(c => c.Type is OrderType.Limit or OrderType.StopLimit);
        RuleFor(c => c.LimitPrice).Null().When(c => c.Type is OrderType.Market or OrderType.Stop)
            .WithMessage("This order type has no limit price.");
        RuleFor(c => c.StopPrice)
            .NotNull().WithMessage("A stop price above zero is required.")
            .GreaterThan(0).WithMessage("A stop price above zero is required.")
            .When(c => c.Type is OrderType.Stop or OrderType.StopLimit);
        RuleFor(c => c.StopPrice).Null().When(c => c.Type is OrderType.Market or OrderType.Limit)
            .WithMessage("This order type has no stop price.");
    }
}

internal sealed class PlaceOrderCommandHandler(
    IBrokerGateway broker,
    IOrderRepository orders,
    IUnitOfWork unitOfWork,
    IRiskLimitsStore riskLimits,
    IQuoteCache quotes,
    ILatestPriceSource latestPrices,
    ITradingEnvironment environment,
    LiveTradingSession liveSession,
    Audit audit,
    TimeProvider clock) : ICommandHandler<PlaceOrderCommand, PlacedOrder>
{
    public async Task<Result<PlacedOrder>> HandleAsync(PlaceOrderCommand command, CancellationToken cancellationToken)
    {
        var symbol = Symbol.Create(command.Symbol);
        var intent = $"{command.Side} {command.Quantity} {symbol} {command.Type}";

        if (environment.Mode == TradingMode.Live && !liveSession.IsArmed)
        {
            await audit.RecordAsync(AuditCategory.Risk, "Refused", $"{intent}: live trading is not armed.", symbol: symbol.Value);
            return SafetyErrors.LiveNotArmed();
        }

        // The broker's account and positions and the price are independent: fetch them at the same time.
        var accountTask = broker.GetAccountAsync(cancellationToken);
        var positionsTask = broker.GetPositionsAsync(cancellationToken);
        var priceTask = ReferencePriceAsync(command, symbol, cancellationToken);
        var unfinished = await orders.ListUnfinishedAsync(cancellationToken);
        try
        {
            await Task.WhenAll(accountTask, positionsTask, priceTask);
        }
        catch (BrokerException ex)
        {
            return TradingErrors.BrokerUnavailable(ex.Message);
        }

        if (await priceTask is not { } price)
            return TradingErrors.NoPrice(symbol.Value);

        var account = await accountTask;
        var position = (await positionsTask).FirstOrDefault(p => p.Symbol == symbol.Value)?.Quantity ?? 0;
        var context = new RiskContext(position, account.DailyProfitLoss, unfinished.Count, account.BuyingPower);
        var decision = RiskPolicy.Evaluate(new OrderIntent(symbol, command.Side, command.Quantity, price), context, riskLimits.Current);

        var violations = decision.Violations.ToList();
        if (account.TradingBlocked)
            violations.Add("The broker has blocked trading on this account.");
        if (violations.Count > 0)
        {
            await audit.RecordAsync(AuditCategory.Risk, "Refused", $"{intent}: {string.Join(" ", violations)}", symbol: symbol.Value);
            return TradingErrors.RiskRejected(violations);
        }

        var now = clock.GetUtcNow();
        var order = Order.Create(ClientOrderId.New(now), symbol, command.Side, command.Type, command.Quantity,
            command.LimitPrice, command.StopPrice, command.TimeInForce, now);
        orders.Add(order);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // From here on the submit is not cancelled by the caller: a half-sent order is worse than a late answer.
        BrokerOrder accepted;
        try
        {
            accepted = await broker.SubmitOrderAsync(order, CancellationToken.None);
            OrderStateSync.Apply(order, accepted, clock.GetUtcNow());
        }
        catch (BrokerException ex) when (ex.IsRejection)
        {
            order.Reject(ex.Message, clock.GetUtcNow());
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            return TradingErrors.BrokerRejected(ex.Message);
        }
        catch (BrokerException ex)
        {
            // Unknown outcome: the order stays New until reconciliation asks the broker about it.
            await audit.RecordAsync(AuditCategory.Order, "SubmitUnknown", ex.Message, order.Id.Value, symbol.Value);
            return TradingErrors.SubmitUnknown(order.Id.Value, ex.Message);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        catch (ConcurrencyConflictException)
        {
            // The trade update stream reported (and saved) this order before the submit returned. Its state comes
            // from the broker too and may be newer, e.g. already filled, so it is kept rather than overwritten.
            return new PlacedOrder(order.Id.Value, accepted.BrokerOrderId, accepted.Status, accepted.FilledQuantity, accepted.AverageFillPrice);
        }

        return new PlacedOrder(order.Id.Value, order.BrokerOrderId, order.Status, order.FilledQuantity, order.AverageFillPrice);
    }

    /// <summary>The limit (or stop) price, else the live quote, else the latest trade from the data API.</summary>
    private async Task<decimal?> ReferencePriceAsync(PlaceOrderCommand command, Symbol symbol, CancellationToken cancellationToken)
    {
        if ((command.LimitPrice ?? command.StopPrice) is { } given)
            return given;
        if (quotes.TryGet(symbol, out var quote) && quote.Price is { } live)
            return live;

        return await latestPrices.GetLatestPriceAsync(symbol, cancellationToken);
    }
}

/// <summary>Moves an order to the state the broker reports. Shared by trade updates and reconciliation.</summary>
internal static class OrderStateSync
{
    /// <returns><c>true</c> if the order changed.</returns>
    public static bool Apply(Order order, BrokerOrder reported, DateTimeOffset at)
    {
        var changed = false;
        if (order.Status == OrderStatus.New && reported.Status != OrderStatus.Rejected)
        {
            order.MarkSubmitted(reported.BrokerOrderId, at);
            changed = true;
        }

        // A cancelled order may have filled partly: apply the final fill before closing it.
        if (reported.FilledQuantity > 0 && reported.AverageFillPrice is { } price)
            changed |= order.ApplyFill(reported.FilledQuantity, price, at);

        changed |= reported.Status switch
        {
            OrderStatus.Cancelled => order.Cancel(at),
            OrderStatus.Expired => order.Expire(at),
            OrderStatus.Rejected => order.Reject(reported.Reason ?? "Rejected by the broker.", at),
            _ => false
        };

        return changed;
    }
}
