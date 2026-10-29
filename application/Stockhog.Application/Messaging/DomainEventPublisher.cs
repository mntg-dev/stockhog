using System.Collections.Concurrent;
using Stockhog.Application.Abstractions.Events;
using Stockhog.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Stockhog.Application.Messaging;

/// <summary>
/// Scoped: handlers are resolved from the same scope as the command that raised the events.
/// Handlers run one after another; an exception stops publishing and reaches the caller.
/// </summary>
internal sealed class DomainEventPublisher(IServiceProvider services) : IDomainEventPublisher
{
    private static readonly ConcurrentDictionary<Type, DomainEventHandlerWrapper> Wrappers = new();

    public async Task PublishAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        foreach (var domainEvent in domainEvents)
        {
            var wrapper = Wrappers.GetOrAdd(domainEvent.GetType(), static type =>
                (DomainEventHandlerWrapper)Activator.CreateInstance(typeof(DomainEventHandlerWrapper<>).MakeGenericType(type))!);

            await wrapper.HandleAsync(domainEvent, services, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal abstract class DomainEventHandlerWrapper
{
    public abstract Task HandleAsync(IDomainEvent domainEvent, IServiceProvider services, CancellationToken cancellationToken);
}

internal sealed class DomainEventHandlerWrapper<TEvent> : DomainEventHandlerWrapper
    where TEvent : IDomainEvent
{
    public override async Task HandleAsync(IDomainEvent domainEvent, IServiceProvider services, CancellationToken cancellationToken)
    {
        foreach (var handler in services.GetServices<IDomainEventHandler<TEvent>>())
            await handler.HandleAsync((TEvent)domainEvent, cancellationToken).ConfigureAwait(false);
    }
}
