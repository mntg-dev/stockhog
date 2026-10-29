using Stockhog.Domain.Common;

namespace Stockhog.Application.Abstractions.Events;

public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Delivers domain events to every registered <see cref="IDomainEventHandler{TEvent}"/>, in the caller's DI scope.
/// </summary>
public interface IDomainEventPublisher
{
    Task PublishAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default);
}

public static class DomainEventPublisherExtensions
{
    /// <summary>
    /// Publishes and clears the events recorded by an aggregate.
    /// </summary>
    public static Task PublishEventsOfAsync(this IDomainEventPublisher publisher, IHasDomainEvents aggregate, CancellationToken cancellationToken = default) =>
        publisher.PublishAsync(aggregate.DequeueDomainEvents(), cancellationToken);
}
