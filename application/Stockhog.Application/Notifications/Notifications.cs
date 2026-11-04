using Stockhog.Application.Abstractions.Events;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Notifications;

/// <summary>What happened, so the UI can word it in the user's language.</summary>
public enum NoticeTopic
{
    /// <summary>Args: side, quantity, symbol, average price.</summary>
    OrderFilled,

    /// <summary>Args: symbol, reason.</summary>
    OrderRejected,

    /// <summary>Args: loss in USD, limit in USD.</summary>
    HaltedOnDailyLoss,

    /// <summary>Args: drawdown in %, limit in %.</summary>
    HaltedOnDrawdown,

    /// <summary>Args: buys, sells.</summary>
    AutopilotTraded
}

/// <summary>Something the user wants to hear about even when the app is in the background.</summary>
public sealed record Notice(NoticeTopic Topic, IReadOnlyList<object> Args)
{
    public bool IsWarning => Topic is NoticeTopic.OrderRejected or NoticeTopic.HaltedOnDailyLoss or NoticeTopic.HaltedOnDrawdown;
}

/// <summary>
/// Shows notices outside the app window, e.g. as Windows notifications. Must return at once and never throw: telling
/// the user must not hold up or break trading.
/// </summary>
public interface INotifier
{
    void Notify(Notice notice);
}

/// <summary>Fills and rejections arrive in the background, often while the user looks elsewhere.</summary>
internal sealed class NotifyOrderEvents(INotifier notifier) : IDomainEventHandler<OrderFilled>, IDomainEventHandler<OrderClosed>
{
    public Task HandleAsync(OrderFilled e, CancellationToken cancellationToken)
    {
        notifier.Notify(new Notice(NoticeTopic.OrderFilled, [e.Side, e.Quantity, e.Symbol.Value, e.AveragePrice]));
        return Task.CompletedTask;
    }

    public Task HandleAsync(OrderClosed e, CancellationToken cancellationToken)
    {
        if (e.Status == OrderStatus.Rejected)
            notifier.Notify(new Notice(NoticeTopic.OrderRejected, [e.Symbol.Value, e.Reason ?? ""]));
        return Task.CompletedTask;
    }
}
